using System.Globalization;
using System.Xml.Linq;
using FloorballDJ.Models;
using NAudio.Wave;

namespace FloorballDJ.Services;

public static class LegacyXmlImporter
{
    public static FloorballProject Import(string path)
    {
        var root = XDocument.Load(path).Root ?? throw new InvalidDataException("XML-filen saknar rot.");
        var project = new FloorballProject { Name = Path.GetFileNameWithoutExtension(path) };
        project.Settings.Rows = Int(root, "Rows", 5);
        project.Settings.Columns = Int(root, "Cols", 5);
        project.Settings.DeckCount = Int(root, "Tabs", root.Elements("JinglePanel").Count());

        foreach (var panel in root.Elements("JinglePanel"))
        {
            var mediaItems = panel.Elements("JingleMedia").ToList();
            var highestPosition = mediaItems.Count == 0 ? -1 : mediaItems.Max(media => Int(media, "GridPosition", 0));
            var columns = Math.Max(1, project.Settings.Columns);
            var rows = Math.Max(1, Math.Max(project.Settings.Rows, (int)Math.Ceiling((highestPosition + 1) / (double)columns)));
            var deck = new Deck
            {
                Name = Value(panel, "Name") ?? $"Deck {project.Decks.Count + 1}",
                Rows = rows,
                Columns = columns
            };
            foreach (var media in mediaItems)
            {
                var position = Int(media, "GridPosition", 0);
                while (deck.Jingles.Count <= position)
                    deck.Jingles.Add(new Jingle { Position = deck.Jingles.Count });
                var jingle = new Jingle
                {
                    Position = position,
                    Title = Value(media, "Title") ?? "Jingle",
                    FilePath = Value(media, "FilePath") ?? "",
                    ButtonColor = LegacyColor(media, "PanelColor", "#182338"),
                    TextColor = LegacyColor(media, "TextColor", "#F7FAFC"),
                    StartSeconds = Milliseconds(media, "StartTime", 0),
                    EndSeconds = Milliseconds(media, "EndTime", -1) is var end && end >= 0 ? end : null,
                    Loop = Bool(media, "Loop"),
                    // Snap använder läge 3 för jinglar som får läggas ovanpå pågående ljud.
                    PlayMode = Int(media, "PlayMode", 0) switch { 3 => JinglePlayMode.Mix, 2 => JinglePlayMode.Duck, _ => JinglePlayMode.Solo },
                    PitchSemitones = Double(media, "Pitch", 0),
                    TempoPercent = Double(media, "Tempo", 0),
                    RatePercent = Double(media, "Rate", 0),
                    DurationSeconds = Duration(media, "ClipDuration"),
                    GainDb = PercentToDb(Double(media, "Amplify", 100)),
                    Shortcut = ShortcutService.Normalize(Value(media, "Shortcut"))
                };
                PopulateDuration(jingle);
                deck.Jingles[position] = jingle;
            }
            project.Decks.Add(deck);
        }
        ProjectService.EnsureLayout(project);
        project.Settings.DeckCount = project.Decks.Count;
        return project;
    }

    private static string? Value(XElement e, string name) => e.Element(name)?.Value;
    private static void PopulateDuration(Jingle jingle)
    {
        if (!File.Exists(jingle.FilePath)) return;
        try
        {
            using var reader = new AudioFileReader(jingle.FilePath);
            jingle.DurationSeconds = reader.TotalTime.TotalSeconds;
        }
        catch { }
    }
    private static int Int(XElement e, string name, int fallback) => int.TryParse(Value(e, name), out var x) ? x : fallback;
    private static double Double(XElement e, string name, double fallback) => double.TryParse(Value(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) ? x : fallback;
    private static double Milliseconds(XElement e, string name, double fallback) => Double(e, name, fallback) is var value && value >= 0 ? value / 1000d : value;
    private static bool Bool(XElement e, string name) => bool.TryParse(Value(e, name), out var x) && x;
    private static double PercentToDb(double percent) => percent <= 0 ? -60 : 20 * Math.Log10(percent / 100);
    private static double Duration(XElement e, string name) => TimeSpan.TryParse(Value(e, name), CultureInfo.InvariantCulture, out var value) ? value.TotalSeconds : 0;
    private static string LegacyColor(XElement e, string name, string fallback)
    {
        if (!int.TryParse(Value(e, name), out var signed)) return fallback;
        var argb = unchecked((uint)signed);
        return $"#{argb:X8}";
    }
}
