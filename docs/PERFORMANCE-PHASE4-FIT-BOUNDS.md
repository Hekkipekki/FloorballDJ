# P4.7 — minimimått och marginaler inom samma arbetsyta

Infört och kontrollerat 2026-10-05 i arbetskopian. Version **0.40.0-rc.1** behålls; ingen produktionsrelease har publicerats. Mätpaketets revision är `phase4-fit-bounds-v1`, med `fitGeometry=margin-aware-minimum-size-v1` och `fitChecks=p4.7-fit-checks-v1`.

`FitToOwnerMonitor` använder nu samma marginaljusterade yta för dialogens minimimått, slutliga storlek och centrering. Det reproducerade [P4.6-kantfallet](PERFORMANCE-PHASE4-PLACEMENT-ANALYSIS.md) är rättat: stora minimimått kan inte längre motverka den begärda marginalen i de kontrollerade fallen. Detta är en placerings-/storleksrättning, inte en uppmätt förbättring av hotkey- eller ljudstartstid.

## Uppföljning efter P4.7

[P4.8:s huvudvyanalys är genomförd](PERFORMANCE-PHASE4-MAIN-VIEW.md) och rättar dessutom sessionsbindningens onödiga uppstartssparning. Nästa profilering är P4.9:s rendering/layout/allokering. Nästa-prioriteten längre ned beskriver P4.7-leveransen.

## Förändring

`CalculateFit` beräknar först det tillgängliga utrymmet i fysiska pixlar efter marginalen på 12 pixlar per sida. Den omvandlar denna storlek till WPF-mått och klampar minimimåtten till samma utrymme. Slutstorleken respekterar både det tillåtna minimumet och utrymmets övre gräns, avrundas till pixlar och centreras efter den slutliga storleken. WPF får motsvarande storlek tillbaka. Storleksvektorer används för omvandlingen så att ett koordinatursprung inte påverkar bredd/höjd.

Den tidigare undre storlekspolicyn 320 × 220 WPF-enheter behålls när arbetsytan rymmer den; den får inte tvinga dialogen utanför en mindre yta. Matematikens marginal minskas när en extremt liten yta inte ens rymmer två marginaler och en pixel. Detta garanterar en positiv planerad rektangel, inte att Windows kan visa ett användbart fönster med sin egen minsta ramstorlek på en sådan yta.

Vanliga storlekar och minimimått behålls. Ägarskärm, normalt fönstertillstånd, centrering och befintliga `NOACTIVATE`/`NOZORDER`-flaggor används fortsatt. Ingen styrning av OS-förgrunden läggs till. `SizeToContent`-inställningen tas inte bort. Maximeringsvägen använder samma tidigare placerings-/minimimåttslogik; P4.6:s alternativa maximeringssekvenser är fortfarande experiment.

Omvandlingen använder WPF:s [TransformFromDevice](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.compositiontarget.transformfromdevice?view=windowsdesktop-10.0), medan native [SetWindowPos tar storlek i pixlar och stöder de bevarade placeringsflaggorna](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos).

## Före och efter på tillgängliga skärmar

P4.6:s rådata finns i `artifacts/performance/p4-placement-analysis-validated/placement-analysis.json`. Efterkontrollen med samma placeringsverktyg, en repetition per fall, ligger i `artifacts/performance/p4-fit-placement-after/placement-analysis.json`; sammanställningen i `comparison.json`.

| Fall | Före | Efter |
| --- | --- | --- |
| Arbetsyta | 1920 × 1032 pixlar | Samma |
| Stor begärd dialog, minimum 2500 × 2000 | Native storlek 1920 × 1032, start 12 pixlar in från kanten; höger/nedre kant 12 pixlar utanför arbetsytan | Native storlek och tillåtet minimum 1896 × 1008; 12-pixelsmarginal på båda sidor |
| Sex stora dialoger i placeringsmatrisen | 0 av 6 helt inom arbetsytan | 6 av 6 inom arbetsytan |
| Sex vanliga Fit-dialoger, 1000 × 700, minimum 700 × 400 | 6 av 6 inom arbetsytan | Samma storlek/minimum; 6 av 6 inom arbetsytan |
| Slumpinställningarnas maximerings-/återställningsväg | P4.6:s referens | Alla 13 kontrollerade baselineöppningars maximerade, återställda och återmaximerade native/WPF-ramar är identiska |

