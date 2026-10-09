# P4.5 — undvik återställning av oförändrade sökresultat

Infört och kontrollerat 2026-10-05 i arbetskopian. Version **0.40.0-rc.1** behålls; ingen produktionsrelease har publicerats. Mätpaketets revision är `phase4-search-refresh-v1` och sökspåret identifieras som `changed-membership-refresh-v1`.

Slumpmässig låtspelares inställningsfönster återställer nu ett decks filtrerade låtlista bara när låtarnas synlighet ändras. Upprepad tom sökning och likvärdiga sökresultat behåller befintliga knappar, fokus och rullposition. Detta åtgärdar det onödiga arbete som [P4.4 identifierade](PERFORMANCE-PHASE4-SHOW-ANALYSIS.md). Ändrade träfflistor visas fortfarande direkt.

## Uppföljning efter P4.5

[P4.6:s ägar-/skärmanalys](PERFORMANCE-PHASE4-PLACEMENT-ANALYSIS.md) är nu genomförd. Nästa implementation är P4.7:s förening av Fit-minimimått och marginaler; inga alternativa maximeringssekvenser är införda. Nästa-prioriteten längre ned beskriver läget när P4.5 levererades.

## Förändring och bevarade funktioner

`ApplySearch` beräknar fortsatt matchning och bästa träff för alla låtar i den valda gruppen. Normalisering, alla-sökord-matchning, accenthantering, rangordning, träffantal och automatisk deckvisning behålls. Ändrad ordning på sökorden kan ge samma träffar men ett annat bästa deck; den beräkningen hoppas därför inte över.

Varje deck jämför låtarnas tidigare synlighet med det nya resultatet. `VisibleJingles.Refresh()` behövs när medlemskapet ändras. När det är oförändrat slipper den virtuella listan kasta bort och återskapa sina kontroller. Källsamlingens tillägg, borttagningar och sortering uppdaterar vyn med sina befintliga händelser. Ingen global cache av sökord införs, så nya/lata grupper och ändrat innehåll behandlas även om söktexten är densamma. Träff-/valt antal uppdateras fortfarande.

Hela draften, ordningen, individuella val och hela-decket-val finns kvar. Spara/Avbryt, 260-DIP-korten, radbrytning, tangentbordsnavigering och begränsningen av byggda kontroller behålls. Riktiga filteråterställningar använder fortsatt P4.3:s återställningsväg. Fönstrets placering och maximering är oförändrade av detta steg.

## Jämförbar desktopmätning

Före: `artifacts/performance/p4-show-analysis-validated/show-analysis.json` från P4.4. Efter: `artifacts/performance/p4-search-analysis-validated/show-analysis.json`. Sammanställning: `comparison.json` i efterkatalogen. Samma åtta fall, frö/ordning, sex uppvärmda observationer per fall och en separat första processöppning: **49 faktiskt visade fönster per matris**. Inga native testsviter eller byggen kördes samtidigt med mätningen.

Båda matriserna använder .NET **10.0.10**, 24 logiska processorer, 96 DPI, aktiverande fönster, produktteman och motsvarande språkhandlers. Den syntetiska profilen har 553 filer och 12 grupper; valda decket har 40 låtar och normalt 32 byggda knappar inklusive bufferrader. Verktygets analysversion är fortfarande `p4.4-show-analysis-v1`, eftersom fall och mätmetod behålls. Per-fönster-sessioner skiljer revisionsetiketterna.

Medianer av sex normalfallsobservationer. Tid avser händelsen till nästa WPF-rendering och dispatcher-tomgång. Byte är UI-trådens tilldelade minne under fasen, inklusive tillhörande mät-/layoutarbete, inte maximalt eller kvarhållet processminne.

