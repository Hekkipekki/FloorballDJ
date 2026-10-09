# P4.9 — metod- och allokeringsprofil för huvudvyscenariot

Genomfört 2026-10-05. Version **0.40.0-rc.1** behålls. Revision `phase4-render-profile-v1` tillför testmarkörer, profileringsscript och analysverktyg. Ingen kontrollmall, teckensnittsinställning eller panelalgoritm optimeras i detta analyssteg.

Nästa implementation är **P4.10: undvik onödig upprepad mätning av låtkortets inneboende storlek**, med korrekt invalidering när innehåll, teckensnitt, mall eller skalning ändras. Profilen ger stöd för att börja i text-/kortmätningen; den anger ingen förväntad generell hastighetsvinst.

## Uppföljning efter P4.9

[P4.10 är infört och kontrollerat](PERFORMANCE-PHASE4-INTRINSIC-MEASURE.md). Den lilla mätvillkorsändringen återanvänder giltig WPF-mätning; medianallokering minskar cirka 0,55–0,65 procent utan verifierad generell tidsvinst. Rapportens profil och nästa-prioritet beskriver före-beteendet. P4.11 analyserar större kvarvarande mall-/kontrollarbete.

## Metod och filer

Samma faktiska huvudvy/meny-/modalfixtur som [P4.8](PERFORMANCE-PHASE4-MAIN-VIEW.md): 553 jinglar, 14 deck, 12 grupper, 40 låtar i valt deck, tyst huvud-/förlyssning och privata testprofiler. Ursprungliga profiler, licenslagring och OS-inställningar används inte. Licensutvärdering och fysisk OS-input ingår inte. Skriptad modal väntetid och stängning behålls.

Två separata serier om **13 öppningar vardera** kördes sekventiellt: första editoröppning samt sex idle- och sex aktiva fall. Den vanliga mätningen kördes utan profiler och utan samtidig byggning/native testsvit. Profileringsserien använder `dotnet-trace 10.0.745401`, `dotnet-sampled-thread-time,gc-verbose`, Release-symboler och testprovidern `FloorballDJ-Analysis`. Markörerna `OpeningStart`/`OpeningReady` avgränsar varje öppning och anger den native UI-tråden. Uppstart, ljudstart före menyn, hålltid och stängning utesluts ur de avgränsade profilresultaten.

- Vanlig tids-/beteendemätning: `artifacts/performance/p4-render-unprofiled/main-view-analysis.json`.
- Profilerad körning och råspår: `artifacts/performance/p4-render-profile/run/`, `main.nettrace` och `main.speedscope.json`.
- Markerad metod-/allokeringssammanställning: `opening-trace-summary.json`; totalsammanställning: `analysis-summary.json` i profilkatalogen.
- Separat kort scriptkontroll: `artifacts/performance/p4-render-script-check/`. Den ingår inte i tabellernas observationer.

Analyseraren ligger i `tests/FloorballDJ.TraceAnalysis` och använder Microsofts TraceEvent **3.1.23**, samma version som den lokala profileraren. Den är ett separat utvecklarverktyg och tillför ingen beroendereferens i appen. `scripts/Start-MainViewProfile.ps1` kör profileraren i en ny katalog och installerar inga verktyg. Scriptet är verifierat under Windows PowerShell 5. Den sista revisionsetiketten ändrades efter huvudserierna; panel-/mätlogiken är oförändrad.

[Microsofts dotnet-trace-dokumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) beskriver trådstacksampling och GC-profiler. Samplad trådtid kan inkludera blockering och är inte CPU-tid. GC-allokeringsprov är viktade uppskattningar, inte exakta object-/typantal eller kvarhållet minne. Windows/EventPipe-spåret upplöser inte kernel-/GPU-/drivrutinsorsaker.

## Vanlig mätning utan profiler

Medianer av sex observationer per fall på samma desktop, .NET 10.0.10 och 96 DPI. MB är tilldelade byte på UI-tråden under öppningsfasen, inklusive relevanta mät-/gränssnittsoperationer, inte maximalt processminne eller fysisk presentation.

| Fall | Till redo/rendering/tomgång, ms | UI-allokering, MB | Median av öppningarnas största köfördröjning, ms |
| --- | --- | --- | --- |
| Vila | 109,21 | 9,41 | 29,68 |
| Huvudljud + förlyssning | 118,55 | 9,41 | 36,79 |

P4.8:s tidigare serie hade 131,04/122,45 ms. Ingen optimering är införd i P4.9 som kan tillskrivas skillnaden. Profilerade tidsvärden används inte som denna baseline. Båda serierna passerar profil-/Avbryt-/modalitets-/läspositions-/stängningskontrollerna; den vanliga seriens 13 editorfönster släpps vid GC-punktkontrollen. Hörbar störningsfrihet, ProBook och långa sessioner är fortsatt öppna.