De faktiska skärmarna är tre 1920 × 1080-skärmar vid 96 DPI, med positiva, noll och negativa koordinater. Eftermatrisen omfattar **61 barnfönster** och samma ägar-/modalitets-/draftkontroller som P4.6. Alla 12 Fit-fall har nu `withinWork=true`. De 61 stängda barnfönstren släpps vid framtvingad GC; det är en punktkontroll, inte ett långtidsprov. Matrisen kördes utan samtidiga native testsviter eller byggen. Tidsvariation i detta rättningssteg används inte som belägg för en generell hastighetsförbättring.

## Tester

Det nya native regressionstestet faller på den tidigare rutinen med den reproducerade för stora rektangeln och passerar efter rättningen. `WindowFitChecks` ingår nu i den vanliga prestanda-/beteendesviten och kan köras separat med `--fit-checks`.

- **60 faktiskt visade Fit-dialoger:** varje skärm med normalt/maximerat ägarfönster; vanliga, för stora, minimum-större-än-begärd och fraktionella storlekar i både modal och vanlig visning. Native slutrektangel, marginal, centrering, ägarskärm och minimimått kontrolleras.
- Riktiga `TextPromptWindow` med redigering/Spara och `ShortcutCaptureWindow` med automatisk höjd/Avbryt ingår. Textens trimning, modalens resultat, befintlig snabbknapp, WPF-fokus och native spärrning/frigöring av ägaren verifieras.
- **21 008 beräkningsfall:** mindre arbetsytor, signerade ursprung, fraktionella värden, 100/125/150/175/200/300-procents skalning och oberoende axlar samt reproducerbara slumpfall. Planerad rektangel ligger inom ytan, minimimått motsäger inte slutstorlek, pixel/DIP-konvertering och centrering stämmer, och vanliga passande mått behålls. Dessa fall ändrar inga OS-skärminställningar och är inte fysisk blandad-DPI-kvalificering.

Riktade resultat finns i `artifacts/performance/p4-fit-checks-validated/window-fit-checks.json`. Releasebygget, hela prestanda-/beteendesviten och Windows PowerShell 5-rapportkontrollerna passerar. Sviten behåller tidigare kontroller av sökning, sortering, val, Spara/Avbryt, ljudutgångar och huvud-/förlyssning. Den befintliga NU1510-varningen kvarstår.

OS-förgrund efter stängning registreras som observation. P4.6:s paketavvikelse är fortfarande en öppen fråga; WPF-fokus och en frigjord native ägare bevisar inte att Windows accepterat OS-aktivering. Detta steg försöker inte dölja eller forcera resultatet.

## Leverans och nästa analys

Mätpaketets **Fit-Checks.cmd** skriver `window-fit-checks.json` till en unik `checks/fit-*`-katalog och visar syntetiska testfönster. Lämna dem utan handpåläggning medan kontrollerna körs. Inga personliga profiler används och inget ljud spelas i den separata Fit-körningen. `Placement-Analysis.cmd` finns kvar; P4.6:s äldre `withinWork=false` är historiskt före-beteende, medan samma stora fall nu förväntas passa. Produktionens placeringsspår identifieras som `source-initialized-fit-envelope-v1`.

**P4.8 är nästa avgränsade analys:** mät slumpinställningarna i faktisk huvudvy/modal användning, idle och med aktiv uppspelning. Skilj konstruktions-/förberedelse-/layoutarbete och normal modal spärrning från dispatcherfördröjning, samt registrera aktiveringsförsök och OS-förgrund före/under/efter dialogen. Följ P4.6:s paketerade fokusavvikelse utan att framtvinga förgrunden. Behåll profil-/licens-/ljudbeteende och gör ingen diagnos av ProBook-frysningen från enbart förenklade fönster.

Fysisk blandad DPI, andra arbetsytor, skärmflytt under visning, senare innehållsändringar i automatiskt storleksanpassade dialoger, verklig profil/huvudvy, OS-input/förgrund, ProBook och långa sessioner återstår att kvalificera. Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p47-implemented-fit-minimum-sizemargin-consistency) och [mätpaketets guide](PERFORMANCE-CAPTURE.md).
