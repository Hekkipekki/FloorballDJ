using System.Collections.ObjectModel;
using System.Text.Json;
using FloorballDJ.Models;

namespace FloorballDJ.Services;

/// <summary>
/// Privately owned save graph, captured synchronously on the model owner's thread.
/// Strings/value fields are immutable; collections/models/subscriptions are detached.
/// No worker ever enumerates the live project, and the copy is never published to UI.
/// </summary>
internal sealed class ProjectSaveSnapshot
{
    private readonly FloorballProject _project;
    private ProjectSaveSnapshot(FloorballProject project) => _project = project;

    internal static ProjectSaveSnapshot Capture(FloorballProject project) => new(new FloorballProject
    {
        FormatVersion = project.FormatVersion,
        Id = project.Id,
        Name = project.Name,
        Settings = project.Settings?.CopyForSave()!,
        Decks = project.Decks is null ? null! : new ObservableCollection<Deck>(
            project.Decks.Select(deck => deck?.CopyForSave()!))
    });

    internal byte[] Serialize(JsonSerializerOptions options) => JsonSerializer.SerializeToUtf8Bytes(_project, options);

    internal void MakePathsRelativeTo(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string? Relative(string? value) => string.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value)
            ? value : Path.GetRelativePath(directory, value);
        foreach (var item in _project.Decks.SelectMany(deck => deck.Jingles).Concat(_project.Settings.AutoplayQueueItems ?? []))
            item.FilePath = Relative(item.FilePath)!;
        _project.Settings.MusicFolderPath = Relative(_project.Settings.MusicFolderPath);
        _project.Settings.AutoplayLastPlaylistPath = Relative(_project.Settings.AutoplayLastPlaylistPath);
        _project.Settings.AutoplayDefaultPlaylistPath = Relative(_project.Settings.AutoplayDefaultPlaylistPath);
        foreach (var playlist in _project.Settings.AutoplayProfiles ?? []) playlist.PlaylistPath = Relative(playlist.PlaylistPath);
    }
}
