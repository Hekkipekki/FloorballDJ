using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FloorballDJ.Models;

namespace FloorballDJ.Views;

public partial class TeamDeckWindow : Window, INotifyPropertyChanged
{
    private readonly Action<Jingle, double?, double?, double?> _play;
    private readonly Action? _orderChanged;
    private readonly TeamDeckProfile _team;
    private readonly Jingle? _defaultJingle;
    private readonly Func<PlaybackSnapshot>? _playbackSnapshot;
    private readonly DispatcherTimer _transitionTimer;
    private Point _dragStart;
    private TeamPlayerButton? _dragSource;
    private TeamPlayerButton? _selectedPlayer;
    private bool _suppressNextClick;
    private bool _transitionStarted;
    private string _statusText = "Välj spelare när lagjingeln har startat";

    public Guid TeamId { get; }
    public string TeamName { get; }
    public bool HasDefaultJingle => _defaultJingle is not null;
    public ObservableCollection<TeamPlayerButton> Players { get; }
    public string StatusText { get => _statusText; private set { if (_statusText == value) return; _statusText = value; Raise(); } }

    public TeamDeckWindow(TeamDeckProfile team, FloorballProject project,
        Action<Jingle, double?, double?, double?> play, Action? orderChanged = null,
        Func<PlaybackSnapshot>? playbackSnapshot = null)
    {
        _team = team;
        TeamId = team.Id;
        TeamName = team.Name;
        _play = play;
        _orderChanged = orderChanged;
        _playbackSnapshot = playbackSnapshot;
        var jingles = project.Decks.SelectMany(deck => deck.Jingles).Where(jingle => jingle.HasAudio)
            .GroupBy(jingle => jingle.Id).ToDictionary(group => group.Key, group => group.First());
        if (team.DefaultJingleId is Guid defaultId) jingles.TryGetValue(defaultId, out _defaultJingle);
        Players = new ObservableCollection<TeamPlayerButton>((team.Players ?? []).Select(player =>
        {
            Jingle? jingle = null;
            if (player.JingleId is Guid jingleId) jingles.TryGetValue(jingleId, out jingle);
            return new TeamPlayerButton(player, jingle);
        }));
        InitializeComponent();
        DataContext = this;
        _transitionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _transitionTimer.Tick += (_, _) => TryStartSelectedPlayer();
        _transitionTimer.Start();
        Closed += (_, _) => _transitionTimer.Stop();
    }

