# P4.13 — återanvänd låtkorten över liståterställningar

Infört och kontrollerat 2026-10-06. Version **0.40.0-rc.1** behålls. Diagnostikrevision: `phase4-card-reuse-v1`. Den befintliga låtlistan återanvänder nu kortens ContentPresenter och kompatibla kontrollträd även vid käll-, deck-, grupp-, sorterings- och filteråterställningar. P4.12:s kvarvarande deckvy behålls. Inga nya användarfunktioner eller bakgrundsjobb införs.

## Ägarskap och säker återanvändning

WPF:s generatorkö töms vid Reset/RemoveAll. Låtlistans eget fabrik-/rensningskontrakt använder därför en ägd FIFO-kö för både scrollning och återställningar. Generatorn kopplar fortfarande loss gammal data, länkar och förbereder varje nytt item. Panelens scrollning använder vanlig Remove, så samma behållare inte samtidigt finns i WPF:s återvinningskö. Inga privata WPF-metoder eller reflexionsändringar används. Se [WPF:s ItemContainerGenerator](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ItemContainerGenerator.cs) och [ItemsControl](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ItemsControl.cs).

Aktiva och cachade behållare **delar samma viewportbudget**: synliga rader, tidigare overscan och högst ett fokuserat extra kort, begränsat av itemantalet. Budgeten justeras vid layout/resize och fontändring. En synlig tom lista släpper både aktiva och cachade behållare; Unloaded tömmer kön. En tillfälligt kollapsad deckvy behåller högst senaste layoutbudget tills den mäts igen eller lämnar trädet. Poolen delas inte mellan fönster och lagrar inte itemobjekt.

Endast matchande ItemTemplate, selector och strängformat återanvänds. En riktig malländring eller borttagen mall får nya relevanta kontrollträd. Färskt innehåll och WPF:s mät-/font-/DPI-invalidering behålls. ContentPresenter kopplar normalt från tidigare item utan att ta bort den explicita mallen; se [frameworkets rensning/förberedelse](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/ContentPresenter.cs).

Ett kort med aktuellt/senast känt tangentbordsfokus eller muscapture återanvänds inte till annan låt. Fokus flyttas före rensning, samma källas sortering behåller sin fokusidentitet och källbyte avbryter gammal återställning. Capture släpps. Detta korrigerar tidiga provversioners scroll-/sorteringsfokusfel som de befintliga testerna upptäckte. FIFO och inputundantaget håller ett gammalt navigeringsmål från att följa med till en annan låt.

## Matchad desktopjämförelse

Före: P4.12-paketet `20261005-234911-ad2fa405`. Efter: Release med samma portabla programsymboler och .NET 10.0.10. Befintlig huvudvymatris utan profiler, sex uppvärmda observationer per fall plus första editor: **13 öppningar och 78 interaktioner per körning**. Körningar/native tester/byggen är sekventiella. Källor: `artifacts/performance/p4-card-reuse-before/`, `p4-card-reuse-after/` och efterkörningens `comparison.json`.

| Fas | UI-byte vila, före → efter | UI-byte aktivt, före → efter |
| --- | --- | --- |
| Deckbyte | 3 833 156 → 2 581 992 | 3 877 188 → 2 561 912 |
| Deckåtergång | 3 865 924 → 2 590 036 | 3 894 572 → 2 563 484 |
| Första andra grupp | 5 147 520 → 3 810 392 | 5 178 372 → 3 795 576 |
| Gruppåtergång | 4 999 024 → 3 663 196 | 5 028 960 → 3 646 120 |
| Uppvärmt gruppbesök | 4 994 960 → 3 660 156 | 5 040 892 → 3 646 748 |
| Uppvärmd återgång | 4 997 304 → 3 637 092 | 5 025 544 → 3 641 932 |

Tilldelningen minskar **32,6–34,2 % vid deckbyten och 26,0–27,7 % vid gruppbyten**, ungefär 1,25–1,39 MB per byte. Efter första uppbyggnaden återanvänds 32 behållare och **noll nya behållarinstanser skapas i samtliga 78 faser**. Före skapades 32 per fas. Mätarens `newContainers` räknar nu faktiska fabriksinstanser; en ny generatormappning kan använda en befintlig behållare. Den gamla flaggan och verkliga instansantalet sammanföll i föreversionen. Även återanvändnings- och budgetantal rapporteras i samma befintliga interaktionsresultat.

Aktiva deckfaser har median 37,47 → 28,38 och 27,24 → 22,66 ms, gruppfaser cirka 35,30–42,37 → 29,00–34,35 ms. Vilofaser är blandade, med både högre och lägre tider. **Snabbare första öppning är inte belagd:** median 118,51 → 119,13 ms vila och 103,51 → 114,49 aktivt, överlappande intervall och något högre allokering efter. Sex observationer per fall kvalificerar inte generell hastighet/svanspercentiler. Byte avser UI-trådens tilldelning inklusive mät-/timerarbete, inte kvarhållet eller maximalt processminne.

## Verifiering och laptopdata

Releasebygget, de riktade slumpinställningskontrollerna och hela befintliga sviten passerar. Aktuella kortpixlar/radpositioner jämförs fortfarande med original-WrapPanel, i en/sv och flera bredder/fontstilar. Aktiva plus cachade behållare kontrolleras mot samma budget, med distinkta aktuella items och komplett viewport. Tangentbord/Space/sorteringsfokus, muscapture vid gruppbyte, tomma/offscreen-filter, hela-deck-val, Spara/Avbryt och huvud-/förlyssningsprogress passerar.

Textprovet kontrollerar verklig instansåteranvändning, ombindning, mallborttagning och att ett övergivet item släpps vid en GC-punktkontroll; synlig tom lista har noll aktiva/cachade. Huvudvymatrisens uppstart, sessionssparningar, Avbryt, läsprogress och stängning passerar; alla 13 stängda editorreferenser släpps. Resultat: `p4-card-reuse-behavior-complete/` och `p4-card-reuse-full-behavior.log`. Rapport- och paketkontroller görs före leverans. Befintlig NU1510-varning kvarstår.

Den nya [verkliga ProBook-mätningen](PERFORMANCE-PROBOOK-20261006.md) gäller **P4.12**, inte denna ändring. Den visar att den stora slumpgruppens synkrona filkontroller på UI-tråden tar 224–464 ms. Det blir nästa prioritet inom redan planerat snabbtangents-/ljudarbete. P4.13 ändrar inte den kodvägen, och löser inte i sig denna uppmätta snabbtangentskostnad.

ProBook/personal profile, fysisk blandad DPI/OS-input/ljudstart, enhetsvariation och långtidsminne återstår för P4.13. Ingen ny obligatorisk analysfas införs före laptoptest. Nytt paket använder samma `Legacy-Capture.cmd`; RAR kan packas upp i en ny mapp för jämförelse med P4.12.
