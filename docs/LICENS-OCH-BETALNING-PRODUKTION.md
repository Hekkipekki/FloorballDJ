# FloorballDJ – licenser och betalning inför produktion

## Nuvarande skydd

- Desktopappen innehåller endast den publika ES256-nyckeln. Supabase-hemlighet, licenspepper och privat signeringsnyckel finns bara i Netlify Functions.
- Provperioden skapas atomiskt i Supabase och kopplas både till installationen och datorns Windows MachineGuid. Att avinstallera och installera appen igen skapar därför inte en ny provperiod.
- Provperioden kontrolleras online vid varje programstart och använder databasens tid. Att ställa tillbaka Windows-klockan förlänger inte provperioden.
- En standardlicens tillåter en aktiv dator. Masterlicenser kan tillåta obegränsat antal datorer, men varje aktivering registreras och kan stängas av.
- Köpta licenser använder tidsbegränsade, signerade offlinebevis. Standardvärdet är 30 dagar innan en ny onlinekontroll krävs.
- Lokalt licensbevis och aktiveringshemlighet skyddas med Windows DPAPI för aktuell Windows-användare.
- Databastabellerna är inte åtkomliga för `anon` eller `authenticated`. Endast serverrollen får köra licensfunktionerna.

## Avsiktlig säkerhetsgräns

Windows MachineGuid stoppar normalt ominstallation av själva appen men kan ändras vid en fullständig ominstallation av Windows och kan manipuleras av en tekniskt kunnig angripare. Hårdvaruattestering via TPM eller en extern DRM-tjänst kan höja tröskeln, men riskerar samtidigt att låsa ute legitima användare efter moderkortsbyte, service eller företagsavbildningar.

För en första kommersiell version är nuvarande modell en bra balans: den hindrar normal trial-reset, nyckeldelning och klockfusk utan att skapa onödiga supportärenden. Följ avvikande mönster i adminsidan och skärp först om faktisk missbruk syns.

## Rekommenderat FastSpring-flöde

1. Kunden betalar i FastSprings butik. FastSpring är Merchant of Record och hanterar skatt/moms och betalningsuppgifter.
2. Produkten anropar `POST /api/v1/fastspring/license` som Remote License Fulfillment.
3. Endpointen verifierar separat Basic Auth, skapar en standardlicens för en dator och returnerar licensnyckeln som en enda textrad.
4. FastSpring visar/skickar nyckeln i sin orderbekräftelse. FloorballDJ eller Supabase tar aldrig emot kortuppgifter.
5. Orderreferensen är unik i databasen. Om FastSpring gör om samma anrop returneras samma nyckel i stället för att skapa en dubblett.

Aktivera inte betalning förrän en riktig testprodukt finns. Då ska även signerade webhookar för återbetalning och chargeback kopplas till licensens `external_order_id` och sätta licensen till `revoked`. Webhookhanteringen måste spara FastSprings event-id och vara idempotent innan den får ändra licensstatus.

## Produktionschecklista

- Byt till Supabases `sb_secret_...`-nyckel på Netlify om en äldre `service_role`-JWT fortfarande används.
- Kontrollera att `LICENSE_TOKEN_ISSUER` exakt är `https://floorballdj-licensing-api.netlify.app` så att den matchar desktopappen.
- Behåll alla hemliga Netlify-variabler markerade som secrets och ge dem endast Functions/Runtime-scope när abonnemanget tillåter det.
- Rotera omedelbart en hemlighet som har synts i skärmbild, logg, Git eller chatt.
- Kör Supabase Security Advisor efter varje migration och kontrollera att inga licenstabeller eller SECURITY DEFINER-funktioner blivit publikt åtkomliga.
- Lägg FastSpring-uppgifter först när en testprodukt är skapad. Använd separata uppgifter för fulfillment och webhooksignering.
- Testa köp, dubblerat fulfillment-anrop, återbetalning, avaktivering, offlineperiod och aktiveringsgräns i sandbox innan köpknappen visas publikt.
- Spara minsta möjliga kunddata: e-post, namn/förening, order-id och nödvändig maskinmetadata. Dokumentera gallring och supportändamål i integritetspolicyn.

## Betalningsalternativ

FastSpring passar om målet är att slippa egen hantering av moms och kortdata. Paddle är ett rimligt alternativ med liknande Merchant-of-Record-upplägg. Stripe ger mer kontroll men innebär normalt mer eget ansvar för skatt, kvitton, återbetalningsflöden och licensorkestrering. För FloorballDJ är FastSpring eller Paddle den enklaste säkra vägen.

## Genomförd härdning 11 augusti 2026

- Varje signerat licens- och provbevis innehåller nu en bindning till både installationen och
  maskinen. En lokalt kopierad token godkänns därför inte på en annan dator.
- Desktopappen godkänner bara kända signeringsnyckel-ID:n, vilket förbereder kontrollerad
  nyckelrotation.
- Publika aktiverings-, refresh- och trial-endpoints har serverbaserad rate limiting.
- Licensaktivering kan återanvända historiken efter avaktivering utan att bryta databasens
  unika index.
- FastSpring-fulfillment kräver exakt en tillåten engångsprodukt och är idempotent per order.
- Signerade webhookar för order, återbetalning och chargeback behandlas idempotent i databasen.
- Uppdateraren begränsar installerarens storlek, verifierar SHA-256 och accepterar bara release-
  länkar från det officiella GitHub-repot.

Den exakta aktiveringsordningen och kvarvarande externa steg finns i
`docs/FASTSPRING-STARTKLAR.md`.