## Vad spåren visar

I de tolv uppvärmda öppningsfaserna finns **1 054 AllocationTick-prov, alla med stack**, och **1 043 UI-trådstacksprov**. Parsern rapporterar noll tappade event och alla 26 markörer för de 13 öppningarna finns. Detta kvalificerar inte varje stack/symbol eller all samplingsnoggrannhet.

De viktade allokeringsproven motsvarar totalt **112 490 616 byte** över de tolv uppvärmda öppningarna. Följande är en disjunkt heuristisk indelning: första matchande stackursprung väljs i tabellens ordning. En rad är inte en självständig bibliotekskostnad.

| Första matchande stackursprung | Viktade prov, MiB | Andel |
| --- | --- | --- |
| SongWrapPanel, mätning/placering | 29,44 | cirka 27,4 % |
| Annan textlayout | 18,92 | cirka 17,6 % |
| Annan uppbyggnad av kontrollmallar | 16,25 | cirka 15,1 % |
| Fönstrets XAML-inläsning | 6,09 | cirka 5,7 % |
| Editorförberedelse | 5,79 | cirka 5,4 % |
| Övrigt | 30,79 | cirka 28,7 % |

Individuella inklusiva stackar visar cirka **35,02 MiB** under `FrameworkElement.ApplyTemplate`, **22,43 MiB** under låtpanelens `MeasureOverride`, **7,01 MiB** under dess `ArrangeOverride` och **9,55 MiB** under `TreeWalkHelper.InvalidateOnTreeChange`. Dessa siffror överlappar tabellens kategorier och varandra och får inte summeras. Vanliga typprov är WPF:s effective-value-/ärvda-egenskapsarrayer och textformateringsobjekt. Vikten tillskrivs typen/stacken vid provtillfället; den är inte exakt minnesåtgång för varje objektklass.

Metodstacksproven innehåller **603** träffar under `ContextLayoutManager.UpdateLayout`, **364** under textformateringens `FormatLine`, och **261** med låtpanelursprung. Flera prov ligger även i GC-/monitorväntan. Inklusiva träffar är inte oberoende, och proverna ska inte översättas till millisekunder eller en viss drivrutinsförklaring. Profileringen pekar på layout/text/malluppbyggnad framför en ny omskrivning av filförberedelse eller språkhantering.

## Avgränsad nästa förändring

Källgranskningen visar att `SongWrapPanel.MeasureOverride` varje gång mäter ett faktiskt första kort med obegränsad yta, därefter mäter synliga kort med kortets begränsade mått. Första kortet kan alltså växla mätvillkor vid upprepade layoutpass. WPF:s [Measure-kontrakt](https://learn.microsoft.com/en-us/dotnet/api/system.windows.uielement.measure?view=windowsdesktop-10.0) och beroende av tillgänglig storlek gör detta till ett konkret arbete att kontrollera. Spåren bevisar inte att allt panel-/textarbete kommer från just detta kort eller hur mycket en cache kommer att spara.

**P4.10:** mät/följ de upprepade inneboende kortmätningarna och återanvänd ett giltigt mått där det är säkert. Invalidera vid verkliga ändringar av innehåll, mall, teckensnitt och DPI; använd ingen global gissad korthöjd. Behåll pixel-/radbrytningsparitet, samtliga synliga rader, kontrollgränser, offscreen-val, rullning, fokus/navigering, sortering/sökning, hela-decket och Spara/Avbryt. Jämför med samma huvudvyscen utan profiler innan en hastighetsvinst anges. Mallförenkling och större innehållsåteranvändning är separata möjliga spår.

## Kontroller och återstående kvalificering

Releasebygget, hela befintliga prestanda-/beteendesviten, uppstarts-/sessionsregressionen och PowerShell 5-rapporterna passerar. Det separata TraceAnalysis-projektet bygger utan varningar. En markerad treöppningskörning analyseras korrekt; ett äldre omarkerat spår avvisas utan sammanställning. Den befintliga appvarningen NU1510 kvarstår. Testprovidern loggar endast försöksnummer, aktivt/idle och UI-tråd, inga titlar eller vägar.

Förgrund bedöms fortsatt från föredata/Activate-resultat. Privat fixture, kontrollerade media och WPF-proxyvärden ersätter inte faktisk profil, användarinitierad OS-input, fysiskt ljud/skärm, codec-/DPI-/enhetsvariation, ProBook eller långtidsbruk. Den rapporterade livefrysningen är inte kvalificerad som löst. Ett ProBook-genomkörningsprov kan göras redan nu; hela roadmapen behöver inte bli färdig först. Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p49-profiled-renderlayout-and-ui-allocation) och [mätguiden](PERFORMANCE-CAPTURE.md).
