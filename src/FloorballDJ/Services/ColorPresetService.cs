using System.Text.Json;

namespace FloorballDJ.Services;

public sealed record ColorPreset(string Name, string ButtonColor, string TextColor);

public sealed class ColorPresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FloorballDJ", "color-presets.json");

    public string PresetsPath => _path;

    public IReadOnlyList<ColorPreset> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<ColorPreset>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<ColorPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(presets, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    public int MergeFrom(string sourcePath)
    {
        if (!File.Exists(sourcePath)) return 0;
        try
        {
            var imported = JsonSerializer.Deserialize<List<ColorPreset>>(File.ReadAllText(sourcePath), JsonOptions) ?? [];
            var merged = Load().Where(preset => !string.IsNullOrWhiteSpace(preset.Name))
                .GroupBy(preset => preset.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.CurrentCultureIgnoreCase);
            foreach (var preset in imported.Where(preset => !string.IsNullOrWhiteSpace(preset.Name)))
                merged[preset.Name.Trim()] = preset with { Name = preset.Name.Trim() };
            Save(merged.Values.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase));
            return imported.Count;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
