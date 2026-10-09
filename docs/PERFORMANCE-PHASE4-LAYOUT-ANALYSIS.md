# P4.2 — analys av första visningen

**Historiskt underlag från 2026-10-04.** De experimentella lösningarna i denna analys infördes därefter i [P4.3 den 5 oktober](PERFORMANCE-PHASE4-RANDOM-LAYOUT.md). Tabellerna och testpaketets revision nedan beskriver P4.2:s dåvarande kod; de är baslinjen för jämförelsen, inte aktuell implementationsstatus.

Analysen är genomförd 4 oktober 2026. **Nästa implementation bör först stoppa breddloopen i poolöversikten och därefter begränsa uppbyggnaden av låtknappar till den synliga ytan.** Bakgrundsförberedelsen från P4.1 minskar filkontroller och skapande av grupper, men löser inte dessa layoutkostnader. Alla låtar, grupper, val och redigeringsfunktioner ska behållas.

## 1. Poolöversikten kan fastna i en växande layout

Ett faktiskt öppnat fönster med 553 filer, 40 låtar i det visade decket och bara detta deck i poolen når inte förberedelsens slutmarkör inom testets 20 sekunder. Det sker även med ett enda större deck. Förberedelsen har då kontrollerat filerna, skapat gruppen och satt `IsReady`; den fortsatta layouten hindrar gränssnittets kö från att nå tomgång. Timeouten är ett felutfall, inte en normal öppningstid.

Poolöversiktens `ListBox` tillåter horisontell mätning utan en fast gräns. När en procentmätare visar 100 procent kan dess indikator bidra till nästa önskade bredd och driva en återkoppling. I den inspelade 40-låtarsreproduktionen är indikatorns bredd cirka **177 253 DIP**, medan hela fönstret är 1 936 DIP brett. Profileringens återkommande UI-stackar ligger i `ContextLayoutManager.UpdateLayout`, `TextBlock.OnRender`, textformatering och DirectWrite. Det finns även tydligt finaliseringsarbete.

Två avgränsningar stöder fyndet: att ta bort det valda deckets låtmall i experimentet stoppar inte loopen; att dölja just poolfördelningslistan gör att fönstret blir färdigt. Att behålla översikten och i testet sätta `HorizontalScrollBarVisibility=Disabled` och `HorizontalContentAlignment=Stretch` gör också layouten stabil. Den begränsningen är **endast en experimentinställning i analysverktyget**, ännu inte införd i programmet. Kontrollera 0/100 procent, en/flera deltagare, långa namn, vertikal rullning och olika skärmstorlekar innan implementationen godkänns.

Detta är ett reproducerat desktopfel. Det är inte ännu bevisat att det orsakade den rapporterade korta frysningen under livekörningen på ProBooken; den körningens valda pool och exakta build är inte dokumenterade.

## 2. Alla knappar byggs även när få syns

Den valda låtlistan består av en vanlig `WrapPanel` inuti `ItemsControl` och `ScrollViewer`. Den bygger hela det valda deckets kontrollträd. Sökning döljer träffar utan att frigöra deras kontroller. Rullning ändrar inte antalet skapade kontroller. Gruppbyte och återgång återskapar mallar trots att P4.1 behåller gruppens redigeringsobjekt.

| Bibliotek / visat deck | Byggda låtknappar / i visningsytan | Visuella objekt | Median till WPF/tomgång, ms | Allokerade MB på UI-tråden |
| --- | ---: | ---: | ---: | ---: |
| 553 filer / 40 låtar, ordinarie layout | 40 / 28 | 933 | 190,2 | 9,59 |
| 2 000 filer / 40 låtar, ordinarie layout | 40 / 24 | 1 257 | 230,4 | 15,17 |
| 553 filer / 553 låtar, begränsad översikt | 553 / 32 | 5 884 | 448,3 | 51,80 |
| Samma fall, låtmallen utelämnad i experimentet | 0 / 0 | 314 | 167,6 | 5,97 |
| 2 000 filer / 2 000 låtar, begränsad översikt | 2 000 / 32 | 20 390 | 1 097,7 | 172,03 |
| Samma fall, låtmallen utelämnad i experimentet | 0 / 0 | 350 | 192,2 | 9,33 |

Varje rad är medianen av sex observationer. MB betyder 1 000 000 byte som allokerats under öppningen; det är inte maximal minnesanvändning. Tiderna är från konstruktionens början till WPF/tomgång efter förberedelse, inklusive visning och förberedelse. De är inte fysiska presentationstider. Stora deck och utelämnade låtmallar använder samma experimentella breddbegränsning i översikten, så dess separata loop inte förväxlas med låtlistans kostnad. En utelämnad mall visar var arbete försvinner; den är inte en färdig optimering och bevarar inte användarens redigeringsyta.

I 100-procentsfallet med 40 låtar blir öppningen färdig på 184,5 ms i det begränsade experimentet; listan är 258 DIP och indikatorn 228 DIP bred, utan växande geometri. Återgång till en redan besökt grupp med 2 000 låtar tar fortfarande 911,8 ms till WPF/tomgång och allokerar cirka 176,09 MB på UI-tråden. Gruppens datamodell återanvänds, men dess 2 000 knappar byggs igen. Det är en tydlig grund för begränsad realisering och återanvändning av kontroller.

Nästa lösning ska behålla kortens 260-DIP-bredd, avstånd, radbrytning, ordning, sökning, hel-deck-val, individuella markeringar och tangentbordsfokus. Behåll hela datamängden och skapa/återanvänd kontroller för aktuell visningsyta med en liten marginal. Verifiera framför allt gruppåtergång, sökning och fokus när objekt återanvänds. Den befintliga sorteringen gör dessutom en `IndexOf`-sökning för varje objekt även när ordningen redan är rätt; undersök en linjär kontroll av oförändrad ordning före omflyttning.

