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
        Name = project.Name,
        Settings = project.Settings?.CopyForSave()!,
        Decks = project.Decks is null ? null! : new ObservableCollection<Deck>(
            project.Decks.Select(deck => deck?.CopyForSave()!))
    });

    internal byte[] Serialize(JsonSerializerOptions options) => JsonSerializer.SerializeToUtf8Bytes(_project, options);
}
