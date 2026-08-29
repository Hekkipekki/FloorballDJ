using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace FloorballDJ.Services;

public sealed class LanguagePreferencesService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FloorballDJ", "ui-preferences.json");

    public string? GetLanguage()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return Normalize(JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path))?.Language);
        }
        catch { return null; }
    }

    public void SetLanguage(string language)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_path, JsonSerializer.Serialize(new Preferences { Language = Normalize(language) ?? "en" },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Normalize(string? language) =>
        string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase) ? "sv" : "en";

    private sealed class Preferences
    {
        public string Language { get; set; } = "en";
    }
}

public static class LanguageService
{
    public static string CurrentLanguage { get; private set; } = "en";
    public static bool IsEnglish => CurrentLanguage == "en";

    private static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Inställningar"] = "Settings", ["ARBETSSTATION"] = "WORKSTATION",
        ["Anpassa ljud, profiler, slumpuppspelning och utseende efter din matchdag."] = "Configure audio, profiles, random playback and appearance for your match day.",
        ["Ljud"] = "Audio", ["Utgångsenhet  ⓘ"] = "Output device  ⓘ", ["Utgångsenhet 2  ⓘ"] = "Output device 2  ⓘ",
        ["Global fade in  ⓘ"] = "Global fade in  ⓘ", ["Global fade ut  ⓘ"] = "Global fade out  ⓘ",
        ["Standard loudness  ⓘ"] = "Default loudness  ⓘ", ["Säkerhetslimiter  ⓘ"] = "Safety limiter  ⓘ",
        ["Skydda utgången mot digital överstyrning"] = "Protect the output against digital clipping",
        ["Limiter-tak  ⓘ"] = "Limiter ceiling  ⓘ", ["Automatiskt mixutrymme  ⓘ"] = "Automatic mix headroom  ⓘ",
        ["Sänk säkert när flera ljud spelas samtidigt"] = "Safely reduce level when several sounds play together",
        ["PA/Talk-dämpning  ⓘ"] = "PA/Talk ducking  ⓘ", ["Profiler"] = "Profiles",
        ["Standardprofil  ⓘ"] = "Default profile  ⓘ", ["Senast använda"] = "Recently used",
        ["Ingen standard"] = "No default", ["Autoplay-lista"] = "Autoplay playlist", ["Namn"] = "Name",
        ["Snabbtangent"] = "Shortcut", ["Spellista  ⓘ"] = "Playlist  ⓘ", ["Volym (dB)  ⓘ"] = "Volume (dB)  ⓘ",
        ["Typografi"] = "Typography", ["Typsnitt"] = "Font", ["Titelstorlek"] = "Title size",
        ["Egna typsnitt"] = "Custom fonts", ["Öppna mapp"] = "Open folder", ["Uppdatera"] = "Refresh",
        ["Ändringarna används direkt när du sparar."] = "Changes are applied when you save.",
        ["Avbryt"] = "Cancel", ["Spara inställningar"] = "Save settings", ["Välj"] = "Choose", ["Välj…"] = "Choose…",
        ["Ta bort"] = "Remove", ["Spara"] = "Save", ["Stäng"] = "Close", ["Spela"] = "Play", ["Paus"] = "Pause",
        ["Stopp"] = "Stop", ["Start"] = "Start", ["Slut"] = "End", ["Sök…"] = "Search…",
        ["_Profil"] = "_Profile", ["Nytt projekt"] = "New project", ["Öppna…"] = "Open…",
        ["Senast använda profiler"] = "Recent profiles", ["Inga profiler ännu"] = "No profiles yet",
        ["Spara som…"] = "Save as…", ["Importera profil…"] = "Import profile…",
        ["Skapa komplett flyttbackup…"] = "Create complete transfer backup…", ["Återställ flyttbackup…"] = "Restore transfer backup…",
        ["Avsluta"] = "Exit", ["_Verktyg"] = "_Tools", ["Inställningar…"] = "Settings…",
        ["Slumpmässig låtspelare…"] = "Random song player…", ["Team Deck…"] = "Team Deck…",
        ["Analysera och balansera ljud…"] = "Analyze and balance audio…", ["Bygg egen jingle…"] = "Build a jingle…",
        ["Kontrollera ljudfiler…"] = "Check audio files…", ["_Hjälp"] = "_Help",
        ["Kom igång och hjälp…"] = "Getting started and help…", ["Sök efter uppdateringar…"] = "Check for updates…",
        ["Licens och provperiod…"] = "License and trial…", ["Autosparningshistorik…"] = "Autosave history…",
        ["Om FloorballDJ"] = "About FloorballDJ", ["Alla ändringar autosparas"] = "All changes are saved automatically",
        ["Sök jingle/låt  Ctrl+F"] = "Search jingle/song  Ctrl+F", ["UTGÅNG  "] = "OUTPUT  ",
        ["Standardenhet"] = "Default device", ["KÖ"] = "QUEUE", ["●  SESSION"] = "●  SESSION",
        ["🎧  FÖRLYSSNING"] = "🎧  PREVIEW", ["Egenskaper…"] = "Properties…", ["Ladda ljud…"] = "Load audio…",
        ["Utseende"] = "Appearance", ["Ändra knappens färger…"] = "Change button colors…",
        ["Använd färger på hela raden…"] = "Apply colors to the entire row…", ["Kopiera jingle"] = "Copy jingle",
        ["Klistra in jingle"] = "Paste jingle", ["Spelläge"] = "Play mode", ["Tillåt flera samtidiga klick"] = "Allow multiple simultaneous clicks",
        ["Töm ruta"] = "Clear slot", ["SAKNAS"] = "MISSING", ["BYT PLATS"] = "SWAP",
        ["Sida"] = "Page", ["Sidor"] = "Pages",
        ["Ändra rader och kolumner…"] = "Change rows and columns…",
        ["Rader och kolumner per sida"] = "Rows and columns per page",
        ["Lägg till sida"] = "Add page", ["Ta bort sista sidan"] = "Remove last page",
        ["Hela decket…"] = "Entire deck…", ["Välj knappar…"] = "Select buttons…",
        ["Sortera alfabetiskt"] = "Sort alphabetically", ["Deckordning"] = "Deck order",
        ["Namn A–Ö"] = "Name A–Z", ["Namn Ö–A"] = "Name Z–A",
        ["Ändrar endast visningsordningen i den här menyn"] = "Only changes the display order in this window",
        ["Rader"] = "Rows", ["Kolumner"] = "Columns",
        ["Sekunder"] = "Seconds", ["Ljudfil"] = "Audio file", ["Titel"] = "Title",
        ["Förhandsvisa"] = "Preview", ["Förhandslyssning"] = "Preview", ["Teknisk information"] = "Technical information",
        ["Ljudmixer"] = "Audio mixer", ["Uppspelning"] = "Playback", ["Loop"] = "Loop",
        ["Ingen snabbknapp"] = "No shortcut", ["<Ingen>"] = "<None>", ["Ingen"] = "None",
        ["Skapa och lägg till"] = "Create and add", ["Analysera"] = "Analyze", ["Analysera valda"] = "Analyze selected",
        ["Spara ändringar"] = "Save changes", ["Klar"] = "Done", ["Nästa"] = "Next", ["Tillbaka"] = "Back",
        ["Aktivera FloorballDJ"] = "Activate FloorballDJ", ["Aktivera licens"] = "Activate license",
        ["Aktivera en köpt licens"] = "Activate a purchased license", ["Avaktivera dator"] = "Deactivate computer",
        ["Försök igen"] = "Try again", ["Fortsätt"] = "Continue", ["LICENSNYCKEL"] = "LICENSE KEY",
        ["LICENS OCH PROVPERIOD"] = "LICENSE AND TRIAL", ["Kontrollerar licens…"] = "Checking license…",
        ["FloorballDJ – licens"] = "FloorballDJ – license", ["FloorballDJ – uppdateringar"] = "FloorballDJ – updates",
        ["PROGRAMUPPDATERING"] = "APPLICATION UPDATE", ["Sök efter uppdateringar"] = "Check for updates",
        ["Ladda ned och installera"] = "Download and install", ["Öppna nedladdningssidan"] = "Open download page",
        ["Ändringslogg"] = "Changelog", ["Release"] = "Release", ["Aktiverad"] = "Active",
        ["HJÄLPCENTER"] = "HELP CENTER", ["Så använder du FloorballDJ"] = "How to use FloorballDJ",
        ["SNABBSTART"] = "QUICK START", ["UPPSPELNING"] = "PLAYBACK", ["DECKS OCH JINGLAR"] = "DECKS AND JINGLES",
        ["AUTOPLAY OCH KÖER"] = "AUTOPLAY AND QUEUES", ["LJUD OCH BALANSERING"] = "AUDIO AND BALANCING",
        ["JINGLEBYGGAREN"] = "JINGLE BUILDER", ["PROFILER OCH BACKUP"] = "PROFILES AND BACKUP",
        ["KORTKOMMANDON"] = "SHORTCUTS", ["FELSÖKNING"] = "TROUBLESHOOTING",
        ["Jingle-egenskaper"] = "Jingle properties", ["Jingle – förhandsvisning"] = "Jingle – preview",
        ["Nivå och normalisering"] = "Level and normalization", ["Manuell gain"] = "Manual gain",
        ["Automatisk LUFS-anpassning"] = "Automatic LUFS normalization", ["Målnivå  ⓘ"] = "Target level  ⓘ",
        ["Analysera ljudfil"] = "Analyze audio file", ["LOUDNESS  ⓘ"] = "LOUDNESS  ⓘ",
        ["TRUE PEAK  ⓘ"] = "TRUE PEAK  ⓘ", ["AVANCERAT: EQ, KOMPRESSOR, PITCH OCH TEMPO"] = "ADVANCED: EQ, COMPRESSOR, PITCH AND TEMPO",
        ["Tonkontroll och hastighet"] = "Tone control and speed", ["Kompressor"] = "Compressor",
        ["Bas"] = "Bass", ["Mellan"] = "Mid", ["Diskant"] = "Treble", ["Tröskel"] = "Threshold",
        ["Ratio"] = "Ratio", ["Attack"] = "Attack", ["Tempo / Rate"] = "Tempo / Rate",
        ["Start och slut"] = "Start and end", ["Trimma tystnad"] = "Trim silence",
        ["Analysera och balansera ljudbibliotek"] = "Analyze and balance audio library",
        ["LOUDNESS OCH SÄKER NIVÅ"] = "LOUDNESS AND SAFE LEVEL", ["Analysera och balansera"] = "Analyze and balance",
        ["Avbryt analys"] = "Cancel analysis", ["Markera alla"] = "Select all", ["Avmarkera alla"] = "Clear all",
        ["Markera ej färdiga"] = "Select unfinished", ["Analyserad, ej balanserad"] = "Analyzed, not balanced",
        ["Inte analyserad"] = "Not analyzed", ["Inte analyserat"] = "Not analyzed", ["Analysvärdet verkar gammalt"] = "Analysis is out of date",
        ["JINGLEBYGGARE"] = "JINGLE BUILDER", ["Skapa mållåtar och egna ljudsekvenser"] = "Create goal songs and custom audio sequences",
        ["Jingle / sekvens"] = "Jingle / sequence", ["Lång mix"] = "Long mix", ["Lägg till ljud"] = "Add audio",
        ["＋ Lägg till ljud"] = "＋ Add audio", ["Öppna rå ljudfil…"] = "Open raw audio file…",
        ["Spela från markör"] = "Play from marker", ["Paus / fortsätt"] = "Pause / resume",
        ["Övergång till nästa ljud"] = "Transition to next audio", ["Crossfade"] = "Crossfade",
        ["Fade out → fade in"] = "Fade out → fade in", ["Mixljud – spela samtidigt"] = "Mix audio – play simultaneously",
        ["Fade out ljud 1"] = "Fade out audio 1", ["Fade in ljud 2"] = "Fade in audio 2",
        ["Mallar och förhandslyssning"] = "Templates and preview", ["Förhandslyssna hela mixen"] = "Preview entire mix",
        ["▶ Förhandslyssna hela mixen"] = "▶ Preview entire mix", ["Bygg lång mix"] = "Build long mix",
        ["LÅNGMIXBYGGARE"] = "LONG MIX BUILDER", ["Planera uppvärmningsmixar och längre musikpass"] = "Plan warm-up mixes and longer music sessions",
        ["Tillgängliga låtar"] = "Available songs", ["Föreslagen spelordning"] = "Suggested play order",
        ["Föreslå spelordning"] = "Suggest play order", ["Beräknad mixlängd"] = "Estimated mix length",
        ["BERÄKNAD MIXLÄNGD"] = "ESTIMATED MIX LENGTH", ["Ta bort svagaste matchning"] = "Remove weakest match",
        ["Finjustera långmix"] = "Fine-tune long mix", ["Önskad mixlängd"] = "Desired mix length",
        ["Slumpmässig låtspelare"] = "Random song player", ["SLUMPGRUPPER"] = "RANDOM GROUPS",
        ["+ Ny grupp"] = "+ New group", ["Spara slumpgrupper"] = "Save random groups",
        ["POOLÖVERSIKT"] = "POOL OVERVIEW", ["Använd hela decket"] = "Use entire deck",
        ["AKTIV SLUMPPROFIL"] = "ACTIVE RANDOM PROFILE",
        ["Byt lag utan att byta musikprofil"] = "Switch team without changing the music profile",
        ["+ Ny profil"] = "+ New profile",
        ["Namn på den aktiva slumpprofilen"] = "Name of the active random profile",
        ["Duplicera aktiv slumpprofil"] = "Duplicate active random profile",
        ["Ta bort aktiv slumpprofil"] = "Delete active random profile",
        ["HÄNDELSE"] = "EVENT",
        ["Team Deck"] = "Team Deck", ["Lagets måljinglar och spelarövergångar"] = "Team goal jingles and player transitions",
        ["LAG I PROFILEN"] = "TEAMS IN PROFILE", ["+ Nytt lag"] = "+ New team", ["Skapa ett lag"] = "Create a team",
        ["Lagnamn"] = "Team name", ["STANDARDJINGLE"] = "DEFAULT JINGLE", ["SPELARKNAPPAR"] = "PLAYER BUTTONS",
        ["+ Lägg till spelare"] = "+ Add player", ["Lägg till spelare"] = "Add player", ["Spara Team Deck"] = "Save Team Deck",
        ["Välj spelarjingle"] = "Choose player jingle", ["STARTPOSITION"] = "START POSITION",
        ["Kontrollera och hitta ljudfiler"] = "Check and locate audio files", ["LJUDBIBLIOTEK"] = "AUDIO LIBRARY",
        ["HITTADE"] = "FOUND", ["TOTAL STORLEK"] = "TOTAL SIZE", ["Länkade filer och problem"] = "Linked files and issues",
        ["Sök och uppdatera"] = "Search and update", ["Välj mapp…"] = "Choose folder…", ["Skapa backup"] = "Create backup",
        ["Komplett flyttbackup"] = "Complete transfer backup", ["AUTOSPARADE REVISIONER"] = "AUTOSAVED REVISIONS",
        ["Återställ vald"] = "Restore selected", ["Läser in historiken…"] = "Loading history…",
        ["Knapputseende"] = "Button appearance", ["Knappfärg"] = "Button color", ["Textfärg"] = "Text color",
        ["Sparade färger"] = "Saved colors", ["VISUELL FÄRGVÄLJARE"] = "VISUAL COLOR PICKER",
        ["Använd färger"] = "Apply colors", ["Välj knappar att ändra"] = "Select buttons to change",
        ["Aktuell sida"] = "Current page", ["Markera hela decket"] = "Select entire deck", ["Rensa val"] = "Clear selection",
        ["Decklayout"] = "Deck layout", ["Använd layout"] = "Apply layout", ["RADER"] = "ROWS", ["KOLUMNER (1–12)"] = "COLUMNS (1–12)",
        ["Fade för deck"] = "Deck fade", ["FADE IN (SEKUNDER)"] = "FADE IN (SECONDS)", ["FADE UT (SEKUNDER)"] = "FADE OUT (SECONDS)",
        ["Använd på deck"] = "Apply to deck", ["Sök bland alla jinglar"] = "Search all jingles",
        ["Sök och välj ljud"] = "Search and choose audio", ["Välj jingle"] = "Choose jingle", ["Visa jingle"] = "Show jingle",
        ["LJUDNIVÅ"] = "AUDIO LEVEL",
        ["Antal jinglar i den aktiva kön"] = "Number of jingles in the active queue",
        ["Byt sida · scrolla över deckfliken · släpp en jingle här för att flytta den"] = "Change page · scroll over the deck tab · drop a jingle here to move it",
        ["Datorns lokala datum och tid"] = "Computer's local date and time",
        ["Fade ut"] = "Fade out", ["Klicka för att hoppa i låten"] = "Click to seek in the song",
        ["Klicka för att skriva in en exakt ljudnivå"] = "Click to enter an exact audio level",
        ["Maximera eller återställ"] = "Maximize or restore", ["Minimera"] = "Minimize",
        ["PA/Talk: tona ned musiken medan speakern talar"] = "PA/Talk: lower the music while the announcer speaks",
        ["Plats i uppspelningskön"] = "Position in the playback queue", ["Programvolym i dB"] = "Application volume in dB",
        ["Spelad under sessionen"] = "Played during this session", ["Stereo-nivå för vänster och höger kanal"] = "Stereo level for left and right channels",
        ["Stoppa allt"] = "Stop all", ["Sök jingle eller låt i alla deck. Enter spelar träffen, pil ned visar nästa träff och Esc stänger sökningen."] = "Search for a jingle or song in every deck. Enter plays the result, Down selects the next result and Esc closes the search.",
        ["Varje klick startar en ny instans från början medan tidigare instanser fortsätter. Högst 8 instanser per jingle spelar samtidigt för stabilt ljud."] = "Each click starts a new instance from the beginning while previous instances continue. At most 8 instances per jingle play simultaneously for stable audio.",
        ["Växla mellan huvudutgång och utgång 2"] = "Switch between the main output and output 2",
        ["Öppna Hjälp → Licens och provperiod för information och aktivering."] = "Open Help → License and trial for information and activation.",
        ["Lägg till .ttf eller .otf och klicka sedan på Uppdatera."] = "Add .ttf or .otf files, then click Refresh.",
        ["Standardprofilen öppnas först vid start. De fem senast använda profilerna visas under Profil i menyraden."] = "The default profile opens first at startup. The five most recently used profiles appear under Profile in the menu bar.",
        ["Varje Autoplay-lista har egen spellista, snabbtangent och volym. Tangenten öppnar Autoplay, ersätter kön och startar första låten automatiskt."] = "Each Autoplay preset has its own playlist, shortcut and volume. The shortcut opens Autoplay, replaces the queue and starts the first song automatically.",
        ["Huvudutgången som publiken hör. Windows standardenhet följer den utgång som är vald i Windows."] = "The main output heard by the audience. Windows default device follows the output selected in Windows.",
        ["Separat utgång för förlyssning, exempelvis hörlurar. Den används bara när förlyssningsläget är aktiverat."] = "Separate preview output, such as headphones. It is used only when preview mode is enabled.",
        ["Alternativ utgång, exempelvis hörlurar för förlyssning"] = "Alternative output, such as headphones for preview",
        ["Hur många sekunder en ny låt normalt tonas in. En enskild jingle kan ha en egen tid som ersätter detta värde."] = "How many seconds a new song normally fades in. An individual jingle can override this value.",
        ["Hur många sekunder den spelande låten normalt tonas ut vid stopp eller byte. En enskild jingle kan ha en egen tid."] = "How many seconds the playing song normally fades out when stopped or changed. An individual jingle can have its own duration.",
        ["Målnivån som används vid automatisk LUFS-balansering. −16 LUFS är en trygg och tydlig startpunkt för idrottshall. Ett värde närmare 0 låter starkare men ger mindre dynamik och säkerhetsmarginal."] = "Target used for automatic LUFS balancing. −16 LUFS is a safe and clear starting point for a sports hall. A value closer to 0 sounds louder but leaves less dynamics and safety margin.",
        ["Rekommenderad start för idrottshall: −16 LUFS. Detta ändrar inte originalfilerna; en fast uppspelningsgain sparas per analyserad jingle."] = "Recommended starting point for a sports hall: −16 LUFS. This does not change the original files; a fixed playback gain is saved for each analyzed jingle.",
        ["Fångar plötsliga toppar som annars kan ge digital överstyrning. Låt normalt denna vara aktiverad. Den ersätter inte korrekt nivåinställning på PA-systemet."] = "Catches sudden peaks that could otherwise cause digital clipping. Normally keep this enabled. It does not replace correct PA system gain staging.",
        ["Rekommenderas aktiverad under match och evenemang."] = "Recommended during matches and events.",
        ["Högsta tillåtna toppnivå före utgången. −1 dBTP är normal säkerhetsmarginal; −2 dBTP ger extra marginal för idrottshall och enklare PA-system."] = "Maximum permitted peak level before output. −1 dBTP is a normal safety margin; −2 dBTP provides extra margin for sports halls and simpler PA systems.",
        ["Ange dBTP. Rekommenderat: −1 normalt eller −2 för extra säkerhetsmarginal i idrottshall."] = "Enter dBTP. Recommended: −1 normally or −2 for extra safety margin in a sports hall.",
        ["Skapar säkerhetsmarginal när flera ljud avsiktligt spelas samtidigt, exempelvis Mix eller Duck. En låt som bara tonas ut vid ett normalt byte påverkar inte längre den nya låtens nivå."] = "Creates headroom when several sounds intentionally play at once, such as Mix or Duck. A song merely fading out during a normal change no longer affects the new song's level.",
        ["Låt normalt detta vara aktiverat. Auto gain för en enskild låt hålls fast och ändras inte under vanlig crossfade."] = "Normally keep this enabled. Auto gain for an individual song stays fixed and does not change during a normal crossfade.",
        ["Hur mycket musiken sänks när PA/Talk är aktiv. −15 dB brukar lämna tydligt utrymme för tal utan att musiken försvinner helt."] = "How much the music is lowered while PA/Talk is active. −15 dB usually leaves clear room for speech without making the music disappear completely.",
        ["Profilen som öppnas automatiskt när FloorballDJ startar. Om ingen standardprofil är vald återställs den vanliga autosparningen."] = "The profile opened automatically when FloorballDJ starts. If no default profile is selected, the regular autosave is restored.",
        ["Ingen sökväg betyder att FloorballDJ använder den vanliga autosparningen."] = "An empty path means FloorballDJ uses the regular autosave.",
        ["Välj en av de fem senast använda profilerna som standardprofil"] = "Choose one of the five most recently used profiles as the default profile",
        ["Spellistan som laddas när den valda snabbtangenten används."] = "The playlist loaded when the selected shortcut is used.",
        ["0 dB är oförändrat. Ett negativt värde, exempelvis −8 dB, ger lugnare pausmusik utan att ändra originalfilerna."] = "0 dB is unchanged. A negative value, such as −8 dB, gives quieter intermission music without changing the original files.",
        ["Ta bort snabbtangent"] = "Remove shortcut", ["Ta bort spellistan"] = "Remove playlist",
        ["Lägg .ttf- eller .otf-filer i den här mappen"] = "Place .ttf or .otf files in this folder", ["Pixlar"] = "Pixels",
        ["SLUMPMÄSSIG LÅTSPELARE"] = "RANDOM SONG PLAYER", ["GRUPPNAMN"] = "GROUP NAME", ["SNABBTANGENT"] = "SHORTCUT",
        ["Bygg situationsstyrda låtpooler"] = "Build situation-based song pools", ["En grupp per situation"] = "One group per situation",
        ["Skapa separata grupper för exempelvis mål, utvisning eller publik. Varje grupp sparas i profilen och får egen snabbtangent."] = "Create separate groups for goals, penalties or crowd moments. Each group is saved in the profile and has its own shortcut.",
        ["Det här kan väljas slumpmässigt"] = "These sounds can be selected at random", ["spelbara ljud"] = "playable sounds", ["berörda deck"] = "included decks",
        ["SÅ FUNGERAR TANGENTEN"] = "HOW THE SHORTCUT WORKS", ["1:a trycket spelar ett slumpvalt ljud. 2:a trycket tonar ut det. 3:e trycket väljer ett nytt ljud."] = "1st press plays a random sound. 2nd press fades it out. 3rd press selects a new sound.",
        ["Tomma deck, textblock och saknade ljudfiler används aldrig i poolen."] = "Empty decks, text blocks and missing audio files are never used in the pool.",
        ["Nya spelbara jinglar som senare läggs till i decket inkluderas automatiskt."] = "New playable jingles added to the deck later are included automatically.",
        ["Sök i hela profilen. Bästa träffen öppnar automatiskt rätt deck."] = "Search the entire profile. The best result automatically opens the correct deck.",
        ["Markera träffar"] = "Select results", ["Rensa deck"] = "Clear deck", ["Duplicera vald grupp"] = "Duplicate selected group",
        ["AUTOPLAY OCH SPELLISTOR"] = "AUTOPLAY AND PLAYLISTS", ["KÖAD SPELORDNING"] = "QUEUED PLAY ORDER",
        ["TILLGÄNGLIGA LÅTAR OCH JINGLAR"] = "AVAILABLE SONGS AND JINGLES", ["ÖVERGÅNG"] = "TRANSITION",
        ["Kön ligger kvar. Space tonar ut och återupptar samma låt; nästa låt startar automatiskt vid slutet."] = "The queue remains. Space fades out and resumes the same song; the next song starts automatically at the end.",
        ["Ladda spellista"] = "Load playlist", ["Spara spellista"] = "Save playlist", ["Spela nu"] = "Play now",
        ["Välj musikmapp"] = "Choose music folder", ["🎧 FÖRLYSSNING"] = "🎧 PREVIEW",
        ["Blanda nästa låt"] = "Shuffle next song", ["Loopa spellistan från början"] = "Loop the playlist from the beginning",
        ["Lägg till i kön"] = "Add to queue", ["Sök titel"] = "Search title", ["Visa nästa deckflik"] = "Show next deck tab",
        ["Total övergångstid. 75 % används för fade ut och 25 % för fade in."] = "Total transition time. 75% is used for fade out and 25% for fade in.",
        ["Språk / Language"] = "Language / Språk", ["Programspråk"] = "Application language",
        ["Språkbytet används nästa gång FloorballDJ startas."] = "The language change is applied the next time FloorballDJ starts.",
        ["Redo"] = "Ready", ["Redo för nästa jingle"] = "Ready for the next jingle",
        ["SPELAR NU"] = "NOW PLAYING", ["FADEAS UT"] = "FADING OUT",
        ["Standardprofilen kunde inte öppnas"] = "The default profile could not be opened",
        ["Senaste projektet återställt"] = "Latest project restored",
        ["Ett nytt projekt skapades"] = "A new project was created",
        ["Förlyssning via utgång 2"] = "Preview through output 2",
        ["Huvudutgång aktiv"] = "Main output active", ["Äldre XML importerad"] = "Legacy XML imported"
    };

    public static void SetLanguage(string language)
    {
        CurrentLanguage = LanguagePreferencesService.Normalize(language);
        var culture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "sv-SE");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    public static string Translate(string value)
    {
        if (!IsEnglish || string.IsNullOrEmpty(value)) return value;
        if (English.TryGetValue(value, out var translated)) return translated;

        return TranslatePrefix(value, "Sida ", "Page ")
            ?? TranslatePrefix(value, "Spelar: ", "Playing: ")
            ?? TranslatePrefix(value, "Tonar ut: ", "Fading out: ")
            ?? TranslatePrefix(value, "Köade ", "Queued ")
            ?? TranslatePrefix(value, "Tog bort ", "Removed ")
            ?? TranslatePrefix(value, "Kunde inte spela ", "Could not play ")
            ?? TranslatePrefix(value, "Ljuduppspelningen avbröts: ", "Audio playback stopped: ")
            ?? TranslatePrefix(value, "Standardprofil öppnad: ", "Default profile opened: ")
            ?? TranslatePrefix(value, "Aktiv profil: ", "Active profile: ")
            ?? TranslatePrefix(value, "Öppnade ", "Opened ")
            ?? TranslatePrefix(value, "Sparat ", "Saved ")
            ?? TranslatePrefix(value, "Autosparning misslyckades: ", "Autosave failed: ")
            ?? value;
    }

    private static string? TranslatePrefix(string value, string sourcePrefix, string targetPrefix) =>
        value.StartsWith(sourcePrefix, StringComparison.Ordinal) ? targetPrefix + value[sourcePrefix.Length..] : null;

    public static void TranslateElement(FrameworkElement element)
    {
        if (!IsEnglish) return;
        if (element is Window window && !BindingOperations.IsDataBound(window, Window.TitleProperty))
            SetIfTranslated(window.Title, translated => window.Title = translated);
        if (element is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty) && !string.IsNullOrEmpty(text.Text))
            SetIfTranslated(text.Text, translated => text.Text = translated);
        if (element is ContentControl content && !BindingOperations.IsDataBound(content, ContentControl.ContentProperty) && content.Content is string contentText)
            SetIfTranslated(contentText, translated => content.Content = translated);
        if (element is HeaderedContentControl header && !BindingOperations.IsDataBound(header, HeaderedContentControl.HeaderProperty) && header.Header is string headerText)
            SetIfTranslated(headerText, translated => header.Header = translated);
        // Menu and context-menu entries derive from HeaderedItemsControl, not
        // HeaderedContentControl. Without this branch the saved language was used
        // by view models while every static menu header remained in Swedish.
        if (element is HeaderedItemsControl itemsHeader && !BindingOperations.IsDataBound(itemsHeader, HeaderedItemsControl.HeaderProperty) && itemsHeader.Header is string itemsHeaderText)
            SetIfTranslated(itemsHeaderText, translated => itemsHeader.Header = translated);
        if (element.ToolTip is string toolTip)
            SetIfTranslated(toolTip, translated => element.ToolTip = translated);
    }

    public static void TranslateTree(DependencyObject root)
    {
        var visited = new HashSet<DependencyObject>();
        TranslateTree(root, visited);
    }

    private static void TranslateTree(DependencyObject root, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root)) return;
        if (root is FrameworkElement element) TranslateElement(element);

        if (root is ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
                if (item is DependencyObject dependencyItem) TranslateTree(dependencyItem, visited);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject dependencyChild) TranslateTree(dependencyChild, visited);

        int visualChildren;
        try { visualChildren = VisualTreeHelper.GetChildrenCount(root); }
        catch (InvalidOperationException) { return; }

        for (var index = 0; index < visualChildren; index++)
            TranslateTree(VisualTreeHelper.GetChild(root, index), visited);
    }

    private static void SetIfTranslated(string source, Action<string> setter)
    {
        var translated = Translate(source);
        if (!string.Equals(source, translated, StringComparison.Ordinal)) setter(translated);
    }
}
