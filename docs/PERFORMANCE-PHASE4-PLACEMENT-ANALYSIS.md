# P4.6 — visning med ägarfönster och bevarad skärmplacering

Desktopanalys genomförd 2026-10-05. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-placement-analysis-v1`; analysverktyget identifieras som `p4.6-placement-analysis-v1`. Produktens delade placeringsrutin är oförändrad av detta steg. Tidigare förbättringar, inklusive P4.5:s sökåterställning, behålls.

Med ett öppet ägarfönster är den uppvärmda synkrona visningen omkring **28 ms** i testet. De alternativa sekvenserna ger några få millisekunders skillnad här. Enbart tidigare maximering ändrar återställd storlek och är därför inte en motsvarande produktändring. Två mer omfattande experiment behåller geometrin på de tillgängliga skärmarna, men är ännu inte kvalificerade för blandad DPI eller produktens alla fönster. Ett separat befintligt kantfall i `FitToOwnerMonitor` är reproducerat: stora minimimått kan tvinga dialogens ytterrektangel utanför arbetsytan.

**Nästa implementation är P4.7: samordna minimimått och marginaler i Fit-rutinen**, med kontroller av skalning, ägarskärm och faktisk placering. Profilering av slumpinställningarna i den riktiga huvudvyn under uppspelning kvarstår som ett separat krav. P4.4:s visningstid utan ägarfönster ska inte användas som förväntad produktbesparing.

## Uppföljning efter P4.6

[P4.7:s Fit-rättning är nu införd och kontrollerad](PERFORMANCE-PHASE4-FIT-BOUNDS.md). De stora Fit-fallen ryms efter rättningen. Denna rapports `withinWork=false`, tabeller och nästa-prioritet beskriver det historiska före-beteendet. OS-förgrundsfrågan är fortsatt öppen; inga alternativa maximeringssekvenser är införda. Nästa analys är P4.8:s faktiska huvudvy/modal öppning med uppspelning och förgrundsdata.

## Metod och omfattning

`WindowPlacementAnalysis` använder riktiga WPF-ägarfönster och faktiskt visade slumpinställningar med samma syntetiska 553-filers/12-gruppers profil och 40 låtar i valt deck. Programmets resurser och motsvarande språkhandlers används. Ägaren placeras på var och en av de tillgängliga skärmarna och provas i normalt och maximerat tillstånd. Ingen skärmupplösning, DPI-inställning eller annan OS-inställning ändras.

På den aktuella desktopmiljön finns **tre 1920 × 1080-skärmar**, alla vid **96 DPI**, med 1920 × 1032 pixlars arbetsyta. Testet täcker positiva, noll och negativa skärmkoordinater. Det kördes i Release, .NET 10.0.10 och 24 logiska processorer, utan samtidig byggning eller native testsvit.

Fyra sekvenser provas i blandad, reproducerbar ordning med sex uppvärmda observationer per skärm/ägartillstånd: **144 vanliga visningar**. Dessutom används en första editoröppning efter att ägaren visats, **24 riktiga ShowDialog-visningar** och **12 Fit-dialoger**. Totalt **181 barnfönster**, utöver de tre ägarfönstren. Varje editor återställs till normalt tillstånd och maximeras igen efter öppningsmätningen. Detta mäter både native ytterrektangel och återställningsrektangel samt WPF:s återställningsdata, minimimått och DPI.

Rådata: `artifacts/performance/p4-placement-analysis-validated/placement-analysis.json`, med per-fönster-diagnostik i `events/` och statistik i `analysis-summary.json`. Provkörningen i `p4-placement-probe-2/` ingår inte i medianerna. Matrisen kördes före den sista ändringen av revisionsetiketten; mätlogiken och placeringsbeteendet är samma i det nya paketet.

## Jämförda sekvenser och resultat

| Sekvens | Experimentets innehåll | Synkron Show, ms | Till redo/WPF/tomgång, ms | UI-allokering, MB |
| --- | --- | --- | --- | --- |
| `baseline` | Nuvarande SourceInitialized-placering följd av maximering | 28,42 | 92,61 | 8,93 |
| `early-max-retain` | Maximerat tillstånd före Show, placeringshandlern behålls | 26,15 | 95,95 | 8,75 |
| `early-work-retain` | Arbetsytans position/storlek sätts före Show och tidig maximering; handlern behålls | 26,63 | 92,21 | 8,75 |
| `early-work-native` | Arbetsytan sätts före Show och tidig maximering; den befintliga handlern utelämnas | 25,79 | 87,86 | 8,75 |

Tabellen visar medianer av **36 uppvärmda observationer per sekvens**, fördelade över tre skärmar och två ägartillstånd. Show-intervallen överlappar: baseline 25,37–41,98 ms, tidig maximering 24,40–44,79 ms, tidig arbetsyta med handler 25,02–42,43 ms och utan handler 23,99–48,33 ms. Skillnaderna är inga generella prestandagarantier eller bevis på en förbättrad svanspercentil.

För de modala fönstren mäts tiden till redo/rendering/tomgång inne i ShowDialog, eftersom själva metodanropet återkommer först när dialogen stängs. Sex observationer per sekvens ger medianerna **84,87 / 86,48 / 83,99 / 91,21 ms** i tabellens ordning. Den lilla modala serien visar inte någon tydlig total fördel av att ta bort placeringshandlern. Den första editoröppningen efter att ägaren visats är 239,80 ms totalt, med 57,36 ms Show; den är inte kall app-/WPF-start.

P4.4/P4.5 öppnar och stänger fönster utan en kvarvarande ägare eller produktionshuvudvy. Deras omkring 120–130 ms Show kan därför inte jämföras med denna serie som en införd besparing. Skillnaden visar att fönster-/resurskontexten är avgörande för en representativ mätning. Den här serien isolerar inte exakt hur stor del som beror på ägarrelationen respektive kvarvarande WPF-resurser. Ingen ny native ETW-spårning har genomförts, och tidigare samplade flush-gränser bevisar inte en specifik Windows-/GPU-orsak.

UI-byte och rendering/tomgång är kontrollerade proxyvärden. De mäter inte maximalt eller kvarhållet processminne, fysisk presentation, hörbar ljudstart eller kostnaden i den riktiga huvudvyn under uppspelning.

## Geometri, återställning, fokus och modalitet

Enbart tidig maximering ger samma maximerade ytterrektangel i **42 av 42 jämförda visningar**, men annan återställningsrektangel i samtliga. I exemplet på skärmen med origo X=1920 återställer baseline till arbetsytan, **1920 × 1032 vid (1920, 0)**. Det enkla experimentet återställer till **1460 × 860 vid (1998, 78)**. Samma maximerade utseende räcker därför inte som paritetskontroll. [Microsoft beskriver RestoreBounds som läget/storleken före minimering eller maximering](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.restorebounds?view=windowsdesktop-10.0); [GetWindowPlacement ger motsvarande native tillstånd](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowplacement).

De två experimenten med förinställd arbetsyta matchar baseline i **42 av 42** jämförelser vardera för maximerat tillstånd inklusive återställningsdata, återställd geometri och ny maximering. Toleransen är en pixel/DIP för rektanglar. Ägarskärmen, DPI, minimimått och både native och WPF-ägarrelation kontrolleras. [MonitorFromWindow väljer skärmen som mest överlappar fönstret](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromwindow); testet jämför barnet med ägarens faktiska skärm.

Alla **169 editorfönster** visar riktiga låtkort och tar emot faktiskt WPF-tangentbordsfokus i sökrutan. De modala fönstren spärrar native ägaren under visning. Ägaren är aktiverbar och i förgrunden efter stängning i samtliga observationer. Profilinställningarna förblir oförändrade. Dessa kontrollresultat ersätter inte OS-hotkey-, fullständigt UI-, blandad-DPI- eller användartestning. Minimering, flytt till ny skärm under pågående dialog och alla andra användare av placeringsservicen är ännu inte kvalificerade.

## Nytt fynd: minimimått kan motverka Fit-rutinens marginal

Sex vanliga Fit-dialoger, 1000 × 700 med minimimått 700 × 400, stannar inom ägarens arbetsyta. Sex avsiktligt för stora dialoger, 4000 × 3000 med minimimått 2500 × 2000, hamnar med sin native ytterrektangel utanför arbetsytan på alla tre skärmarna, med båda ägartillstånden.

Orsaken syns i koden och observationen: minimimåtten klampas till hela arbetsytan, **1920 × 1032**, medan SetWindowPos begär arbetsytan minus två 12-pixelsmarginaler, **1896 × 1008**. Windows måste respektera de större minimimåtten. Resultatet blir en **1920 × 1032**-rektangel med start 12 pixlar in från arbetsytans övre/vänstra kant, alltså 12 pixlar utanför höger/nedre kant. Måtten avser native ytterrektangeln, inklusive fönsterkanter; detta är inte en uppmätt mängd dolt innehåll i varje produktdialog.

`withinWork=false` redovisar detta befintliga fel. Verktygets lyckade avslut betyder att analysen har samlat kompletta observationer; det betyder inte att alla experiment eller Fit-fall uppfyller sitt avsedda beteende. Fyndet är ännu inte rättat i P4.6.

## Paketerad kontroll och öppen OS-förgrundsfråga

Den självförsörjande paketkörningen med en repetition visar **61 barnfönster** på samma tre skärmar. Resultatet finns i `artifacts/performance/p4-placement-packaged-analysis/placement-analysis.json`. Geometri-/återställningsskillnaden och Fit-felet reproduceras. WPF-fokus i barnet, spärrad modal ägare och frigjord ägare efter stängning kontrolleras och passerar. Barnfönstren släpps vid GC-punktkontrollen.

I denna körning är däremot **OwnerForegroundAfterClose=false för samtliga 49 editorfönster**, även baseline. Detta skiljer sig från den fulla arbetskopiematrisens förgrundsobservationer. Orsaken är inte fastställd: WPF:s interna fokusresultat bevisar inte OS-förgrund eller att Windows accepterat aktiveringsförsöket. Paketets OS-förgrundsåtergång är därför **inte kvalificerad**. Resultatet får inte döljas genom att tvinga ägaren till förgrunden efter mätningen. Inga placeringsalternativ främjas på grundval av dessa resultat. Faktisk huvudvy, användarflöde och OS-aktivering behöver följas upp.

## Leverans, kontroller och nästa steg

Det nya valbara verktyget `--placement-analysis` och paketets **Placement-Analysis.cmd** gör jämförelsen möjlig på tillgängliga skärmar utan personlig profil eller uppspelning. Alla alternativ är begränsade till syntetiska fönster. Ingen av dem används av produktens offentliga konstruktor eller delade placeringsservice.

Releasebygget, hela prestanda-/beteendesviten och Windows PowerShell 5-rapportkontrollerna passerar. Tidigare funktioner och P4.5:s sök-/fokus-/Spara-/Avbryt-/huvud-/förlyssningskontroller behålls. Den befintliga NU1510-varningen kvarstår. De 181 stängda barnfönstren släpps vid framtvingad GC, vilket är en punktkontroll snarare än ett långtidsprov.

**P4.7:** gör tillåtna minimimått förenliga med den fysiska marginalen och slutlig fönsterstorlek i Fit-rutinen. Behåll ägarskärm, normalt tillstånd, centrering, fokus/modalitet och funktionen vid vanliga storlekar. Verifiera native slutrektangel och DIP/pixel-omvandling även för mindre arbetsytor och alternativa skalningar. Lägg till kontroller som faller på det reproducerade P4.6-felet och passerar efter rättningen. Tidig maximering ska fortsatt vara ett separat experiment tills dess bredare beteende och nytta är kvalificerade.

Faktisk ProBook-frysning, riktiga profiler/huvudvy under uppspelning, skilda DPI/arbetsytor, fysisk presentation och långa sessioner återstår. Arbetet gör inga ProBook-specifika hårdvaruantaganden. Se [aktuell roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p46-analyzed-show-sequence-with-ownerdisplay-parity) och [mätpaketets guide](PERFORMANCE-CAPTURE.md).
