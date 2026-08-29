using System.Security.Cryptography;
using FloorballDJ.Models;
using FloorballDJ.Services;

var testRoot = Path.Combine(Path.GetTempPath(), $"FloorballDJ-backup-smoke-{Guid.NewGuid():N}");
var sourceDirectory = Path.Combine(testRoot, "source");
var backupParent = Path.Combine(testRoot, "backups");
var appDataDirectory = Path.Combine(testRoot, "appdata");
Directory.CreateDirectory(sourceDirectory);
Directory.CreateDirectory(backupParent);
Environment.SetEnvironmentVariable("FLOORBALLDJ_DATA_DIR", appDataDirectory);

var contentRoot = Path.Combine(testRoot, "FloorballDJ");
var profilesFolder = UserContentFolders.EnsureSubfolder(UserContentFolders.ProfilesFolderName, contentRoot);
var createdJinglesFolder = UserContentFolders.EnsureSubfolder(UserContentFolders.CreatedJinglesFolderName, contentRoot);
if (!Directory.Exists(profilesFolder) || !Directory.Exists(createdJinglesFolder))
    throw new InvalidOperationException("Användarmapparna skapades inte.");

var sourceAudio = Path.Combine(sourceDirectory, "test-audio.wav");
var expectedBytes = Enumerable.Range(0, 32768).Select(index => (byte)(index * 31 % 251)).ToArray();
await File.WriteAllBytesAsync(sourceAudio, expectedBytes);
var expectedWriteTime = new DateTime(2026, 8, 19, 10, 30, 0, DateTimeKind.Utc);
File.SetLastWriteTimeUtc(sourceAudio, expectedWriteTime);

var service = new ProjectService();
var project = ProjectService.CreateDefault();
project.Name = "Flyttest";
project.Settings.FadeInSeconds = 1.25;
project.Settings.FadeOutSeconds = 3.5;
project.Settings.OutputDeviceId = "old-computer-output";
project.Settings.SecondaryOutputDeviceId = "old-computer-preview";
project.Settings.MusicFolderPath = sourceDirectory;
var jingle = project.Decks[0].Jingles[0];
jingle.Title = "Testjingle";
jingle.FilePath = sourceAudio;
jingle.StartSeconds = 1.5;
jingle.EndSeconds = 4.25;
project.Decks[0].PageCount = 2;
ProjectService.EnsureLayout(project);
var pageTwoText = project.Decks[0].Jingles[project.Decks[0].Rows * project.Decks[0].Columns];
pageTwoText.IsTextBlock = true;
pageTwoText.Title = "Sida två";
project.Settings.RandomPoolProfiles =
[
    new RandomPoolProfile
    {
        Name = "IBF Mål",
        Shortcut = "Shift+2",
        DeckIds = [project.Decks[0].Id],
        JingleIds = [jingle.Id],
        FollowUpJingleIds = [jingle.Id],
        FollowUpFadeOutSeconds = 2.25,
        FollowUpFadeInSeconds = .6
    }
];
var ibfRandomSetup = project.Settings.RandomPoolSetups.Single();
ibfRandomSetup.Name = "IBF";
ibfRandomSetup.Profiles = project.Settings.RandomPoolProfiles;
project.Settings.ActiveRandomPoolSetupId = ibfRandomSetup.Id;
project.Settings.RandomPoolSetups.Add(new RandomPoolSetup
{
    Name = "Scandic",
    Profiles =
    [
        new RandomPoolProfile
        {
            Name = "Scandic Publik",
            Shortcut = "Shift+3",
            JingleIds = [jingle.Id]
        }
    ]
});
project.Settings.TeamDeckProfiles =
[
    new TeamDeckProfile
    {
        Name = "IBF Dalen",
        Shortcut = "1",
        DefaultJingleId = jingle.Id,
        TransitionAtSeconds = 8,
        DefaultFadeOutSeconds = 1.5,
        PlayerFadeInSeconds = .75,
        Players = [new TeamDeckPlayer { Number = "19", Name = "Testspelare", JingleId = jingle.Id, StartSecondsOverride = 12.5 }]
    }
];