## 3. Visning och språk behöver mätas rätt

En uppvärmd konstruktor är kort: 2,1 ms i 553/40-fallet, men den synkrona `Show()`-delen tar 101,7 ms i median. Ägarens skärmplacering, maximering, fönsterskapande, stilar och första mätning bör profileras separat innan den delen ändras. Den separata första processöppningen tar 159,8 ms i konstruktion, visar första förberedelseinnehållet vid 372,1 ms och når WPF/tomgång vid 485,7 ms; detta bevisar inte kallt OS-/filcachetillstånd. `ContentRendered` beskriver i samtliga matrisöppningar först ett förberedelsefönster. Att visa det är inte samma sak som att en redigerbar låtlista är färdig.

Den uppmätta första språkgenomgången av det initiala fönstret är liten här: omkring 0,25–0,39 ms i de kompletta matrisfallen. Den sker före den senare låtmallen och kan inte användas för att påstå att alla senare kontroller har översatts eller att språkhanteringen är huvudorsaken till fördröjningen. Fallet utan språkhandlers är ett motexperiment med delvis andra texter, inte ett likvärdigt produktläge.

Analysen upptäckte två återgångar efter P4.1:s asynkrona öppning. Den första deckfliken kunde förbli ovald i ett redan laddat fönster, och två statiska texter i den sena deckmallen kunde vara svenska trots engelskt språk. Dessa är rättade: efter överföring av decklistans bindning väljs första decket endast när ett giltigt val saknas; mallens två texter hämtar befintliga översättningar direkt från deckmodellen. Befintligt deckval respekteras. Inga fil-, uppspelnings- eller profilformat ändras. Föreproben visar 14 flikar/0 låtknappar när fönstret visas före förberedelsen, men första fliken/40 knappar när förberedelsen sker före visning. Efterproben visar första fliken i båda fallen.

## Metod, resultat och begränsningar

Analysen använder Release/net10.0-windows, programmets Fluent-/temaresurser och samma språkhandlers som `App`, utan att starta licensfönster, personliga profiler, huvudfönstrets timers eller ljud. Den visar riktiga maximerade fönster utan att aktivera dem. Syntetiska filer används endast för filkontroller; ingen avkodning ingår. Datorn är Ryzen 9 7900X, 24 logiska processorer, cirka 31,22 GiB rapporterat RAM, GTX 1080 Ti/AMD Radeon. Fönstren mättes vid 96 DPI, 1 936 × 1 048 DIP. Detta är en testmiljö, ingen föreslagen lägstanivå för produkten.

Matrisen innehåller en separat observation i en ny process och sex observationer per fall i blandad, reproducerbar ordning: **55 öppningar**. Den omfattar svenska/engelska, små/stora deck, grupp/deckbyte, sökning och rullning. Kontroller verifierar att den nya och återbesökta gruppens faktiska låtlista visas, att sökning behåller rätt resultat och att språk/ordning stämmer. Allokeringarna gäller gränssnittets tråd, inte hela processen eller maximal minnesanvändning. GC-räknarna visar insamlingar under tidsfönstret, inte deras paustid. Dispatcheroperationer är extra instrumenterade och små stickprov ger inga pålitliga p95/p99.

Efter dessa 55 stängningar och framtvingad GC är **0 av 55** fönster nåbara via svaga referenser. Det är en begränsad kontroll av kvarhållning; handtag, minnesutveckling, ljud och en verklig lång session är fortfarande öppna. Fysisk skärmpresentation, riktiga profiler, laptop, flera DPI/monitorer, full tangentbordsnavigering och uppspelning/övergångar samtidigt med layouten är inte kvalificerade av denna analys.

Verifieringen omfattar den riktade slumpinställningssviten med synlig öppning, grupp/setup-byte, språk och Spara/Avbryt, plus den kompletta prestandasviten med båda ljudutgångsvägarna. Den kända NU1510-varningen är oförändrad. Versionsnumret är fortsatt 0.40.0-rc.1; mätpaketets revision är `phase4-random-settings-v2`.

## Reproducerbara underlag

- Slutlig matris: `artifacts/performance/p4-layout-analysis-validated/layout-analysis.json` och två bilder av WPF-innehållet. Bilderna är inte skärmfångster från kompositören.
- Första decket före/efter: `artifacts/performance/p4-layout-selection-before/selection-probe.json`, `p4-layout-selection-after/selection-probe.json` och riktad verifiering under `p4-layout-selection-validation`.
- Breddloop med ett litet deck: `artifacts/performance/p4-layout-single-pool-probe/timeout-553-40-single-pool.json`.
- Samplade stackar: `artifacts/performance/p4-layout-large-probe/large.nettrace` och `large.speedscope.json`, insamlade med lokalt `dotnet-trace` 10.0.745401 och profilerna `dotnet-sampled-thread-time,gc-collect`. Tracen gäller reproduktionen före korrigering av de sena språktexterna, inte en laptopkörning.
- Avgränsningar: `p4-layout-ablation-investigation`/`p4-layout-ablation-label` visar att utelämnad låtmall inte löser översiktsloopen; `p4-layout-overview-ablation` och `p4-layout-finite-probe` visar stabila öppningar när översikten döljs respektive begränsas.

Kör `Layout-Analysis.cmd` i mätpaketet eller analysverktyget med `--layout-analysis --output <ny mapp>`. Experimentflaggorna redovisas per fall. Kända loopfall finns som separata valbara prober med 20 sekunders timeout. Använd nya utmatningsmappar för jämförelser. Se [mätguiden](PERFORMANCE-CAPTURE.md) och [P4.3 i roadmappen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p43-implemented-stable-overview-and-bounded-song-rendering).
