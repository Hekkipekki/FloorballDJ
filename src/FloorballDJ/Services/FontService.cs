using System.Windows.Media;
using Microsoft.Win32;

namespace FloorballDJ.Services;

public static class FontService
{
    public static string FontsDirectory { get; } = ResolveFontsDirectory();

    private static readonly BundledFont[] BundledFonts =
    [
        new("Inter  ·  Medföljer", "Inter", "Assets/Fonts/Inter/", "Inter"),
        new("Atkinson Hyperlegible Next  ·  Medföljer", "Atkinson Hyperlegible Next", "Assets/Fonts/AtkinsonHyperlegibleNext/", "Atkinson Hyperlegible Next"),
        new("Manrope  ·  Medföljer", "Manrope", "Assets/Fonts/Manrope/", "Manrope")
    ];

    public static IReadOnlyList<FontChoice> GetFonts()
    {
        var result = new List<FontChoice>();
        foreach (var font in BundledFonts)
            result.Add(new FontChoice(font.DisplayName, $"bundled:{font.Value}", CreateBundledFamily(font), false, true));

        foreach (var name in GetSystemFontNames())
            result.Add(new FontChoice(name, name, new FontFamily(name), false));

        try
        {
            if (Directory.EnumerateFiles(FontsDirectory).Any(path =>
                    Path.GetExtension(path) is var extension &&
                    (extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))))
            {
                var folderUri = CreateFolderUri();
                foreach (var family in Fonts.GetFontFamilies(folderUri))
                {
                    var name = family.FamilyNames.Values.FirstOrDefault() ?? family.Source.Replace("./#", "");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    result.Add(new FontChoice($"{name}  ·  Egen font", $"custom:{name}",
                        new FontFamily(folderUri, $"./#{name}"), true));
                }
            }
        }
        catch { }

        if (result.Count == 0)
        {
            foreach (var name in new[] { "Segoe UI", "Arial", "Verdana", "Tahoma", "Cascadia Mono" })
                result.Add(new FontChoice(name, name, new FontFamily(name), false));
        }

        return result
            .GroupBy(font => font.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(font => font.IsBundled)
            .ThenByDescending(font => font.IsCustom)
            .ThenBy(font => font.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static FontFamily Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new FontFamily("Segoe UI Variable Display");
        if (value.StartsWith("bundled:", StringComparison.OrdinalIgnoreCase))
        {
            var bundledName = value["bundled:".Length..];
            var bundled = BundledFonts.FirstOrDefault(font => font.Value.Equals(bundledName, StringComparison.OrdinalIgnoreCase));
            if (bundled is not null) return CreateBundledFamily(bundled);
        }
        if (!value.StartsWith("custom:", StringComparison.OrdinalIgnoreCase)) return new FontFamily(value);
        var name = value["custom:".Length..];
        return new FontFamily(CreateFolderUri(), $"./#{name}");
    }

    private static Uri CreateFolderUri() => new(FontsDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, UriKind.Absolute);

    private static FontFamily CreateBundledFamily(BundledFont font)
    {
        var folderUri = new Uri($"pack://application:,,,/FloorballDJ;component/{font.ResourceFolder}", UriKind.Absolute);
        return new FontFamily(folderUri, $"./#{font.FamilyName}");
    }

    private static IEnumerable<string> GetSystemFontNames()
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase)
            { "Segoe UI", "Segoe UI Variable Display", "Arial", "Verdana", "Tahoma", "Cascadia Mono" };
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var marker = valueName.LastIndexOf(" (", StringComparison.Ordinal);
                    var name = marker > 0 ? valueName[..marker] : valueName;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
            }
            catch { }
        }
        return names;
    }

    private static string ResolveFontsDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloorballDJ", "Fonts"),
            Path.Combine(AppContext.BaseDirectory, "Fonts")
        };
        foreach (var candidate in candidates)
        {
            try { Directory.CreateDirectory(candidate); return candidate; }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return candidates[^1];
    }
}

internal sealed record BundledFont(string DisplayName, string Value, string ResourceFolder, string FamilyName);

public sealed record FontChoice(string DisplayName, string Value, FontFamily PreviewFont, bool IsCustom, bool IsBundled = false);
