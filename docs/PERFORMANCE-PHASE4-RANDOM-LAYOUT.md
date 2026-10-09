# P4.3 — stabil poolöversikt och begränsad låtlista

Genomfört 2026-10-05 i arbetskopian. Versionen är fortfarande **0.40.0-rc.1**. Mätpaketets revision är `phase4-random-layout-v1`; ingen produktionsrelease har publicerats.

## Förändringar

Poolöversikten mäts nu inom sin tillgängliga bredd. Horisontell rullning är avstängd och raderna fyller listans bredd. Namn med förkortad visning behåller hela namnet i sin tooltip; procent, progressindikatorer, ordning, markering och vertikal rullning finns kvar. Detta stoppar den breddloop som [P4.2 reproducerade](PERFORMANCE-PHASE4-LAYOUT-ANALYSIS.md) när ett deck stod för 100 procent.

Låtlistan använder `SongWrapPanel` och `VirtualizingSongItemsControl`. Samma 260-DIP-kort, marginaler och mall ligger kvar. Panelen mäter ett faktiskt kort, inklusive teckensnitt och marginaler, och bygger raderna som möter visningsytan samt en extra rad på vardera sidan. En fokuserad knapp får behållas när man rullar bort den. WPF:s behållare återanvänds vid rullning. Mängden kontroller följer fönstrets visningsyta, inte antalet låtar; återanvändningskön kan behålla behållare från en tidigare större visningsyta under kontrollens livstid.

Hela gruppens låtobjekt, identiteter och individuella val finns kvar. En egen filtrerad vy visar sökträffarna utan att ändra den kompletta listan som används för val och sparning. Hela-decket-val låser och markerar även återanvända knappar utan att radera individuella val. Tangentbordsnavigering kan bygga och fokusera låtar som ännu inte har någon knapp. Sortering behåller samma kulturjämförelse och ursprungsordning vid lika namn, men applicerar en ändrad ordning med en enda samlad uppdatering. Oförändrad ordning gör inga flyttningar. Fokus återställs efter layout, med en avbrytbar väntande åtgärd och utan att ta tillbaka fokus från ett annat fält.

## Jämförbar desktopmätning

Före: `artifacts/performance/p4-layout-analysis-validated/layout-analysis.json`. Efter: `artifacts/performance/p4-layout-optimized-validated/layout-analysis.json`. Samma nio syntetiska fall, ordning/frö, sex uppvärmda observationer per fall och en separat första processöppning: **55 faktiskt visade fönster per matris**. Fönstren var 1936 × 1048 DIP vid 96 DPI med programmets resurser och motsvarande språkhandlers. Mätningarna kördes utan samtidig byggning eller andra native testsviter.

Stora deck använder breddbegränsningen på båda sidor av jämförelsen: före som experiment, efter som infört produktbeteende. Historiska fallnamn med `finite` behålls för matchningen. Experimentet med utelämnad låtmall är fortsatt en avgränsning och inte ett användbart produktläge.

| Fall | Knappar före → efter | Öppning till WPF/tomgång, ms | UI-trådens allokering under öppning, MB |
| --- | --- | --- | --- |
| 553 filer, 40 låtar i valt deck, engelska | 40 → 32 | 190,2 → 177,6 | 9,59 → 9,03 |
| 2 000 filer, 40 i valt deck, engelska | 40 → 28 | 230,4 → 205,7 | 15,17 → 14,45 |
| 553 filer i ett deck | 553 → 36 | 448,3 → 187,3 | 51,80 → 9,20 |
| 2 000 filer i ett deck | 2 000 → 36 | 1 097,7 → 228,2 | 172,03 → 12,37 |
| 553 filer, 40 i valt deck, svenska | 40 → 32 | 175,2 → 178,7 | 9,13 → 8,60 |
| 553 filer, 40 i valt deck, ett deltagande deck | 40 → 36 | 184,5 → 186,2 | 10,57 → 9,99 |

Alla tal är medianer av sex observationer. MB är 1 000 000 byte. Allokering betyder skapade hanterade objekt på UI-tråden under mätningen, **inte** processens totala eller maximala minne. I 2 000-låtsfallet minskar öppningstiden cirka 79 procent och allokeringen cirka 93 procent. Den synliga ytan innehåller 32 knappar på båda sidor; efter finns fyra extra knappar som förbereder nästa rad. Antalet visuella noder minskar från 20 390 till 749.

| Åtgärd | Före, ms till WPF/tomgång | Efter, ms till WPF/tomgång | UI-allokering före → efter, MB |
| --- | --- | --- | --- |
| Återgå till grupp med 553 låtar | 285,8 | 51,4 | 50,18 → 5,30 |
| Byta till grupp med 2 000 låtar | 1 095,4 | 66,4 | 176,56 → 5,83 |
| Återgå till grupp med 2 000 låtar | 911,8 | 67,4 | 176,09 → 5,33 |
| Tömma sökning, 2 000-låtsdeck | 44,0 | 16,6 | 1,07 → 3,08 |
| Tömma sökning, 40-låtsdeck/553 filer | 4,0 | 19,8 | 0,07 → 2,71 |
| Rulla till slutet, 2 000-låtsdeck | 0,3 | 8,5 | 0,01 → 1,64 |

