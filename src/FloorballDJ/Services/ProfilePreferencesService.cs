using System.Text.Json;

namespace FloorballDJ.Services;

public sealed class ProfilePreferencesService
{
    private const int MaximumRecentProfiles = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private readonly object _gate = new();
    private readonly string _preferencesPath;
    private readonly string _autosavePath;

    public ProfilePreferencesService(string appDataDirectory, string autosavePath)
    {
        _preferencesPath = Path.Combine(appDataDirectory, "profile-preferences.json");
        _autosavePath = Path.GetFullPath(autosavePath);
    }

    public string? GetDefaultProfilePath()
    {
        lock (_gate) return Load().DefaultProfilePath;
    }

    public IReadOnlyList<string> GetRecentProfiles()
    {
        lock (_gate)
        {
            var preferences = Load();
            var recent = NormalizeRecent(preferences.RecentProfilePaths);
            if (!recent.SequenceEqual(preferences.RecentProfilePaths, StringComparer.OrdinalIgnoreCase))
            {
                preferences.RecentProfilePaths = recent;
                TrySave(preferences);
            }
            return recent;
        }
    }

    public void RecordProfile(string path)
    {
        var normalized = NormalizeProfilePath(path);
        if (normalized is null || IsAutosave(normalized)) return;

        lock (_gate)
        {
            var preferences = Load();
            preferences.RecentProfilePaths.RemoveAll(item =>
                string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
            preferences.RecentProfilePaths.Insert(0, normalized);
            preferences.RecentProfilePaths = NormalizeRecent(preferences.RecentProfilePaths);
            Save(preferences);
        }
    }

    public void RemoveRecentProfile(string path)
    {
        var normalized = NormalizeProfilePath(path);
        if (normalized is null) return;
        lock (_gate)
        {
            var preferences = Load();
            if (preferences.RecentProfilePaths.RemoveAll(item =>
                    string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase)) > 0)
                Save(preferences);
        }
    }

    public void ClearRecentProfiles()
    {
        lock (_gate)
        {
            var preferences = Load();
            preferences.RecentProfilePaths.Clear();
            Save(preferences);
        }
    }

    public void SetDefaultProfile(string? path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? null : NormalizeProfilePath(path);
        if (!string.IsNullOrWhiteSpace(path) && normalized is null)
            throw new ArgumentException("Standardprofilens sökväg är ogiltig.", nameof(path));
        if (normalized is not null && !File.Exists(normalized))
            throw new FileNotFoundException("Standardprofilen finns inte längre.", normalized);

        lock (_gate)
        {
            var preferences = Load();
            preferences.DefaultProfilePath = normalized;
            if (normalized is not null && !IsAutosave(normalized))
            {
                preferences.RecentProfilePaths.RemoveAll(item =>
                    string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
                preferences.RecentProfilePaths.Insert(0, normalized);
                preferences.RecentProfilePaths = NormalizeRecent(preferences.RecentProfilePaths);
            }
            Save(preferences);
        }
    }

    private List<string> NormalizeRecent(IEnumerable<string> paths)
        => paths
            .Select(NormalizeProfilePath)
            .Where(path => path is not null && !IsAutosave(path) && File.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumRecentProfiles)
            .ToList();

    private static string? NormalizeProfilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private bool IsAutosave(string path)
        => string.Equals(path, _autosavePath, StringComparison.OrdinalIgnoreCase);

    private ProfilePreferencesData Load()
    {
        try
        {
            if (!File.Exists(_preferencesPath)) return new ProfilePreferencesData();
            var preferences = JsonSerializer.Deserialize<ProfilePreferencesData>(File.ReadAllText(_preferencesPath), JsonOptions)
                              ?? new ProfilePreferencesData();
            preferences.RecentProfilePaths ??= [];
            return preferences;
        }
        catch
        {
            return new ProfilePreferencesData();
        }
    }

    private void TrySave(ProfilePreferencesData preferences)
    {
        try { Save(preferences); }
        catch { }
    }

    private void Save(ProfilePreferencesData preferences)
    {
        var directory = Path.GetDirectoryName(_preferencesPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_preferencesPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, JsonOptions));
            File.Move(temporaryPath, _preferencesPath, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private sealed class ProfilePreferencesData
    {
        public string? DefaultProfilePath { get; set; }
        public List<string> RecentProfilePaths { get; set; } = [];
    }
}