| Händelse | Tid före → efter, ms | Byte före → efter | Återställningar före → efter | Nya knappar före → efter |
| --- | --- | --- | --- | --- |
| Sök en låt | 4,77 → 4,95 | 471 564 → 486 976 | 1 → 1 | 1 → 1 |
| Likvärdigt resultat, ändrad versalisering/blanksteg | 1,83 → 0,88 | 229 228 → 101 400 | 1 → 0 | 1 → 0 |
| Töm faktiskt filter, återgå till 40 låtar | 16,14 → 13,68 | 2 783 892 → 2 788 536 | 1 → 1 | 32 → 32 |
| Anropa tom sökning igen, oförändrat resultat | 29,18 → 0,74 | 2 931 428 → 69 088 | 1 → 0 | 32 → 0 |
| Byt grupp | 50,47 → 33,92 | 5 335 376 → 5 294 332 | Ny listkontroll på båda sidor | 32 → 32 |
| Återgå till gruppen | 32,15 → 58,23 | 5 141 520 → 5 153 684 | Ny listkontroll på båda sidor | 32 → 32 |

Den upprepade tomma sökningens UI-allokering minskar cirka **97,6 procent**. Dess uppmätta tidsintervall är 25,10–34,28 ms före och 0,40–1,62 ms efter. Samtliga **62 faser med oförändrat resultat** i eftermatrisens riktiga editorfönster, inklusive första processobservationen och de alternativa visningsfallen, ger noll återställningar och noll nya kontroller.

Gruppbyten bygger fortfarande nya mallar/listkontroller. Återgången till en grupp har en högre median i efterkörningen, med överlappande intervall 23,45–73,53 respektive 24,19–75,45 ms; förbättringen tar inte bort detta arbete. Små urval och WPF-schemaläggning ger variation. Öppnings-/Show-tiderna används inte som bevis för att P4.5 löser den kvarvarande visningskostnaden. Ett faktiskt tömt filter återskapar fortfarande visningsytans knappar; det är separat fortsatt arbete.

## Kontroller

`RandomSettingsSearchChecks` använder ett faktiskt visat fönster och kontrollerar att likvärdiga sökningar behåller samma fokuserade checkbox och inte skickar någon vyåterställning. Ett test med samma två träffar men omvända sökord verifierar att rätt bästa deck ändå väljs. Ändrade/inga träffar och återställning till hela listan kontrolleras, liksom källans sortering, tillägg och borttagning, lata grupper, setupbyte och oförändrade ursprungliga inställningar. Det första relevanta oförändrad-resultat-testet faller på P4.4-beteendet och passerar med förbättringen.

P4.3:s visade 2 000-låtskontroll kompletteras med upprepad tom sökning när en låt längst ned har riktigt WPF-fokus. Samma checkbox, rullposition, återställningsantal och kontrollpool behålls. Befintliga kontroller av kortpixlar/radbrytning, flera bredder, språk, teckensnitt, offscreen-val, sortering, tangentbord, hela-decket, Spara/Avbryt och fortgående huvud-/förlyssning passerar.

Riktade resultat ligger i `artifacts/performance/p4-search-behavior-validated/`, inklusive `random-settings-search-checks.json`, `random-settings-layout-checks.json` och `random-settings-checks.json`. Releasebygget, hela prestanda-/beteendesviten och rapportkontrollerna i Windows PowerShell 5 passerar. Den befintliga NU1510-varningen kvarstår. Eftermatrisens 49 stängda fönster släpps vid framtvingad GC; det är en punktkontroll, inte ett långtidstest.

## Nästa steg och kvalificering

**P4.6 är nästa avgränsade analys:** pröva en kortare initial placerings-/maximeringssekvens med samma ägarskärm, arbetsyta, minimi-/återställd storlek, fokus och modalitet. P4.4:s snabbare försök utan placeringsrutin ska först jämföras med dessa beteendekrav; det är inte en färdig produktlösning. Utvärdera vid behov native spårning av WPF:s synkrona flush. Återanvändning över riktiga filteråterställningar och gruppbyten ligger kvar som separata möjliga förbättringar.

Verklig profil och huvudvy under uppspelning, faktisk ProBook-frysning, fysisk skärmvisning/ljudstart, alternativa skärm-/DPI-fall och långa sessioner är ännu inte kvalificerade. Inget krav på ProBook-specifik hårdvara införs. Se [roadmapen](PERFORMANCE-ROADMAP-RC1-TO-1.0.md#p45-implemented-avoid-unchanged-search-resets) och [mätpaketets guide](PERFORMANCE-CAPTURE.md).
