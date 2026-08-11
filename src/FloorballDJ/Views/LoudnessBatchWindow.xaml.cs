using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using FloorballDJ.Models;
using FloorballDJ.Services;
using FloorballDJ.ViewModels;

namespace FloorballDJ.Views;

public partial class LoudnessBatchWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AudioAnalysisService _analysis = new();
    private CancellationTokenSource? _cancellation;
    public ObservableCollection<LoudnessBatchItem> Items { get; } = [];

    public LoudnessBatchWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        WindowPlacementService.MaximizeOnOwnerMonitor(this);
        _viewModel = viewModel;
        TargetBox.Text = viewModel.Settings.DefaultLoudnessTargetLufs.ToString("0.#", CultureInfo.InvariantCulture);
        PeakBox.Text = viewModel.Settings.MasterLimiterCeilingDbtp.ToString("0.#", CultureInfo.InvariantCulture);
        foreach (var deck in viewModel.Decks)
            foreach (var jingle in deck.Jingles.Where(item => item.HasAudio && File.Exists(item.FilePath)))
            {
                var item = new LoudnessBatchItem(deck.Name, jingle);
                item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(LoudnessBatchItem.IsSelected)) UpdateSummary(); };
                Items.Add(item);
            }
        DataContext = this;
        UpdateSummary();
        Closed += (_, _) => _cancellation?.Cancel();
    }

    private async void AnalyzeOnly_Click(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void AnalyzeAndBalance_Click(object sender, RoutedEventArgs e) => await RunAsync(true);
    private void CancelAnalysis_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var item in Items) item.IsSelected = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var item in Items) item.IsSelected = false; }

    private async Task RunAsync(bool enableNormalization)
    {
        if (_cancellation is not null) return;
        var selectedItems = Items.Where(item => item.IsSelected).ToArray();
        if (selectedItems.Length == 0)
        {
            MessageBox.Show(this, "Markera minst en ljudfil som ska analyseras.", "Inga ljudfiler valda", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var target = Parse(TargetBox.Text, -16, -30, 0);
        var peak = Parse(PeakBox.Text, -1, -12, 0);
        TargetBox.Text = target.ToString("0.#", CultureInfo.InvariantCulture);
        PeakBox.Text = peak.ToString("0.#", CultureInfo.InvariantCulture);
        _viewModel.Settings.DefaultLoudnessTargetLufs = target;
        _viewModel.Settings.MasterLimiterCeilingDbtp = peak;
        _cancellation = new CancellationTokenSource();
        CancelAnalysisButton.IsEnabled = true;
        FilesList.IsEnabled = SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = false;
        Progress.Maximum = Math.Max(1, selectedItems.Length);
        var cache = new Dictionary<string, LoudnessAnalysis>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var index = 0; index < selectedItems.Length; index++)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                var item = selectedItems[index];
                item.Status = "Analyserar…";
                ProgressText.Text = $"{index + 1} av {selectedItems.Length}: {item.Title}";
                var key = $"{item.Jingle.FilePath}|{item.Jingle.StartSeconds:0.###}|{item.Jingle.EndSeconds:0.###}";
                if (!cache.TryGetValue(key, out var result))
                {
                    result = await _analysis.AnalyzeAsync(item.Jingle.FilePath, item.Jingle.StartSeconds, item.Jingle.EndSeconds, _cancellation.Token);
                    cache[key] = result;
                }
                Apply(item.Jingle, result, target, peak, enableNormalization);
                item.Refresh();
                item.Status = enableNormalization ? "Balanserad" : "Analyserad";
                Progress.Value = index + 1;
            }
            _viewModel.NotifyJingleChanged();
            await _viewModel.SaveAsync();
            ProgressText.Text = enableNormalization ? "Valda ljudfiler har analyserats och balanserats." : "Analysen av valda filer är klar.";
        }
        catch (OperationCanceledException) { ProgressText.Text = "Analysen avbröts. Redan analyserade filer behålls."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Loudnessanalysen avbröts", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally
        {
            _cancellation.Dispose(); _cancellation = null; CancelAnalysisButton.IsEnabled = false;
            FilesList.IsEnabled = SelectAllButton.IsEnabled = SelectNoneButton.IsEnabled = true;
            UpdateSummary();
        }
    }

    private static void Apply(Jingle jingle, LoudnessAnalysis result, double target, double peak, bool enable)
    {
        jingle.IntegratedLufs = result.IntegratedLufs; jingle.TruePeakDbtp = result.TruePeakDbtp;
        jingle.LoudnessRangeLu = result.LoudnessRangeLu; jingle.MaxMomentaryLufs = result.MaxMomentaryLufs;
        jingle.AnalysisFileSize = result.FileSize; jingle.AnalysisFileWriteUtcTicks = result.FileWriteUtcTicks;
        jingle.NormalizationTargetLufs = target; jingle.NormalizationGainDb = result.SuggestedGain(target, peak);
        if (enable) jingle.NormalizationEnabled = true;
    }

    private void UpdateSummary()
    {
        var analyzed = Items.Count(item => item.Jingle.HasFreshLoudnessAnalysis);
        var selected = Items.Count(item => item.IsSelected);
        SummaryText.Text = $"{Items.Count} ljud • {selected} valda • {analyzed} aktuella analyser";
    }

    private static double Parse(string text, double fallback, double min, double max) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max) : fallback;
}

public sealed class LoudnessBatchItem : INotifyPropertyChanged
{
    private string _status;
    private bool _isSelected = true;
    public LoudnessBatchItem(string deck, Jingle jingle) { Deck = deck; Jingle = jingle; _status = jingle.HasFreshLoudnessAnalysis ? "Aktuell" : "Ej analyserad"; }
    public string Deck { get; }
    public Jingle Jingle { get; }
    public string Title => Jingle.Title;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Raise(); } }
    public string Lufs => Jingle.IntegratedLufs?.ToString("0.0", CultureInfo.InvariantCulture) ?? "–";
    public string Peak => Jingle.TruePeakDbtp is double value ? $"{value:0.0} dBTP" : "–";
    public string Gain => Jingle.IntegratedLufs is null ? "–" : $"{Jingle.NormalizationGainDb:+0.0;-0.0;0.0} dB";
    public string Status { get => _status; set { if (_status == value) return; _status = value; Raise(); } }
    public void Refresh() { Raise(nameof(Lufs)); Raise(nameof(Peak)); Raise(nameof(Gain)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
