using System.Globalization;
using System.Text;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

internal sealed record AudioLibrarySource(string DeckName, string Title, string FilePath);
internal sealed record AudioLibraryStatistics(AudioFileStatus[] Files, long Bytes, string Formats);
internal sealed record AudioRelinkMatch(int SourceIndex, string FilePath, double? DurationSeconds);

internal static class AudioLibraryService
{
    internal static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".flac", ".mp4", ".ogg" };

    internal static Task<AudioLibraryStatistics> InspectAsync(AudioLibrarySource[] sources, CancellationToken token = default)
    {
        var snapshot = sources.ToArray();
        return Task.Run(() =>
        {
            var performance = PerformanceDiagnostics.BeginOperation("LibraryInspectionRequested");
            using var total = performance.Measure("LibraryInspection");
            var statusByPath = new Dictionary<string, (bool Exists, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            var files = new List<AudioFileStatus>(snapshot.Length);
            long bytes = 0;
            foreach (var source in snapshot)
            {
                token.ThrowIfCancellationRequested();
                if (!statusByPath.TryGetValue(source.FilePath, out var status))
                {
                    status = (File.Exists(source.FilePath), 0);
                    if (status.Exists)
                        try { status.Bytes = new FileInfo(source.FilePath).Length; } catch { }
                    statusByPath.Add(source.FilePath, status);
                    bytes += status.Bytes;
                }
                files.Add(CreateStatus(source, !status.Exists));
            }
            var formats = string.Join(", ", snapshot.Select(source => Path.GetExtension(source.FilePath).TrimStart('.').ToUpperInvariant())
                .Where(format => format.Length > 0).Distinct().OrderBy(format => format));
            performance.Mark("LibraryInspectionItemCount", snapshot.Length);
            performance.Mark("LibraryInspectionUniquePaths", statusByPath.Count);
            return new AudioLibraryStatistics(files.OrderByDescending(file => file.IsMissing).ThenBy(file => file.DeckName)
                .ThenBy(file => file.Title).ToArray(), bytes, formats.Length > 0 ? formats : "–");
        }, token);
    }

    internal static async Task<AudioRelinkMatch[]> FindMatchesAsync(string root, AudioLibrarySource[] sources,
        bool updateAll, AudioMetadataService metadata, CancellationToken token = default)
    {
        var snapshot = sources.ToArray();
        var matches = await Task.Run(() =>
        {
            var performance = PerformanceDiagnostics.BeginOperation("LibraryRelinkRequested");
            using var total = performance.Measure("LibraryRelinkSearch");
            var discovered = EnumerateAudioFiles(root, token);
            var byName = discovered.GroupBy(path => Path.GetFileName(path) ?? "", StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Key.Length > 0)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var candidates = discovered.Select(path => new AudioCandidate(path, NormalizeFileName(path))).ToArray();
            var byNormalizedName = candidates.Where(candidate => candidate.NormalizedName.Length > 0)
                .GroupBy(candidate => candidate.NormalizedName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var results = new List<(int Index, string Path)>();
            for (var index = 0; index < snapshot.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var source = snapshot[index];
                if (!updateAll && File.Exists(source.FilePath)) continue;
                var match = FindBestMatch(source.FilePath, source.Title, byName, byNormalizedName, candidates, token);
                if (match is not null && !string.Equals(source.FilePath, match, StringComparison.OrdinalIgnoreCase))
                    results.Add((index, match));
            }
            performance.Mark("LibraryRelinkCandidateCount", discovered.Count);
            performance.Mark("LibraryRelinkMatchCount", results.Count);
            return results.ToArray();
        }, token).ConfigureAwait(false);
        var durations = await metadata.ReadAsync(matches.Select(match => match.Path), token).ConfigureAwait(false);
        return matches.Select((match, index) => new AudioRelinkMatch(match.Index, match.Path, durations[index].DurationSeconds)).ToArray();
    }

    private static List<string> EnumerateAudioFiles(string root, CancellationToken token)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (AudioExtensions.Contains(Path.GetExtension(file))) result.Add(file);
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    pending.Push(child);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return result;
    }

    // Keep RC1's exact, unique normalized and unambiguous fuzzy matching order.
    internal static string? FindBestMatch(string filePath, string title, IReadOnlyDictionary<string, string> byName,
        IReadOnlyDictionary<string, AudioCandidate[]> byNormalizedName, IReadOnlyList<AudioCandidate> candidates,
        CancellationToken token = default)
    {
        var expectedFileName = Path.GetFileName(filePath) ?? "";
        if (byName.TryGetValue(expectedFileName, out var exact)) return exact;
        var expectedKeys = new[] { NormalizeFileName(expectedFileName), NormalizeFileName(title) }
            .Where(key => key.Length >= 6).Distinct(StringComparer.Ordinal).ToArray();
        if (expectedKeys.Length == 0) return null;
        foreach (var key in expectedKeys)
            if (byNormalizedName.TryGetValue(key, out var normalizedMatches) && normalizedMatches.Length == 1)
                return normalizedMatches[0].Path;
        var ranked = candidates.Select(candidate =>
            {
                token.ThrowIfCancellationRequested();
                return new { candidate.Path, Score = expectedKeys.Max(key => FileNameSimilarity(key, candidate.NormalizedName)) };
            })
            .Where(result => result.Score >= 0.78).OrderByDescending(result => result.Score).Take(2).ToArray();
        if (ranked.Length == 0) return null;
        if (ranked.Length > 1 && ranked[0].Score - ranked[1].Score < 0.08) return null;
        return ranked[0].Path;
    }

    internal static string NormalizeFileName(string value)
    {
        var stem = Path.GetFileNameWithoutExtension(value).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(stem.Length);
        foreach (var character in stem)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        var ignored = new HashSet<string>(StringComparer.Ordinal) { "spotifydown" };
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => !ignored.Contains(token)));
    }

    private static double FileNameSimilarity(string expected, string candidate)
    {
        if (expected.Length == 0 || candidate.Length == 0) return 0;
        if (string.Equals(expected, candidate, StringComparison.Ordinal)) return 1;
        var shorter = expected.Length <= candidate.Length ? expected : candidate;
        var longer = expected.Length > candidate.Length ? expected : candidate;
        if (shorter.Length >= 8 && longer.Contains(shorter, StringComparison.Ordinal))
            return 0.88 + 0.12 * shorter.Length / longer.Length;
        var expectedTokens = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var candidateTokens = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var shared = expectedTokens.Intersect(candidateTokens).Count();
        return expectedTokens.Count + candidateTokens.Count == 0 ? 0 : 2d * shared / (expectedTokens.Count + candidateTokens.Count);
    }

    private static AudioFileStatus CreateStatus(AudioLibrarySource source, bool missing)
    {
        if (!missing) return new(source.DeckName, source.Title, source.FilePath, false, "", "", "");
        var expectedFolder = Path.GetDirectoryName(source.FilePath) ?? "Okänd mapp";
        var problem = Directory.Exists(expectedFolder)
            ? $"Filen finns inte i den länkade mappen: {expectedFolder}"
            : $"Den länkade mappen finns inte längre: {expectedFolder}";
        var current = expectedFolder;
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
            current = Path.GetDirectoryName(current) ?? "";
        var hint = $"Sök efter “{Path.GetFileName(source.FilePath)}” från: {(string.IsNullOrWhiteSpace(current) ? "välj musikbibliotekets rotmapp" : current)}";
        return new(source.DeckName, source.Title, source.FilePath, true, problem, hint, current);
    }
}

internal sealed record AudioCandidate(string Path, string NormalizedName);
