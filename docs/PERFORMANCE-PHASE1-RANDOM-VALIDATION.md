# P1.7 — färsk filkontroll vid behov i slumpval

Infört och kontrollerat 2026-10-06. Version **0.40.0-rc.1** behålls; diagnostikrevision `phase1-random-validation-v1`, `randomPoolAvailability=candidate-on-demand-per-command-v1`. P4.13:s kortåteranvändning och tidigare förbättringar behålls. Ingen produktionsrelease har publicerats.

Den [verkliga ProBook-körningen](PERFORMANCE-PROBOOK-20261006.md) visade 224–464 ms av synkron filkontroll för den 248-medlemmars slumpgruppen före uppspelning. Programmet kontrollerar nu kandidater efter behov i stället för att först läsa alla primärkandidaters filstatus.

## Urval och bevarade regler

Urvalet söker i samma prioriteringsordning som den tidigare `GetEligibleCandidates`: annat deck när variationsgränsen är nådd, därefter sessionsmässigt ospelade inom den prioriteten, och undvikande av föregående jingle när alternativ finns. Varje prioritet drar jämnt utan återläggning. En saknad kandidat tas bort från det aktuella försöket; nästa prioritet används först när den högre saknar spelbara kandidater. Finns inga giltiga kandidater återgår hanteraren till samma kategori-/direkta fallback som tidigare.

Tillgängligheten ägs av **en enda tangentbordshantering**. Ingen status sparas över tangenter, inga tidsbaserade tillgänglighetscacher införs och inga nya bakgrundsjobb startas. Kandidatbildning och värsta möjliga uttömning är linjära i gruppstorleken. En helt saknad grupp kan fortfarande behöva hela sökningen; den normala giltiga gruppen behöver en kontroll för primärvalet. En ID-kontroll kan behöva flera filprober vid duplicerade ID:n, med samma första existerande medlemsförekomst och samma första-globala-ID/deckattribution som tidigare.

Aktuell aktiv medlem kontrolleras färskt vid upprepad tangent före det befintliga fade-/nästa-tangentbeteendet. Utan giltig medlem får tomgruppens fallback fortfarande ske. Följdljud kontrolleras **fortfarande vid lyckad primärstart** och deras giltiga ID:n lagras då, precis som tidigare. Den befintliga färska kontrollen vid själva följdövergången behålls. Följdljud som saknas vid start blir inte nytillåtna under pågående låt. Den tidigare urvals-/spelvägen återanvänds med det färdiga valet, så fades, state, Space-resume-rensning, startfel och deck-run-uppdatering behålls. Kategoriernas befintliga väg ändras inte.

## Verifiering

17 920 uttömmande fall varierar varje tillgänglighets-/sessionsmask för fyra kandidater, tidigare jingle, tracking av/på, variation, null-run och gränsklämning. Alla möjliga slumpgrenar räknas med sina exakta sannolikheter och jämförs med tidigare `GetEligibleCandidates` på de giltiga medlemmarna. Samma kandidater får exakt samma jämna slutlig sannolikhet; varje kandidat kontrolleras högst en gång i urvalet.

Separata tester kontrollerar duplicerade ID:n och borttagna/återställda filer mellan kommandon. Den **verkliga MainWindow-tangentbordshanteraren**, med tyst WAV/WASAPI och en intern testinjektion för filstatus, kontrollerar:

- 248 giltiga primärmedlemmar och ett följdljud: två filprober totalt, varav en för primärvalet.
- Upprepad aktiv tangent: en färsk primärkontroll och samma fadebeteende.
- Följdljud saknat vid nästa start: inget motsvarande pending-ID.
- Alla primärmedlemmar saknade: 248 kontroller och korrekt kategorifallback; återställda medlemmar återfår omedelbart prioritet.

Hela befintliga prestanda-/beteendesviten passerar, inklusive lookup/preference/fallback, random/setup/fade, sparning, uppstart, rendering/fokus och båda tysta audio-output-vägarna. Logg: `artifacts/performance/p1-random-validation-full.log`. Releasebygge och Windows PowerShell 5-rapporter passerar. Befintlig NU1510-varning kvarstår. Paketets samma riktade kontroller och DLL/PDB-/metadata-/dokumenthashar verifieras före leverans.

## Kontrollerad kostnadsjämförelse

Samma körning jämför sex observationer per väg för 248 giltiga kandidater. Filproben simulerar **en millisekunds väntetid med Stopwatch/SpinWait**; gamla vägen validerar alla, nya validerar tills ett korrekt val finns. Resultat: `artifacts/performance/p1-random-validation-final/random-pool-availability-checks.json`.

| Mått | Tidigare full kontroll | Kontroll vid behov |
| --- | --- | --- |
| Primärfilprober per tryck | 248 | 1 |
| Median kontroll/urval i simulerat prov | 248,51 ms | 1,04 ms |
| Intervall | 248,49–249,81 ms | 1,04–1,35 ms |

Detta belägger borttaget arbete och korrekt valfördelning, **inte** en faktisk ny ProBook-/hörbar starttid. Vanliga fil-/decoderöppningar, outputinitialisering och ljudets fade/innehåll kvarstår. Nästa laptopjämförelse använder samma stora/lilla grupp, filer, utgång, strömläge och sekvens som tidigare; den tidigare körningen var på batteri med offlineåtkomst i molnsynkad mapp. Ingen särskild ProBook-anpassning införs.

## Mätfält

`RandomPoolFileValidation` omfattar nu de provade kandidaterna och relevanta följdljuden, inte en full poolscan. `RandomPoolFileProbes` räknar faktiska filprober och `RandomPoolSelectedValidCount` rapporterar hittad primärmedlemskandidat/aktiv medlem, 0 eller 1. Fulla `RandomPoolAvailableCount` utgår eftersom hela gruppen inte undersöks. Playback-resultatet och eventuella startfel rapporteras separat.

Rapporten summerar dessa under `randomPoolWork`; äldre fångster utan markörerna får null/”not recorded”, inte en falsk nolla. Inga titel-, mediepath- eller tangentvärden läggs till. `Legacy-Capture.cmd` fortsätter vara standardvägen; output-reuse är fortfarande valbar prototyp. Det nya RAR-paketet packas upp i en separat mapp för faktisk jämförelse.
