# P4.8 — slumpinställningar i faktisk huvudvy

Genomfört 2026-10-05 i arbetskopian. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-main-view-analysis-v1`. Steget tillför en huvudvymätning och rättar en återkoppling från initial sessionsbindning till sparning. Tidigare funktioner, placeringsrättning och ljudbackendens standardbeteende behålls.

## Uppföljning efter P4.8

[P4.9:s metod-/allokeringsprofil är genomförd](PERFORMANCE-PHASE4-RENDER-PROFILE.md). Nästa implementation är P4.10:s giltiga inneboende kortmått. Nästa-prioriteten längre ned beskriver läget vid P4.8-leveransen.

## Uppstartsfel hittat och rättat

Den första körningen laddade inte den avsedda autosave-profilen. Rå uppstartsevent i `artifacts/performance/p4-main-view-probe/startup/events.jsonl` visar sparning före profilinläsning, följt av fyra standarddeck och **noll ljudjinglar** i stället för fixtureprofilens 553. Sessionsknappen är enkelriktat bunden till `Settings.TrackSession`. Initial `Checked` körde `SessionToggle_Changed`, som sparade även när modellens värde redan var samma. Standardprojektet kunde därför skrivas till autosave-filen före inläsning.

Hanteraren avstår nu från sparning när värdet redan stämmer. Riktiga ändringar fortsätter att slå av/på sessionsspårning, nollställa räknare vid avstängning och spara. Detta minskar oavsiktligt spararbete och förebygger det reproducerade uppstartsfelet. Det är inte en global avstängning av autosave eller ett nytt profilformat.

Ett test med den faktiska huvudvyn kontrollerar ursprungliga deckidentiteter och byte-för-byte oförändrad autosave efter uppstart. Det kontrollerar också verklig av-/påslagning av sessionsknappen, återställda räknare, sparade flaggor och huvudvyns normala flush/dispose/stängning. Testet ingår i ordinarie prestandasvit och kan köras separat med `--main-startup-checks`. Den tidigare assertionen om rätt inläst profil föll före rättningen och passerar efter den.

## Metod

`MainViewSettingsAnalysis` använder den riktiga `MainWindow`, `MainViewModel`, inläsningen av en separat syntetisk autosave, befintliga timers, huvudvyns resurser och den riktiga `RandomPlayerSettings_Click`-hanteraren med `ShowDialog`. Händelsehanteraren anropas programmatiskt, utan OS-musklick eller hotkey. Språk-/menyhandlers speglar applikationen. Profilen innehåller **553 ljudjinglar, 14 deck och 12 grupper**, med 40 låtar i det visade decket. Två jinglar delar den tysta testfilen, så färska filkontroller kan delas för samma sökväg.

Den separata första editoröppningen följs av sex öppningar i vila och sex med huvudljud och förlyssning i blandad, reproducerbar ordning: **13 faktiska modala öppningar**. Aktiva fall använder en 90-sekunders tyst WAV, 48 kHz/16 bitar/stereo, med befintlig standardenhet uttryckligen vald för båda testutgångarna. Backend är ordinarie per-röst/legacy. Övriga bibliotekssökvägar är tillgänglighetsfixtures, inte codecprov. Huvudpositionen kommer från den publicerade huvudutgångssnapshoten; förlyssningspositionen kommer från den separata sekundära snapshoten. Den senast valda rösten används inte som ersättning för båda läsarna.

En begränsad bakgrundssampler kan ha högst en väntande callback vid normal dispatcherprioritet. Den jämför köfördröjning under 200 ms före öppning och fram till förberedda data, nästa WPF-rendering och tomgång. Skriptet håller dialogen ytterligare 180 ms före Avbryt. Modal livstid inkluderar denna väntan och stängning och är därför ingen öppningstid. Native spärrning av ägaren skiljs från faktisk köfördröjning.

Körningen använder .NET 10.0.10 och 24 logiska processorer på desktopen vid aktuell DPI. Personliga profiler används inte. `App.OnStartup`-språkval och licensutvärdering ingår inte: licensinstansen lämnas i `None`, utan anrop till licenslagring eller nätverk. Ingen licensregel ändras i produkten. En intern konstruktorsvariant låter testets dispatcher fortsätta efter samma sparnings-/disposal-/stängningsflöde; offentliga konstruktorer behåller applikationsavslutet. Detta är en mätning av verklig huvudvy, inte en kvalificerad autentiserad produktstart på användarens profil.

Rådata: `artifacts/performance/p4-main-view-analysis-final/main-view-analysis.json`, `startup/` och per-öppning `events/`. Statistik: `analysis-summary.json`. Matrisen kördes utan samtidiga native testsviter eller byggen. Den sista revisions-/begränsningstextändringen och det separata ordinarie regressionstestet tillkom efter matrisen; mät-/rättningslogiken är densamma.

## Resultat

Medianer av sex observationer per fall. MB avser UI-trådens tilldelade byte, inklusive relevant gränssnitts-/probarbete, inte maximalt eller kvarhållet processminne. Kökolumnerna visar medianen av varje öppnings uppmätta median respektive maximum; de är inte en generell svanspercentil.

| Fall | Till redo/rendering/tomgång, ms | Konstruktion via meny, ms | UI-allokering, MB | Kömedian före → under, ms | Median av öppningarnas största köfördröjning, ms |
| --- | --- | --- | --- | --- | --- |
| Vila | 131,04 | 5,76 | 9,37 | 0,24 → 3,28 | 40,74 |
| Huvudljud + förlyssning | 122,45 | 7,24 | 9,41 | 0,05 → 2,52 | 37,05 |

Öppningarna varierar 90,47–216,78 ms i vila och 87,36–157,98 ms under uppspelning. Skillnaden mellan dessa små urval bevisar inte att uppspelning gör öppningen snabbare. Första editoröppningen efter huvudvystart är **205,01 ms och 11,01 MB**; detta är inte kall applikationsstart. Rendering-prioriterade callbacks i öppningsfaserna når **76,79 ms** i denna körning. Hela öppningens allokering är fortfarande omkring 9,4 MB, medan själva konstruktions- och biblioteksprepareringsspannen är kortare. Spannen är nästlade och ska inte adderas som separata kostnader. Exakt metod-/allokeringsorsak är ännu inte profilerad i denna huvudvymatris.

Alla sex aktiva fall visar framsteg för både huvudutgångens och förlyssningens läspositioner samt ordinarie 50-ms-publikationer i viewmodel medan den modala dialogen är öppen. Detta bevisar fortsatt läs-/uppdateringsaktivitet, inte hörbar eller helt störningsfri kontinuitet, fysisk skärmvisning eller fysisk key-to-speaker-tid. Huvudvyn är avsiktligt native spärrad under modalens livstid. Det beteendet ska inte automatiskt räknas som en dispatcherfrysning.

Samtliga öppningar visar riktiga låtkort, lämnar liveinställningar och sparad fixtureprofil oförändrade vid Avbryt och frigör huvudvyn efter stängning. De 13 stängda editorfönstren släpps vid framtvingad GC. Det är en punktkontroll, inte ett långtidstest.

## Förgrundsfrågan har fått rätt kontext

Den fulla matrisen registrerar **Activate=false och ett annat fönster i OS-förgrunden före samtliga 13 öppningar**. Samma kategori ligger kvar under och efter dialogerna. Detta kan inte användas som belägg för att en redan aktiv huvudvy tappade förgrunden vid återgång. Det tidigare korta provet i `p4-main-view-probe-fixed/` hade tre accepterade aktiveringar och övergången **huvudvy → dialog → huvudvy**. Provet ingår inte i tabellens medianer och hade ännu inte den senare explicit valda tvåutgångs-fixturen, så det används enbart som en separat förgrundsobservation.

WPF:s [Activate returnerar om aktiveringen lyckats och använder Win32:s regler](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.activate?view=windowsdesktop-10.0). [Windows begränsar vilka processer som kan ta förgrunden](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow). Den exakta nekande orsaken fastställs inte av dessa observationer. Inget tvångsaktiveringsanrop eller ändrad OS-policy läggs till. P4.6:s äldre paketresultat saknar dessa före-/försöksdata och får inte retroaktivt tilldelas en säker orsak. En användarinitierad, berättigad OS-input/förgrundskörning är fortfarande en öppen kvalificering.

Det ofullständiga försöket i `p4-main-view-analysis-validated/` stoppades när testprofilen saknade uttryckligt vald förlyssningsutgång och programmets vanliga konfigurationsdialog öppnades. Det ingår inte i resultatserien eller som en långsam öppningsobservation. Testfixturen korrigerades; produktens utgångskontroll behålls.

## Kontroller och leverans

Releasebygget, hela prestanda-/beteendesviten inklusive det nya huvudvyregressionstestet och Windows PowerShell 5-rapporterna passerar. Tidigare val-/sök-/sorterings-/Fit-/ljud-/Spara-/Avbryt-kontroller behålls. Befintlig NU1510-varning kvarstår. P4.8 tillför valbara `MainWindowInitialization`/`MainWindowReady`, menykonstruktion/modal-livstid/retur och fönstrets Loaded/Activated/Deactivated-markörer. Diagnostik skriver inga titlar, mediavägar eller licensinnehåll.

**Main-View-Analysis.cmd** i mätpaketet kör 13 öppningar, spelar endast tyst testljud genom befintlig enhet och skriver `main-view-analysis.json` samt uppstarts-/öppningsevent i en unik `checks/main-view-*`-katalog. **Main-Startup-Checks.cmd** kör den korta uppstarts-/session-/stängningsregressionen. Lämna testfönstren utan handpåläggning. Inga personliga profiler eller utgångsinställningar ändras.

## Nästa steg

**P4.9:** profilera återstående rendering/layout och UI-allokering i denna huvudvyscen, med tidsmätningen och profileringskörningen separat. Lokalisera metod-/resurskostnaden bakom de långa Render-callbacksen och cirka 9,4 MB per öppning innan kontroll-/mall-/resursåteranvändning införs. Bevara visuell/layout-/fokusparitet och kompletta draft-/Spara-/Avbryt-regler. Behandla stora gruppbyten/riktiga filteråterställningar som separata arbetsförlopp. Foreground-prover ska klassificeras från föredata och aktiveringsresultat.

Verklig profil/ProBook, codec-/DPI-/enhetsvariation, användarinitierad OS-input/förgrund, fysisk ljud-/skärmpresentation och långa sessioner återstår. Den rapporterade livefrysningen är därför ännu inte kvalificerad som löst. Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p48-analyzed-actual-main-viewmodal-opening-and-foreground) och [mätguiden](PERFORMANCE-CAPTURE.md).
