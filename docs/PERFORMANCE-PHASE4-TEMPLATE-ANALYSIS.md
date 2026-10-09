# P4.11 — malluppbyggnad och deck-/gruppbyten

Analyserat och kontrollerat 2026-10-06. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-template-analysis-v1`, med `interactionAnalysis=p4.11-template-phases-v1`. Detta steg inför mätning och väljer nästa förbättring; någon ny återanvändning av produktionskontroller införs inte här. Tidigare optimeringar behålls. Ingen release har publicerats.

## Metod och omfattning

Den faktiska MainWindow/MainViewModel, modala menyvägen och privata syntetiska 553-låts-/14-decks-/12-gruppers profilen från P4.8 används. Första decket innehåller 40 låtar. Efter varje redo öppning mäts sex faser: annat deck, tillbaka, första besök i annan grupp, tillbaka, uppvärmt återbesök och tillbaka igen. Varje fas mäter synkront arbete och total tid från åtgärd till WPF-rendering/tomgång, byte tilldelade på UI-tråden, kontrollidentitet, nya genererade kortbehållare, nya editorobjekt och filprober. Totaltiden inkluderar den synkrona delen; de ska inte summeras.

Separata körningar utan respektive med profiler omfattar vardera **13 öppningar och 78 interaktioner**: första editor efter huvudvyuppstart samt sex uppvärmda öppningar i vila och sex med huvudljud/förlyssning. Varje öppning använder en ny editor; gruppernas återbesök sker inom samma editor. Ordningen är fast för att skilja första besök från återbesök. Körningarna görs sekventiellt utan samtidiga byggen eller andra native prestandasviter. Detta är inget före/efter-par och fast ordning kan påverka tiderna.

- Tidserie: `artifacts/performance/p4-template-unprofiled/main-view-analysis.json`.
- Profil: `artifacts/performance/p4-template-profile/main.nettrace`, `main.speedscope.json`, `phase-trace-summary.json`.
- Sammanställning: `artifacts/performance/p4-template-profile/analysis-summary.json`.

## Resultat utan profiler

Medianer från sex observationer per fall. Byte avser tilldelning under fasen, inklusive mätarbete och normala UI-/timeruppdateringar; det är inte kvarhållet eller maximalt processminne.

| Fas | WPF/tomgång, vila / aktivt, ms | UI-byte, vila / aktivt | Nya låteditorer | Ny kortuppbyggnad |
| --- | --- | --- | --- | --- |
| Annat deck | 45,69 / 29,40 | 3 864 660 / 3 873 244 | 0 | 32 behållare |
| Tillbaka till deck | 35,24 / 30,52 | 3 884 924 / 3 895 624 | 0 | 32 behållare |
| Första andra grupp | 43,74 / 46,24 | 5 405 408 / 5 435 020 | 553 | Ny låtlista, 32 behållare |
| Tillbaka till grupp | 48,20 / 43,27 | 5 238 164 / 5 280 788 | 0 | Ny låtlista, 32 behållare |
| Uppvärmt gruppbesök | 46,72 / 39,87 | 5 254 072 / 5 276 172 | 0 | Ny låtlista, 32 behållare |
| Uppvärmd återgång | 50,20 / 43,92 | 5 251 008 / 5 259 352 | 0 | Ny låtlista, 32 behållare |

Samtliga 78 faser har **noll nya filprober**. Deckbyten behåller samma `VirtualizingSongItemsControl` men återställer dess genererade kort. Gruppbyten ersätter låtlistans kontroll och panel, även när gruppens data redan är realiserade. P4.1:s lata dataförberedelse fungerar alltså: bara första besöket skapar ytterligare 553 låteditorer, medan mall-/kontrollarbetet återkommer även vid återbesök.

Den synkrona medianen är cirka 2,43–3,90 ms vid deckbyten och 4,42–9,40 ms vid gruppbyten. Resten av totalmåttet omfattar dispatcher/layout/renderingsarbete och väntan. Att aktiva deckfall här har lägre totalmedian innebär inte att ljuduppspelning gör gränssnittet snabbare; urvalet är litet och variationen måste behållas i jämförelser. Den fullständiga sammanställningen innehåller min/max per fas.

## Separat stack-/allokeringsprofil

Testmarkörerna har utökats med InteractionStart/InteractionReady, fast fasnamn och native UI-tråd. Analysverktyget identifierar **91 kompletta faser**. För jämförelser används 84 uppvärmda faser, med första öppningen och dess sex interaktioner separat. Alla 4 301 allokeringstickar i de uppvärmda faserna har stack; 3 759 trådstackprov finns. Parsern rapporterar noll förlorade händelser. Det är inte i sig en garanti för komplett stack-/symbol-/native-täckning.

Första matchande stackursprung ger följande andelar av viktade allokeringsprov, sammanlagt tolv observationer per fas:

| Fas | Låtpanelursprung | Övrig malluppbyggnad |
| --- | --- | --- |
| Öppning | 27,69 % | 17,46 % |
| Deckbyte / återgång | 64,85 / 64,75 % | 9,20 / 8,43 % |
| Första andra grupp | 46,49 % | 18,45 % |
| Gruppåtergång | 47,72 % | 18,94 % |
| Uppvärmt gruppbesök / återgång | 47,86 / 47,57 % | 16,78 / 19,73 % |

Låtpanelursprung inkluderar mall-/text-/egenskapsarbete som sker under panelens anrop. Kolumnen ”övrig malluppbyggnad” räknar bara mallarbete utanför den först prioriterade panelkategorin. Andelarna visar var arbete uppstår, inte hur mycket en ändring säkert kan spara. Inklusiva `ApplyTemplate`, `MeasureOverride` och `ArrangeOverride`-stackar överlappar. AllocationTick ger viktade stickprov; typer/byte är uppskattad attribuering. Trådstackprov är antal, inte CPU-millisekunder. Ingen native-/kernel-/GPU-orsak fastställs.

## Kodvägar och nästa förbättring

`RandomPlayerProfileEditor.Decks` behåller redan realiserade editorer under fönstrets livstid. `DeckTabs` får en annan Decks-källa vid gruppbyte; innehållsmallen följer aktuell tab-selektion, och den första giltiga fliken återställs av `DeckTabs_TargetUpdated`. Mätningen bekräftar att innehållskontrollen då ersätts. Vid deckbyte behålls innehållskontrollen, men `SongWrapPanel.OnItemsChanged` begär en återställning och nästa Measure tar bort generatorns behållare och panelens barn.

**P4.12: prova en gemensam, kvarvarande deckvy vid gruppbyten.** Avgränsa först ägandet av innehållsmallen från flikarnas tillfälligt tomma selektion och håll högst en aktiv deckvy per editor. Behåll alla flikar och nuvarande första-deck-, sök-, sorterings-, språk-, fokus- och redigeringsregler. Befintliga kortåterställningar är ett separat nästa delproblem; deras hela profilandel ska inte tillskrivas en kvarvarande värdkontroll.

Godkänn en sådan ändring först efter en matchad körning av denna sexfasmatris, tydligt minskad kontroll-/allokeringskostnad och oförändrade riktiga kortpixlar/rader, tangentbordsfokus, aktuell bunden grupp/deck, tomma/filterade listor, hela-decket/offscreen-val och Spara/Avbryt. Snabba byten, borttagning/duplicering, ny källa, språk/font/malländring och stängning ska hålla data/bindningar aktuella och avbryta väntande fokus-/layoutarbete. Ingen obegränsad cache av alla gruppers kontrollträd ska införas. Om vinsten uteblir behålls befintlig struktur och kortåterställningen blir nästa avgränsade kandidat.

## Verifiering och praktisk kvalificering

Varje interaktion kontrollerar att alla synliga kort hör till rätt aktuellt deck, att hela viewporten finns och att kortantalet hålls begränsat. Huvudljud och förlyssning fortsätter framåt med viewmodeluppdateringar i aktiva fall. Avbryt behåller inställningar och diskprofil. Uppstartens profil, riktiga sessionsändringar och huvudvyns spara/stäng-livscykel passerar. Alla 13 stängda editorfönster släpps vid GC-punktkontrollen; det kvalificerar inte hela processens långtidsminne eller varje övergiven underkontroll separat.

Releasebygget, hela befintliga prestanda-/beteendesviten, Windows PowerShell 5-rapporterna och den nya interaktions-/profilvägen passerar. Första hela sviten föll i den befintliga kontrollen ”Space still toggles the bound checkbox”; en separat slumpinställningskörning och därefter en ny hel körning passerade utan kodändring. Orsaken till första utfallet är inte fastställd. Loggar: `artifacts/performance/p4-template-behavior-final.log` och `p4-template-behavior-confirm.log`; separat slumpkontroll: `p4-template-random-recheck`. Spåranalysatorn läser även P4.9:s äldre öppningsmarkörer, med 13 kompletta öppningsfaser i `p4-template-profile/legacy-opening-summary.json`. Det portabla paketets korta interaktionskörning och metadata/dokument/assembly-/symbolhashar verifieras efter paketering.

Automatiska faser använder programmatisk selektion, inte OS-klick eller snabbtangenter. Modal livstid inkluderar nu valfria interaktioner och efterföljande hålltid; jämför inte den med tidigare rena öppningskörningar. De tysta läspositionsproverna bevisar inte hörbar störningsfrihet eller fysisk ljudstart. Faktisk laptop/profil, OS-förgrund, blandad DPI och längre sessioner återstår. **ProBook-genomkörningen kan göras redan nu** med `Legacy-Capture.cmd` och samma profil/ljudutgång/power-/DPI-inställning.

Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p411-analyzed-larger-templatecontrol-work) och [mätguiden](PERFORMANCE-CAPTURE.md).


Uppföljning 2026-10-06: P4.12:s kvarvarande deckvy är nu införd och kontrollerad; se [implementation, före/efter och tidsbegränsningar](PERFORMANCE-PHASE4-PERSISTENT-DECK.md).