var backup = await service.CreateMediaBackupAsync(project, backupParent);
Assert(backup.MediaFileCount == 1, "Backupen ska innehålla en unik ljudfil.");
Assert(backup.MissingFiles.Count == 0, "Backupen ska inte rapportera saknade filer.");
Assert(File.Exists(Path.Combine(backup.Directory, "floorballdj-backup.json")), "Backupmanifest saknas.");
Assert(File.Exists(Path.Combine(backup.Directory, "LÄS MIG - ÅTERSTÄLL BACKUP.txt")), "Återställningsguide saknas.");

var portable = await service.LoadAsync(backup.ProfilePath);
var portableAudio = portable.Decks[0].Jingles[0].FilePath;
Assert(File.Exists(portableAudio), "Den portabla ljudsökvägen fungerar inte.");
Assert(Hash(sourceAudio) == Hash(portableAudio), "Ljudfilens bytes ändrades i backupen.");
Assert(File.GetLastWriteTimeUtc(portableAudio) == expectedWriteTime, "Ljudfilens analystidsstämpel bevarades inte.");
Assert(portable.Settings.OutputDeviceId is null && portable.Settings.SecondaryOutputDeviceId is null,
    "Maskinspecifika ljudutgångar följde med.");
Assert(portable.Settings.FadeInSeconds == 1.25 && portable.Settings.FadeOutSeconds == 3.5,
    "Profilens ljudinställningar bevarades inte.");
Assert(portable.Decks[0].PageCount == 2 &&
       portable.Decks[0].Jingles[portable.Decks[0].Rows * portable.Decks[0].Columns].Title == "Sida två",
    "Deckets sidor bevarades inte i backupen.");
Assert(portable.Settings.RandomPoolProfiles.Count == 1 &&
       portable.Settings.RandomPoolProfiles[0].Name == "IBF Mål" &&
       portable.Settings.RandomPoolProfiles[0].Shortcut == "Shift+2" &&
       portable.Settings.RandomPoolProfiles[0].JingleIds.SequenceEqual([jingle.Id]) &&
       portable.Settings.RandomPoolProfiles[0].FollowUpJingleIds.SequenceEqual([jingle.Id]) &&
       portable.Settings.RandomPoolProfiles[0].FollowUpFadeOutSeconds == 2.25 &&
       portable.Settings.RandomPoolProfiles[0].FollowUpFadeInSeconds == .6,
    "Profilens egna slumpgrupp bevarades inte i flyttbackupen.");
Assert(portable.Settings.RandomPoolSetups.Count == 2 &&
       portable.Settings.RandomPoolSetups.Single(setup => setup.Name == "Scandic").Profiles.Single().Shortcut == "Shift+3" &&
       portable.Settings.ActiveRandomPoolSetup?.Name == "IBF",
    "Separata slumpprofiler eller aktiv slumpprofil bevarades inte i flyttbackupen.");
Assert(backup.RandomPoolProfileCount == 2, "Backupresultatet rapporterar fel antal profilbundna slumpgrupper.");
Assert(backup.TeamDeckProfileCount == 1, "Backupresultatet rapporterar fel antal Team Deck.");
Assert(portable.Settings.TeamDeckProfiles.Single().Name == "IBF Dalen" &&
       portable.Settings.TeamDeckProfiles.Single().DefaultJingleId == jingle.Id &&
       portable.Settings.TeamDeckProfiles.Single().TransitionAtSeconds == 8 &&
       portable.Settings.TeamDeckProfiles.Single().DefaultFadeOutSeconds == 1.5 &&
       portable.Settings.TeamDeckProfiles.Single().PlayerFadeInSeconds == .75 &&
       portable.Settings.TeamDeckProfiles.Single().Players.Single().JingleId == jingle.Id &&
       portable.Settings.TeamDeckProfiles.Single().Players.Single().StartSecondsOverride == 12.5,
    "Profilens Team Deck bevarades inte i flyttbackupen.");

var restored = await service.RestorePortableBackupAsync(backup.Directory);
var imported = await service.LoadAsync(restored.ProfilePath);
Assert(restored.MissingMediaCount == 0, "Återställd backup rapporterar saknade ljudfiler.");
Assert(File.Exists(imported.Decks[0].Jingles[0].FilePath), "Återställd ljudfil saknas.");
Assert(Hash(sourceAudio) == Hash(imported.Decks[0].Jingles[0].FilePath), "Återställd ljudfil skiljer sig från originalet.");
Assert(imported.Decks[0].Jingles[0].StartSeconds == 1.5 && imported.Decks[0].Jingles[0].EndSeconds == 4.25,
    "Jinglens klippgränser bevarades inte.");
