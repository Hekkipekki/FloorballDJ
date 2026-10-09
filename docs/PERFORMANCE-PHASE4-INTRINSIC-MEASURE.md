# P4.10 — återanvänd giltig inneboende kortmätning

Infört och kontrollerat 2026-10-06. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-intrinsic-measure-v1`, med `intrinsicCardMeasurement=wpf-stable-sample-constraint-v1`. Ingen produktionsrelease har publicerats.

Låtpanelen växlar inte längre mätvillkor för sitt första provkort vid varje layoutpass. WPF kan därmed återanvända en giltig mätning. Ändringar av text, mall och ärvt teckensnitt leder fortfarande till ommätning. De faktiska låtkorten, begränsningen av byggda kontroller, hela datamodellen, val och redigeringsfunktioner behålls.

## Förändring

Tidigare mätte `SongWrapPanel` första kortet med obegränsad yta för att läsa dess faktiska inneboende mått, och mätte därefter samma kort med begränsade kortmått i visningsloopen. Vid nästa layoutpass växlade villkoret igen. Nu behåller just provkortet sitt obegränsade mätvillkor även i loopen. De andra korten mäts fortsatt med panelens uppmätta kortstorlek, och alla arrangeras i samma kortrektanglar.

Ingen separat giltighetscache, global korthöjd eller ny lagring av kontroller införs. WPF äger giltighet och invalidering. Panelen anropar fortfarande Measure även för ett provkort som kan ha återanvänts och fått nytt innehåll under passet; ett smutsigt eller ombundet kort hoppas inte över. Kodändringen är avgränsad till provkortets mätvillkor.

## Verifiering

Ett visat textkortstest räknar faktiska `MeasureOverride`-anrop. Tolv framtvingade stabila panelpass ger **noll nya textmätningar** efter rättningen. Testet faller på den tidigare panelkoden. Ändrat ärvt teckensnitt, bunden text och ersatt mall kräver fortfarande ommätning och ger rätt ny höjd, bredd och kolumnantal. Rullning med återanvända provkort visar rätt innehåll. Resultat: `artifacts/performance/p4-intrinsic-behavior-final/intrinsic-card-checks.json`.

De faktiska 2 000-låtsfallen jämför fortsatt kortpixlar och radpositioner med ursprunglig WrapPanel. En lång titel på första låtkortet kompletterar trimningskontrollen. Flera fönsterbredder, ändrad fontstorlek samt Cascadia Mono/fet/kursiv text jämförs och återställs. Alla synliga rader, kontrollgränser, offscreen-val, rullning, en/sv, fokus/navigering, sortering, sökning, hela-decket, Spara/Avbryt och huvud-/förlyssningsprogress passerar. Övriga resultat ligger i samma beteendekatalog.

Releasebygget, hela prestanda-/beteendesviten inklusive uppstarts-/sessionsregressionen och Windows PowerShell 5-rapporterna passerar. Befintlig NU1510-varning kvarstår. WPF:s naturliga DPI-invalidering behålls, men fysisk blandad-DPI-körning är fortsatt en separat öppen kvalificering. Testerna inför inga nya OS-skärminställningar.

## Mätning i huvudvyscenariot

Före: `artifacts/performance/p4-render-unprofiled/main-view-analysis.json` från P4.9. Efter: `artifacts/performance/p4-intrinsic-main-after/main-view-analysis.json`; sammanställning `comparison.json`. Samma syntetiska 553-jinglars/12-gruppers huvudvyfixtur, sex uppvärmda observationer per fall plus en första editoröppning: **13 modala öppningar på båda sidor**. Körningarna är utan profiler och utan samtidiga native sviter eller byggen. Ordning/frö, resurser och tysta ljudutgångar behålls.

| Fall | Till redo/WPF/tomgång före → efter, ms | UI-trådens tilldelade byte före → efter |
| --- | --- | --- |
| Vila | 109,21 → 116,48 | 9 413 636 → 9 361 956 |
| Huvudljud + förlyssning | 118,55 → 108,44 | 9 409 924 → 9 349 172 |

Minnestilldelningens median minskar **51 680 / 60 752 byte**, cirka **0,55 / 0,65 procent**. Tidernas riktning är blandad och intervallen överlappar: vila 87,36–147,95 ms före respektive 84,70–154,09 efter; aktivt 71,94–130,61 respektive 88,10–138,23 ms. Dessa små urval bevisar ingen generell snabbare öppning eller bättre svanspercentil. Den större delen av de cirka 9,35 MB per öppning kvarstår. P4.9:s panelandel omfattade mycket mer arbete än just provkortets upprepade mätning och ska inte tolkas som en motsvarande möjlig besparing från denna lilla ändring.

Huvud-/förlyssningsläsarna och viewmodeluppdateringarna fortsätter i aktiva fall; Avbryt behåller inställningar och diskprofil. Uppstartsskydd, verkliga sessionstoggle-sparningar och huvudvyns stängning passerar. De 13 stängda editorfönstren släpps vid GC-punktkontrollen. Dessa är WPF/byte-/läspositionsprover, inte fysisk presentation, maximalt processminne eller hörbar störningsfrihet.

## Nästa analys och verklig kvalificering

**P4.11:** avgränsa större kvarvarande mall-/kontrolluppbyggnad och gruppbyten med samma metod-/allokeringsspår och beteendekrav. Undersök möjlig återanvändning med tydligt ägarskap och avbrytning; anta inte att hela panelprofilens andel kan tas bort. Ingen sådan större återanvändning införs i P4.10.

ProBook-genomkörningen kan göras redan nu. Faktisk profil/laptop, fysisk blandad DPI, OS-input/förgrund, codec-/enhetsvariation, längre sessioner och den exakta rapporterade livefrysningen är ännu inte kvalificerade. Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p410-implemented-stable-intrinsic-card-measurement) och [mätguiden](PERFORMANCE-CAPTURE.md).


Uppföljning 2026-10-06: P4.11:s analys är nu genomförd, se [deck-/gruppbyten och nästa avgränsade implementation](PERFORMANCE-PHASE4-TEMPLATE-ANALYSIS.md). Den nya analysrevisionen behåller denna produktionsändring.
