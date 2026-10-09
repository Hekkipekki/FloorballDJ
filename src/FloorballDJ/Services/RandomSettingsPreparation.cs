using System.Globalization;
using System.Text;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

internal sealed record RandomSettingsJingleSource(Guid Id, string Title, string Path, double Duration, double Start, double? End);
internal sealed record RandomSettingsDeckSource(Guid Id, string Name, RandomSettingsJingleSource[] Jingles);
internal sealed record RandomSettingsLibraryItem(Guid Id, string Title, string SearchText, string NormalizedSearchText, double Duration, int Order);
internal sealed record RandomSettingsLibraryDeck(Guid Id, string Name, RandomSettingsLibraryItem[] Jingles);
internal sealed record RandomSettingsLibrary(RandomSettingsLibraryDeck[] Decks, int FileChecks, int AvailableItems);

/// <summary>Owned scalar inputs only; no workers read live project/UI models.</summary>
internal sealed record RandomSettingsLibraryInput(RandomSettingsDeckSource[] Decks)
{
    internal static RandomSettingsLibraryInput Capture(FloorballProject project)
    {
        using var duration = PerformanceDiagnostics.BeginOperation("RandomSettingsSnapshotRequested").Measure("RandomSettingsSnapshot");
        return new(project.Decks.Select(deck => new RandomSettingsDeckSource(deck.Id, deck.Name,
            deck.Jingles.Select(item => new RandomSettingsJingleSource(item.Id, item.Title, item.FilePath,
                item.DurationSeconds, item.StartSeconds, item.EndSeconds)).ToArray())).ToArray());
    }

    internal bool Matches(FloorballProject project)
    {
        if (project.Decks.Count != Decks.Length) return false;
        for (var d = 0; d < Decks.Length; d++)
        {
            var source = Decks[d]; var current = project.Decks[d];
            if (source.Id != current.Id || source.Name != current.Name || source.Jingles.Length != current.Jingles.Count) return false;
            for (var j = 0; j < source.Jingles.Length; j++)
            {
                var saved = source.Jingles[j]; var item = current.Jingles[j];
                if (saved.Id != item.Id || saved.Title != item.Title || saved.Path != item.FilePath ||
                    !saved.Duration.Equals(item.DurationSeconds) || !saved.Start.Equals(item.StartSeconds) || saved.End != item.EndSeconds) return false;
            }
        }
        return true;
    }
}

internal sealed class RandomSettingsPreparation
{
    internal static RandomSettingsPreparation Shared { get; } = new(File.Exists);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, bool> _fileExists;
    internal RandomSettingsPreparation(Func<string, bool> fileExists) => _fileExists = fileExists;

    internal async Task<RandomSettingsLibrary> PrepareAsync(RandomSettingsLibraryInput input, CancellationToken token, PerformanceOperation opening = default)
    {
        var operation = opening.Enabled ? opening : PerformanceDiagnostics.BeginOperation("RandomSettingsPreparationRequested");
        if (opening.Enabled) operation.Mark("RandomSettingsPreparationRequested");
        using (operation.Measure("RandomSettingsPreparationQueueWait")) await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var total = operation.Measure("RandomSettingsLibraryPreparation");
                var exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                var decks = new List<RandomSettingsLibraryDeck>();
                var available = 0;
                foreach (var deck in input.Decks)
                {
                    token.ThrowIfCancellationRequested();
                    var items = new List<RandomSettingsLibraryItem>();
                    foreach (var item in deck.Jingles)
                    {
                        token.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(item.Path)) continue;
                        if (!exists.TryGetValue(item.Path, out var valid))
                        {
                            using (operation.Measure("RandomSettingsFileProbe")) valid = _fileExists(item.Path);
                            exists.Add(item.Path, valid);
                        }
                        if (!valid) continue;
                        var fileName = Path.GetFileNameWithoutExtension(item.Path);
                        var search = $"{item.Title} {fileName}";
                        items.Add(new(item.Id, string.IsNullOrWhiteSpace(item.Title) ? fileName : item.Title,
                            search, NormalizeSearchText(search), Math.Max(0, item.End.GetValueOrDefault(item.Duration) - item.Start), items.Count));
                    }
                    available += items.Count;
                    if (items.Count > 0) decks.Add(new(deck.Id, deck.Name, items.ToArray()));
                }
                token.ThrowIfCancellationRequested();
                operation.Mark("RandomSettingsFileChecks", exists.Count);
                operation.Mark("RandomSettingsAvailableItems", available);
                return new RandomSettingsLibrary(decks.ToArray(), exists.Count, available);
            }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