Assert(restored.RandomPoolProfileCount == 2 &&
       imported.Settings.RandomPoolProfiles.Count == 1 &&
       imported.Settings.RandomPoolProfiles[0].Name == "IBF Mål",
    "Profilens slumpgrupp återställdes inte.");
Assert(imported.Settings.RandomPoolSetups.Count == 2 &&
       imported.Settings.ActiveRandomPoolSetup?.Name == "IBF",
    "Profilens separata slumpuppsättningar återställdes inte.");
Assert(restored.TeamDeckProfileCount == 1 && imported.Settings.TeamDeckProfiles.Single().Players.Single().Name == "Testspelare",
    "Profilens Team Deck återställdes inte.");

// En annan profil på samma dator ska behålla en helt separat uppsättning.
var secondProfile = ProjectService.CreateDefault();
secondProfile.Name = "Scandic";
secondProfile.Settings.RandomPoolProfiles =
[
    new RandomPoolProfile
    {
        Name = "Scandic Utvisning",
        Shortcut = "Ctrl+7",
        DeckIds = [secondProfile.Decks[1].Id]
    }
];
var firstProfilePath = Path.Combine(testRoot, "ibf.floorballdj.json");
var secondProfilePath = Path.Combine(testRoot, "scandic.floorballdj.json");
await service.SaveAsync(project, firstProfilePath);
await service.SaveAsync(secondProfile, secondProfilePath);
var reloadedFirst = await service.LoadAsync(firstProfilePath);
var reloadedSecond = await service.LoadAsync(secondProfilePath);
ProjectService.EnsureLayout(reloadedFirst);
ProjectService.EnsureLayout(reloadedSecond);
Assert(reloadedFirst.Settings.RandomPoolProfiles.Single().Name == "IBF Mål",
    "IBF-profilens slumpgrupp ändrades av en annan profil.");
Assert(reloadedSecond.Settings.RandomPoolProfiles.Single().Name == "Scandic Utvisning" &&
       reloadedSecond.Settings.RandomPoolProfiles.Single().Shortcut == "Ctrl+7",
    "Scandic-profilen fick fel slumpgrupp.");

var revisionProject = ProjectService.CreateDefault();
revisionProject.Name = "Revisionstest";
var revisionPath = Path.Combine(testRoot, "revisions.floorballdj.json");
await service.SaveAsync(revisionProject, revisionPath);
revisionProject.Decks[0].Jingles[0].IsTextBlock = true;
revisionProject.Decks[0].Jingles[0].Title = "Ny periodrubrik";
await service.SaveAsync(revisionProject, revisionPath);
var revisions = await service.GetRevisionsAsync(revisionPath);
Assert(revisions.Count == 1 && revisions[0].ChangeDescription.Contains("lade till", StringComparison.OrdinalIgnoreCase) &&
       revisions[0].ChangeDescription.Contains("Ny periodrubrik", StringComparison.Ordinal),
    "Autosparhistoriken beskrev inte händelsen som skapade revisionen.");

var pagedDeck = new Deck { Rows = 2, Columns = 3, PageCount = 2 };
for (var index = 0; index < 12; index++) pagedDeck.Jingles.Add(new Jingle { Position = index });
pagedDeck.Jingles[6].IsTextBlock = true;
pagedDeck.Jingles[6].Title = "Första rutan på sida två";
ProjectService.ResizeDeckLayout(pagedDeck, 3, 2);
Assert(pagedDeck.PageCount == 2 && pagedDeck.Jingles[6].Title == "Första rutan på sida två",
    "Layoutändring flyttade innehåll mellan decksidor.");

var shrinkingDeck = new Deck { Rows = 2, Columns = 3, PageCount = 1 };
for (var index = 0; index < 6; index++)
    shrinkingDeck.Jingles.Add(new Jingle
    {
        Position = index,
        IsTextBlock = true,
        Title = $"Jingle {index + 1}"
    });
ProjectService.ResizeDeckLayout(shrinkingDeck, 1, 3);
Assert(shrinkingDeck.PageCount == 2 && shrinkingDeck.Jingles.Take(6).All(jingle => jingle.HasContent),
    "Minskad layout skapade inte en ny sida för jinglar som inte längre fick plats.");

Console.WriteLine("Portable backup smoke test passed.");
return;

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