    private void PlayDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_defaultJingle is null) return;
        ClearPlayerSelection();
        _transitionStarted = false;
        // Lagets förvalda goal horn/måljingle ska slå an direkt. Ljudmotorn
        // behåller endast sin ohörbart korta de-click-ramp.
        _play(_defaultJingle, null, 0, null);
        StatusText = $"Standardjingle: {_defaultJingle.Title} – välj spelare";
    }

    private void Player_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressNextClick) { _suppressNextClick = false; return; }
        if ((sender as FrameworkElement)?.DataContext is not TeamPlayerButton player || player.Jingle is null) return;
        var snapshot = _playbackSnapshot?.Invoke();
        if (_defaultJingle is not null && snapshot?.JingleId == _defaultJingle.Id && !_transitionStarted)
        {
            if (ReferenceEquals(_selectedPlayer, player))
            {
                ClearPlayerSelection();
                StatusText = "Ingen spelare vald – lagjingeln fortsätter";
                return;
            }
            SelectPlayer(player);
            StatusText = $"Väntar: #{player.Number} {player.Name} – automatisk övergång";
            TryStartSelectedPlayer();
            return;
        }

        SelectPlayer(player);
        _transitionStarted = true;
        _play(player.Jingle, player.StartSecondsOverride, _team.PlayerFadeInSeconds, _team.DefaultFadeOutSeconds);
        StatusText = $"Spelar: #{player.Number} {player.Name} · {player.JingleTitle}";
    }

    private void TryStartSelectedPlayer()
    {
        if (_transitionStarted || _selectedPlayer?.Jingle is null || _defaultJingle is null || _playbackSnapshot is null) return;
        var snapshot = _playbackSnapshot();
        if (snapshot.JingleId != _defaultJingle.Id || !snapshot.IsPlaying || snapshot.Duration <= TimeSpan.Zero) return;
        var transitionAt = _team.TransitionAtSeconds ?? Math.Max(0, snapshot.Duration.TotalSeconds - _team.DefaultFadeOutSeconds);
        if (snapshot.Position.TotalSeconds + .02 < transitionAt) return;
        _transitionStarted = true;
        _play(_selectedPlayer.Jingle, _selectedPlayer.StartSecondsOverride,
            _team.PlayerFadeInSeconds, _team.DefaultFadeOutSeconds);
        StatusText = $"Övergång: #{_selectedPlayer.Number} {_selectedPlayer.Name} · {_selectedPlayer.JingleTitle}";
    }

    private void SelectPlayer(TeamPlayerButton player)
    {
        foreach (var item in Players) item.IsSelected = ReferenceEquals(item, player);
        _selectedPlayer = player;
    }

    private void ClearPlayerSelection()
    {
        foreach (var item in Players) item.IsSelected = false;
        _selectedPlayer = null;
    }

    private void Player_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _suppressNextClick = false;
        _dragStart = e.GetPosition(this);
        _dragSource = (sender as FrameworkElement)?.DataContext as TeamPlayerButton;
    }

    private void Player_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource is null) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _suppressNextClick = true;
        DragDrop.DoDragDrop((DependencyObject)sender, _dragSource, DragDropEffects.Move);
    }

    private void Player_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is not Button button || !e.Data.GetDataPresent(typeof(TeamPlayerButton))) return;
        if (button.DataContext is TeamPlayerButton player) player.IsDropTarget = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void Player_DragLeave(object sender, DragEventArgs e) => ResetDropTarget(sender as Button);

    private void Player_Drop(object sender, DragEventArgs e)
    {
        var targetButton = sender as Button;
        ResetDropTarget(targetButton);
        if (targetButton?.DataContext is not TeamPlayerButton target ||
            e.Data.GetData(typeof(TeamPlayerButton)) is not TeamPlayerButton source || source == target) return;
        var oldIndex = Players.IndexOf(source);
        var targetIndex = Players.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0) return;
        Players.Move(oldIndex, targetIndex);
        _team.Players = Players.Select(player => player.Source).ToList();
        _orderChanged?.Invoke();
        StatusText = $"Flyttade #{source.Number} {source.Name} till plats {targetIndex + 1}";
        e.Handled = true;
    }

    private static void ResetDropTarget(Button? button)
    {
        if (button?.DataContext is TeamPlayerButton player) player.IsDropTarget = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class TeamPlayerButton : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDropTarget;
    public TeamDeckPlayer Source { get; }
    public string Number { get; }
    public string Name { get; }
    public string JingleTitle { get; }
    public bool HasJingle => Jingle is not null;
    public Jingle? Jingle { get; }
    public double? StartSecondsOverride => Source.StartSecondsOverride;
    public Brush Background { get; }
    public Brush Foreground { get; }
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; RaiseVisual(); } }
    public bool IsDropTarget { get => _isDropTarget; set { if (_isDropTarget == value) return; _isDropTarget = value; RaiseVisual(); } }
    public Brush BorderBrush => ParseBrush(IsSelected || IsDropTarget ? "#35E0C1" : "#4B6B8B", "#4B6B8B");
    public Thickness BorderThickness => new(IsSelected ? 4 : IsDropTarget ? 3 : 1);

    public TeamPlayerButton(TeamDeckPlayer player, Jingle? jingle)
    {
        Source = player;
        Number = player.Number;
        Name = player.Name;
        Jingle = jingle;
        JingleTitle = jingle is null ? "Ingen jingle vald" : jingle.Title;
        Background = ParseBrush(player.ButtonColor, "#17304A");
        Foreground = ParseBrush(player.TextColor, "#F3F7FC");
    }

    private static Brush ParseBrush(string value, string fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return (Brush)new BrushConverter().ConvertFromString(fallback)!; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void RaiseVisual()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BorderThickness)));
    }
}