**Avvägningen syns i sökning och rullning.** Före låg alla knappar redan kvar. Efter sökåterställning måste den synliga delen byggas igen, och rullning binder återanvända behållare till andra låtar. Det gör vissa små liståtgärder dyrare, men arbetet och antalet kontroller förblir begränsade. Stora deck slipper bygga tusentals osynliga knappar vid öppning och gruppbyte. Detta är inte belägg för att varje åtgärd blivit snabbare.

Den första processöppningen i 553/40-fallet är i stort oförändrad: 485,7 → 486,6 ms. Uppvärmd synkron `Show` kostar fortfarande ungefär 112–123 ms i produktfallen. Förberedelse, fönsterplacering, resurser och första processanvändning finns kvar som separata kostnader. Sex observationer ger ingen kvalificerad p95/p99 eller garanti för andra datorer.

## Kontroller

- Hela befintliga prestandasviten passerar, inklusive båda ljudutgångsvägarna, sparning/profilformat, metadata, vågformer, autoplay, volym och snabbtangents-/slumpbeteende. Windows PowerShell 5:s rapportkontroller passerar. Den tidigare NU1510-varningen kvarstår.
- Nya faktiskt visade 2 000-låtsfönster kontrollerar engelska/svenska, 0/100-procentspool, ett mycket långt decknamn och dess tooltip, stabil indikatorbredd och tre fönsterbredder. Flera deltagande deck ingår även i mätmatrisen. De 100-procentiga indikatorerna står still efter layout, i stället för att växa vidare.
- Kortens storlek och position jämförs mot den ursprungliga `WrapPanel` med samma mall, vid första och sista raderna, tre bredder och större text. Likvärdiga tillstånd utan muspekaren över kortet ger identiska renderade kortpixlar.
- Varje rad som möter visningsytan måste ha alla sina riktiga knappar, varje realiserad behållare exakt en unik låt, och det totala antalet behållare hålla den angivna gränsen. Rullning till sista låten använder redan skapade behållare. Ett tomt sökresultat lämnar inga låtknappar eller falsk rullhöjd.
- Tab över osynliga rader, pilar, Home/End, PageDown, Space, inträde och utträde ur listan samt fokusidentitet vid sortering kontrolleras med WPF-händelser och faktiskt tangentbordsfokus. Bakåt ut ur listan kontrolleras genom samma navigeringsfunktion; detta är inte ett OS-test av Shift+Tab.
- Individuella val utanför visningsytan, hela-decket-låsning, sortering, sökning på en tidigare osynlig låt och Spara/Avbryt kontrolleras. Tyst huvud-/förlyssning fortsätter avancera under 30 rullningar mellan listans början och slut. Det bevisar fortgående ljudutmatning, inte hörbar eller helt störningsfri kontinuitet.
- Det tidigare loopfallet körs separat **utan analysverktygets breddflagga**: `artifacts/performance/p4-layout-single-pool-fixed/layout-analysis.json`, fyra öppningar, ingen timeout. `artifacts/performance/p4-layout-behavior-validated/` innehåller de riktade beteenderesultaten. De 55 stängda matrisfönstrens svaga referenser överlever inte den framtvingade GC-kontrollen; detta är en punktkontroll, inte ett långtidsprov.

## Uppföljning 2026-10-05

[P4.4:s desktopanalys](PERFORMANCE-PHASE4-SHOW-ANALYSIS.md) är nu genomförd med 49 öppningar och separat stackprofil. Oförändrade sökresultat återskapar knappar; nästa implementation är P4.5:s skydd mot onödig liståterställning. Visningssekvensen behöver fortsatt ägar-/skärm-/DPI-verifiering. Texten nedan beskriver prioriteten när P4.3 levererades.

## Kvar och nästa steg vid P4.3-leveransen

Verklig före/efter-körning med samma profil, ljudutgång, strömläge och version på ProBooken och andra datorer återstår. Fysisk skärmvisning, OS-tangentbordsflöde, alternativa DPI/skärmar, hörbar ljudkontinuitet och längre sessioner är inte kvalificerade. Den exakta rapporterade livefrysningen har därför ännu inte verifierats som löst. Låtobjekt för besökta grupper behålls fortfarande tills fönstret stängs; begränsningen gäller kontrollerna, inte hela datamodellen.

**P4.4 blir nästa avgränsade analys:** den kvarvarande synkrona fönstervisningen/placeringen och första resursanvändningen, samt sökåterställningens arbete. Profilera dessa utan att tillskriva dem allmänna orsaker utifrån enbart totalsiffran. Behåll samma kontrollfall och komplettera med faktisk huvudvy/uppspelning och fysisk hårdvarujämförelse. Den större ljudägarskaps-/koordinatorförändringen och övriga verktyg är fortsatt separata öppna spår.

Panelen använder WPF:s [IScrollInfo-kontrakt](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-scroll-content-by-using-the-iscrollinfo-interface) och [återanvändning av behållare](https://learn.microsoft.com/en-us/dotnet/api/system.windows.controls.primitives.irecyclingitemcontainergenerator.recycle?view=windowsdesktop-10.0). Se [aktuell roadmap](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p43-implemented-stable-overview-and-bounded-song-rendering) för status och kvarvarande kvalificering.
