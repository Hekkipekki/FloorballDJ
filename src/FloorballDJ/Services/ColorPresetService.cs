using System.Text.Json;

namespace FloorballDJ.Services;

public sealed record ColorPreset(string Name, string ButtonColor, string TextColor);

public sealed class ColorPresetService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FloorballDJ", "color-presets.json");

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
}
