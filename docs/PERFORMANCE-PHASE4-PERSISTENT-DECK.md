# P4.12 — återanvänd deckvyn vid gruppbyten

Infört och kontrollerat 2026-10-06. Version **0.40.0-rc.1** behålls. Mätpaketets revision är `phase4-persistent-deck-v1`, med `randomSettingsDeckView=stable-explicit-template-host-v1`.

Detta är en ändring av den befintliga slumpinställningsvyn. Grupp- och profilbyten återanvänder nu samma låtlista, scrollvy och panel i stället för att bygga om hela deckvyn. Låtdata och bindningar byts till aktuell grupp/deck. Inga nya användarfunktioner, bakgrundsjobb eller cacher av alla gruppers kontrollträd införs.

## Förändringen

WPF:s flikhantering tömmer SelectedContentTemplate när selektionen tillfälligt försvinner vid ett källbyte. För den här vyn använder alla deck samma explicita mall. Den lilla `PersistentDeckTabControl` binder därför den befintliga temamallens innehållsvärd till ContentTemplate när första verkliga decket valts. SelectedContent fortsätter följa normal WPF-selektion. Flikar, tema, native fokus-/navigeringshantering och samma faktiska låtkort behålls. Tom selektion döljer innehållet och rensar tidigare låtar; en verkligt ersatt innehållsmall bygger fortfarande en ny vy. Ingen extra vy byggs i den första tomma förberedelseskärmen.

Detta följer WPF:s befintliga kontrakt för SelectedContent och PART_SelectedContentHost. En explicit ContentTemplate tillåter ContentPresenter att byta DataContext utan att välja en ny mall; en ändrad mall river däremot dess tidigare träd. Se [.NET 10 TabControl](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/TabControl.cs) och [ContentPresenter](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ContentPresenter.cs).

Eftersom låtlistan nu kan stanna laddad vid gruppbyte avbryts dess väntande fokusåterställning också när ItemsSource byts. Tidigare fokusidentitet och panelens väntande fokusägare rensas. Samma källas sökning/sortering och fokusregler behålls; ett gammalt gruppobjekt kan inte återställas i den nya gruppens vy. Stängning behåller den befintliga Unloaded-avbrytningen.

## Före/efter

Samma befintliga P4.11-matris körs utan profiler: **13 modala huvudvyöppningar och 78 deck-/gruppbyten per körning**, sex uppvärmda observationer vardera i vila och med tyst huvudljud/förlyssning. Före är det levererade P4.11-paketet; efter är Release med samma portabla programsymboler och .NET 10.0.10. Körningarna är sekventiella utan samtidiga byggen/native sviter. Datafixtur, befintliga mätare, faser och ljudväg behålls.

Källor: `artifacts/performance/p4-persistent-main-reverse-before/main-view-analysis.json`, `p4-persistent-main-matched-after/main-view-analysis.json` och dess `comparison.json`. Tidigare provkörningar behålls separat; de är inte den slutliga tabellen nedan.

| Gruppbyte | UI-byte vila, före → efter | UI-byte aktivt, före → efter |
| --- | --- | --- |
| Första andra grupp | 5 483 368 → 5 188 100 | 5 453 364 → 5 175 236 |
| Tillbaka | 5 322 792 → 5 014 588 | 5 284 636 → 5 057 696 |
| Uppvärmt besök | 5 318 956 → 5 011 620 | 5 278 684 → 5 029 680 |
| Uppvärmd återgång | 5 313 828 → 5 013 680 | 5 302 216 → 5 040 668 |

Gruppbytenas median minskar **227–308 kB**, cirka **4,3–5,8 procent**. Samma låtlista och panel finns nu kvar i samtliga 78 interaktionsfaser. Före ersattes kontrollen vid varje gruppbyte. Första nya gruppbesöket skapar fortfarande 553 lata låteditorer; återbesök skapar noll. Filproberna är fortsatt noll under bytena.

**Kortåterställningen kvarstår:** varje byte genererar fortfarande 32 kortbehållare i denna viewport. Ändringen sparar uppbyggnad runt listan, inte hela låtpanelens tidigare profilandel.

Tiderna visar ingen generell förbättring. Gruppfasernas medianer är blandade: vila cirka 36,55–54,92 ms före och 42,70–45,78 efter; aktivt 39,27–42,20 före och 39,98–46,88 efter. Öppningsmedianen är **112,01 → 128,29 ms** i vila och **128,14 → 122,60 ms** aktivt. Öppningsintervallen överlappar (92,13–142,63 / 100,20–161,64 ms vila, 80,79–155,74 / 84,53–151,34 aktivt). UI-byte vid öppning är också blandade. Vi kan därför belägga återanvändning och mindre tilldelning vid gruppbyten, men inte snabbare första öppning eller bättre svanspercentiler. Det lilla urvalet utesluter inte en tidsregression; verklig laptopprofil behöver jämföras.

## Verifiering och fortsatt arbete

Befintlig slumpinställningskontroll omfattar nu en/sv, bevarad kontroll/panel och rätt aktuell källa vid deck-/grupp-/setupbyte, tom selektion, tjugo snabba byten, byte med väntande gammal fokusåterställning, innehållsmallens verkliga ersättning/återställning och stängning. En visad vanlig TabControl används som referens för hela deckvyns kontrollmått. Befintliga riktiga kortpixlar/radbrytning, fontvariation, viewportgränser, navigation, oförändrad sökning, offscreen-/hela-deck-val, Spara/Avbryt, bakgrundsförberedelse och huvud-/förlyssningsprogress behålls.

Den matchade huvudvymatrisen passerar rätt innehåll, startup-/sessionssparningar, Avbryt, oberoende läsprogress och stängning. Alla 13 stängda editorreferenser släpps vid GC-punktkontrollen. Releasebygget och hela befintliga sviten passerar, inklusive de utökade vykontrollerna. Logg: `artifacts/performance/p4-persistent-full-behavior.log`. Rapportkontroller och den paketerade interaktionskontrollen verifieras före leverans. Befintlig NU1510-varning kvarstår.

Ingen fysisk ljudstart/presentation, hörbar störningsfrihet, blandad DPI, långtid eller faktisk ProBook-profil kvalificeras av dessa desktopprov. **ProBook-genomkörningen kan göras nu** med `Legacy-Capture.cmd`; jämför samma profil, utgång, strömläge och skärm/DPI med P4.11-paketet. Nästa prioritet bör styras av den verkliga körningen och kvarvarande kostnader i den befintliga roadmapen. Ingen ny obligatorisk analysfas läggs till före laptoptestet.

Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md) och [mätguiden](PERFORMANCE-CAPTURE.md).


Uppföljning 2026-10-06: [P4.13](PERFORMANCE-PHASE4-CARD-REUSE.md) hanterar nu de kvarvarande kortåterställningarna. Den [nya ProBook-körningen](PERFORMANCE-PROBOOK-20261006.md) gäller den tidigare P4.12-versionen och används som faktisk prioritetsdata.
