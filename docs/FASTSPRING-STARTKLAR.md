# FastSpring – startklar engångslicens för FloorballDJ

Det här dokumentet beskriver vad som redan är implementerat och vad som återstår när ett
FastSpring-konto har godkänts. Köp ska vara avstängt på webbplatsen tills hela testmatrisen är
godkänd i FastSprings testmiljö.

## Affärsmodell

- Produkt: `floorballdj-windows`
- Typ: engångsköp, inte prenumeration
- Leverans: en standardlicens per order
- Aktivering: en aktiv Windows-dator åt gången
- Offlineperiod: 30 dagar mellan lyckade onlinekontroller
- Provperiod: sju dagar per fysisk dator, skapas utan licensnyckel
- Flytt: kunden avaktiverar den gamla datorn och aktiverar den nya

Framtida större huvudversioner kan säljas separat. Formulera alltid exakt vad som ingår på
köpsidan innan priset publiceras.

## Det som redan är förberett

1. `POST /api/v1/fastspring/license` tar emot FastSprings serveranrop, verifierar Basic Auth,
   godkänner endast den tillåtna produkten och kvantitet 1, skapar en licens och returnerar
   nyckeln som ren text.
2. Samma order kan anropas flera gånger utan att flera licenser skapas.
3. `POST /api/v1/fastspring/webhook` validerar `X-FS-Signature` mot den exakta råa request-body.
4. Händelserna `order.completed`, `return.created` och `chargeback.created` är idempotenta.
5. Återbetalning eller chargeback spärrar licensen och avaktiverar registrerade maskiner.
6. Supabase är inte åtkomligt direkt från desktopappen eller webbplatsen.
7. Publika köpknappar är avstängda i `website/assets/site-config.js`.

## Netlify-variabler

Följande ska finnas som hemligheter i Netlify Functions/Runtime:

| Variabel | Värde |
| --- | --- |
| `FASTSPRING_FULFILLMENT_USERNAME` | Slumpmässigt separat användarnamn för fulfillment |
| `FASTSPRING_FULFILLMENT_PASSWORD` | Minst 32 slumpmässiga bytes |
| `FASTSPRING_ALLOWED_PRODUCTS` | `floorballdj-windows` |
| `FASTSPRING_WEBHOOK_SECRET` | Hemligheten som skapas för webhooken i FastSpring |
| `FASTSPRING_WEBHOOK_MODE` | `test` under test, därefter `live` |
| `LICENSE_KEY_ENCRYPTION_KEY` | Separat slumpmässig hemlighet för återläsbara leveransnycklar |

Använd inte samma hemlighet till två ändamål. Rotera en hemlighet direkt om den hamnat i en
skärmbild, chatt, logg eller Git-historik.

## Produkt i FastSpring

1. Skapa en digital produkt med exakt path/id `floorballdj-windows`.
2. Välj engångsbetalning/perpetual product. Skapa ingen subscription plan.
3. Lägg in pris, valuta, produktbeskrivning, ikon och systemkrav.
4. Lägg till Remote License Fulfillment:
   - URL: `https://floorballdj-licensing-api.netlify.app/api/v1/fastspring/license`
   - metod: POST
   - Basic Auth: samma två fulfillment-värden som på Netlify
   - parametrar: `email`, `name`, `company`, `product`, `quantity`, `reference`, `order`, `account`
5. Konfigurera leveransen så att endpointens enradiga textsvar visas i orderbekräftelse och e-post
   som kundens licensnyckel.

Fälten `product`, `quantity`, `reference` och FastSprings interna `order` måste alltid skickas.
API:t avslår okänd produkt, annan kvantitet än 1 och saknade orderidentifierare.

## Webhook i FastSpring

1. Skapa endpointen
   `https://floorballdj-licensing-api.netlify.app/api/v1/fastspring/webhook`.
2. Prenumerera på `order.completed`, `return.created` och `chargeback.created`.
3. Kopiera webhook-secret till `FASTSPRING_WEBHOOK_SECRET`.
4. Behåll `FASTSPRING_WEBHOOK_MODE=test` tills testköp och retur fungerar.
5. Ett HTTP 500 betyder att licensen ännu inte kunnat matchas; FastSpring ska då försöka igen.
   Det hanterar att webhooken ibland kan komma före licensleveransen.

## Testmatris innan live

- Godkänt testköp ger exakt en nyckel och exakt en licens i adminsidan.
- Omsänt fulfillment-anrop ger samma nyckel, inte en ny licens.
- Kvantitet 2 och okänt produkt-id avslås.
- Nyckeln aktiveras på en dator men inte samtidigt på en andra.
- Avaktivering gör att samma nyckel kan flyttas till en annan dator.
- Återbetalning och chargeback spärrar licensen och avaktiverar maskinen.
- Dubblerad webhook ändrar inte status två gånger.
- Sju dagars provperiod återställs inte av ominstallation eller ändrad Windows-klocka.
- Offlinebevis, timeout och felmeddelanden fungerar utan att pågående ljud avbryts.
- Integritets-, villkors-, återbetalnings- och kontaktsidor innehåller slutliga säljaruppgifter.

## Växla till live

Först efter godkänd KYC och genomförd testmatris:

1. Sätt `FASTSPRING_WEBHOOK_MODE=live` och gör en ny Netlify-deploy.
2. Fyll i riktigt pris och FastSpring checkout-URL i `website/assets/site-config.js`.
3. Sätt `legalReady: true` och därefter `purchasesEnabled: true`.
4. Gör ett riktigt köp till lågt testpris, verifiera licens, kvitto och återbetalning.
5. Återställ ordinarie pris och publicera köpet.

Köpknappen ska aldrig aktiveras bara för att kontot finns; även webhook, juridiska uppgifter och
det verkliga returflödet måste vara verifierade.

## Underlag för FastSpring-ansökan

- Vad säljs: downloadable Windows desktop software / audio playback software.
- Affärsmodell: one-time perpetual license for one active computer.
- Webbplats: `https://floorballdj.netlify.app/`.
- Testbar produktbeskrivning, skärmbilder, nedladdning och sju dagars provperiod finns redan.
- Komplettera före granskning med eget domännamn eller åtminstone domänbaserad arbetsmail,
  juridiskt namn, adress, supportkontakt, pris och slutliga policyer.

FastSpring stöder utbetalningskonton för både `Individual` och `Business`. Ett börsnoterat bolag
krävs inte. En privatperson måste kunna verifiera identitet och skatteuppgifter; ett företag
måste kunna verifiera registreringsuppgifter och verklig huvudman. För återkommande kommersiell
försäljning i Sverige är enskild näringsverksamhet ofta den enklaste vägen, men kontrollera den
egna situationen med Skatteverket eller rådgivare.
