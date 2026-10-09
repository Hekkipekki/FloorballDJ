# P4.4 — kvarvarande fönstervisning och sökåterställning

Desktopanalys genomförd 2026-10-05. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-show-analysis-v1`. Steget tillför mätning och en kontrollerad analys; det inför ingen ny optimering av produktens fönsterplacering eller sökbeteende.

Analysen pekar ut två separata förbättringsspår. Oförändrade sökresultat återställer låtlistan och bygger nya knappar i onödan. Fönstrets initiala placering/maximering påverkar också den synkrona visningen, men rutinen för att välja skärm är i sig kort. Språköversättningen förklarar inte huvuddelen av öppningstiden i detta test. **Nästa implementation är P4.5: undvik liståterställningar när synlighet och ordning är oförändrade.** Därefter följs placering och visning upp med ägarfönster och flera skärm-/DPI-fall.

## Uppföljning efter analysen

[P4.5 är nu infört och kontrollerat](PERFORMANCE-PHASE4-SEARCH-REFRESH.md): oförändrade sökresultat återställer inte längre låtlistan. Rapportens tabeller och nästa-prioritet nedan beskriver P4.4-beteendet före denna förbättring. [P4.6:s ägar-/skärmanalys är också genomförd](PERFORMANCE-PHASE4-PLACEMENT-ANALYSIS.md); P4.7:s Fit-minimimått/marginaler är nästa implementation.

## Metod och avgränsning

`RandomSettingsShowAnalysis` öppnar åtta typer av fönster med sex uppvärmda observationer per fall i blandad, reproducerbar ordning, plus en separat första processöppning: **49 visade fönster**. Den syntetiska profilen har 553 filer, 12 grupper och 40 låtar i det valda decket. Programmets resurser och motsvarande två språkhandlers används, med engelska som testspråk. Fönstren aktiveras lika i alla fall; det behövs när `EnsureHandle` har skapat och maximerat fönstret före `Show`. Denna matris kan därför inte användas som direkt före/efter-jämförelse med P4.3:s icke-aktiverande matris.

Mätningarna kördes i Release på samma desktop, 24 logiska processorer, .NET 10, 96 DPI och 1936 × 1048 DIP för de maximerade fönstren, utan samtidig byggning eller native testsvit. Testet har inget ägarfönster och ingen produktionshuvudvy, uppspelning eller personlig profil. De alternativa fallen är avgränsningar av kostnader, inte godkända produktändringar.

Rådata finns i `artifacts/performance/p4-show-analysis-validated/show-analysis.json`; sammanställningen i `analysis-summary.json` och enskilda placeringsevent i underkatalogen `events/`. Den oberoende profileringskörningen finns i `artifacts/performance/p4-show-profile/show.nettrace`, `show.speedscope.json` och `run/show-analysis.json`. Provkörningen i `p4-show-probe-fixed/` ingår inte i tabellernas medianer. Matrisen kördes före den sista ändringen av revisionsetikett och begränsningstext; mätlogiken är densamma i det nya paketet.

`EnsureHandle` skiljer skapandet av det underliggande Windows-fönstret från visningen och kan utlösa `SourceInitialized`. Därför jämförs hela öppningsförloppet, inte enbart den kortare efterföljande `Show`-tiden. Detta följer [Microsofts API-kontrakt](https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.windowinterophelper.ensurehandle?view=windowsdesktop-10.0).

## Fönstervisning och första resurser

Medianer av sex observationer. Öppning avser konstruktionsstart till förberedda data, nästa WPF-rendering och dispatcher-tomgång. MB är tilldelade byte på UI-tråden, inte kvarhållet eller maximalt processminne. Kolumnernas medianer ska inte summeras till en ny total.

| Fall | Konstruktion, ms | Arbete före Show, ms | Synkron Show, ms | Hela öppningen, ms | UI-allokering, MB |
| --- | --- | --- | --- | --- | --- |
| Normal produktväg | 3,32 | 0 | 129,69 | 228,81 | 9,04 |
| EnsureHandle före Show | 3,75 | 38,76 | 84,20 | 213,81 | 9,12 |
| Data förberedd före Show | 3,64 | 12,30 | 128,97 | 175,89 | 9,33 |
| Förinställd maximering, placeringsrutin utelämnad | 3,66 | 0 | 56,42 | 132,73 | 8,86 |
| Ikon borttagen efter konstruktion | 5,89 | 0 | 119,82 | 191,35 | 9,07 |
| Tomt innehåll, normal placering/tema | 0,12 | 0 | 124,18 | 136,35 | 0,11 |
| Tomt innehåll, förinställd maximering utan placering | 0,12 | 0 | 55,69 | 79,76 | 0,08 |
| Tomt innehåll, Window.ThemeMode=None | 0,12 | 0 | 120,30 | 143,91 | 0,11 |

Normalfallets Show ligger mellan 122,02 och 148,14 ms; hela öppningen mellan 182,43 och 433,37 ms. Fallet utan placeringsrutin ligger mellan 52,60 och 71,17 ms för Show. Detta är ett litet urval, inte en tillförlitlig svanspercentil eller ett generellt hastighetslöfte.

Produktvägen skapar fönstret, flyttar/storleksändrar det till arbetsytan i `SourceInitialized` och sätter därefter maximerat tillstånd. Det alternativa fallet sätter maximering redan före Show och hoppar över rutinen. Skillnaden ändrar flera delar av initieringsordningen och tar bort garantin om ägarens skärm. Den visar att visningssekvensen är värd att förbättra, men isolerar inte kostnaden till ett enskilt native anrop.

Placeringens hela handler tar normalt **2,59 ms**, varav `SetWindowPos` **2,58 ms**; tilldelningen av maximerat tillstånd tar **0,007 ms**. Tidsstämplarna är nästlade och får inte adderas. Med redan förberedda data tar placeringshandlern 17,87 ms, eftersom storleksändringen också kan utlösa layout med faktiskt innehåll. `SourceInitialized`-observatören körs efter placeringshandlern, som kan utlösa `Loaded` återinträdande. Eventens tidsordning är därför ingen ren uppdelning mellan oberoende steg.

Den separata stackprofilen, sju normalöppningar inklusive första processen, innehåller cirka **394 ms samplad tid** vid gränsen `DUCE.Channel.SyncFlush` under `Window.ShowHelper`, samt cirka 90 ms vid `HwndWrapper`-konstruktion. Det är samplad trådtid över hela profileringskörningen, inte kostnad per öppning, faktisk CPU-tid eller en upplöst Windows-/GPU-stack. EventPipe-profilen visar en synkron WPF/native gräns att följa upp; den bevisar inte en viss drivrutins-, DWM- eller animationsorsak. [Microsoft beskriver dessa profiler och deras begränsningar](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace).

Den första processöppningen är 575,07 ms: konstruktion 164,91 ms och Show 222,83 ms. Språkhandlers tar sammanlagt 2,34 ms i den första öppningen och **0,59 ms** i normalfallets uppvärmda median. Mätningen omfattar öppningens handlers fram till tomgång. Senare språk-/layoutarbete finns i interaktionsfaserna. Ikonförsöket tar bort ikonen efter konstruktionen och avlägsnar därför inte dess initiala XAML-resurskostnad. Temaförsöket behåller applikationens sammanslagna Fluent-resurser. Dessa experiment stödjer inte att ta bort ikon, tema, teckensnitt eller språkhantering. Första processöppning är heller inte ett prov av kall OS-/diskcache.

Att förbereda data före visning minskar totalen i denna fixture, men lämnar Show-kostnaden kvar och skjuter upp den första synliga återkopplingen. Det är inte infört i programmet. Att flytta HWND-skapandet tidigare flyttar också synkront arbete; den mindre Show-siffran är ingen motsvarande total besparing.

## Sökning och återskapade kontroller

Faser efter normalöppning, sex observationer. Listan har 32 byggda knappar när alla 40 låtar visas, inklusive bufferrader.

| Händelse | Synkront arbete, ms | Till rendering/tomgång, ms | UI-allokering, MB | Återställningar / nya knappar |
| --- | --- | --- | --- | --- |
| Sök en låt | 1,82 | 4,77 | 0,47 | 1 / 1 |
| Samma resultat med ändrad versalisering/blanksteg | 0,36 | 1,83 | 0,23 | 1 / 1 |
| Töm sökningen, återgå till 40 låtar | 0,24 | 16,14 | 2,78 | 1 / 32 |
| Anropa tom sökning igen med oförändrat resultat | 1,55 | 29,18 | 2,93 | 1 / 32 |
| Byt grupp | 3,19 | 50,47 | 5,34 | Ny listkontroll, 32 knappar |
| Återgå till gruppen | 2,90 | 32,15 | 5,14 | Ny listkontroll, 32 knappar |

Antalen återställningar och nya knappar är desamma i alla sex normalobservationer. Fasens byte inkluderar tillhörande layout, översikt och mätverktygets arbete; de är inte ett exakt pris för varje enskild knapp. Det oförändrade tomma anropet sker via testverktyget till den riktiga privata metoden. Produktens grupp-/setup-handlers kan också både tömma sökrutan och explicit anropa `ApplySearch("")`.

Koden anropar `VisibleJingles.Refresh()` för varje deck även om ingen `IsVisible` ändras. Den filtrerade vyn skickar då en återställning. Panelens återställningsväg tar bort kontroller och generatorns återanvändningskö, och nästa layout bygger visningsytan igen. Återanvändningen vid vanlig rullning fungerar fortfarande; den här kostnaden är ett annat förlopp. Gruppbyten använder nya mallar/listkontroller och ska inte redovisas som samma panels återställning.

## Infört och verifierat

- Valbara, korrelerade tidssteg för placeringshandlern, `SetWindowPos` och maximering. Med mätning avstängd används de befintliga inaktiva strukturerna; produktens placeringsbeteende behålls.
- En separat `--show-analysis`-körning med öppningsfaser, språk-/dispatcherdata och faktiskt kontrollarbete vid sökning/gruppbyte. Endast den interna analyskonstruktorn kan hoppa över placering; vanliga anrop använder fortsatt placeringsrutinen.
- Alla 49 matrisfönster och sju profilerade fönster passerar kontroller av kompletta filtrerade data, varje synlig låtrad, begränsat kontrollantal och oförändrade ursprungliga inställningar. Samtliga matrisfönster släpps vid den framtvingade GC-punktkontrollen. Alla diagnostiksessioner avslutas utan tappade event eller skrivfel.
- Releasebygget, hela prestanda-/beteendesviten och rapportkontrollerna i Windows PowerShell 5 passerar. Sviten omfattar tidigare sök-/sorterings-/fokus-/kortpixel-/Spara-/Avbryt-kontroller och huvud-/förlyssning. Bygget har den befintliga NU1510-varningen.

## Nästa implementation och fortsatt kvalificering

**P4.5:** avgör per deck om synliga identiteter/ordning faktiskt ändras och undvik `Refresh` vid oförändrat resultat. Fortsätt beräkna bästa deck och behåll sökningens normalisering, rangordning, träffantal, val och nya/lata grupper. Ett globalt cacheat sökord är inte tillräckligt när grupp, innehåll eller sortering ändras. Verifiera att likvärdig och upprepad tom sökning inte återskapar knappar eller stör fokus, samtidigt som riktiga resultatändringar fortfarande visas omedelbart. Behåll P4.3:s visningsgränser och kompletta modell. Eventuell återanvändning över riktiga filteråterställningar utvärderas separat efter detta.

**Följande visningsspår:** pröva en kortare initial placering/maximeringssekvens med samma ägarskärm, arbetsyta, minimistorlek, återställda storlek och fokus/modalitet. Testa normala/maximerade ägare, flera skärmar, alternativ DPI och mindre arbetsytor före produktändring. Fortsätt vid behov med native ETW för synkron flush; inför inte experimentets borttagna placeringsrutin som lösning.

Det finns ännu ingen ny faktisk ProBook-mätning av denna revision. Livefrysningen, uppspelning i riktiga huvudvyn, fysisk presentation/ljudstart, alternativa DPI/skärmar, verkliga profiler och långa sessioner återstår att kvalificera. Se [aktuell roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p44-analyzed-remaining-showresources-and-search-reset) och [instruktioner för mätpaketet](PERFORMANCE-CAPTURE.md).
