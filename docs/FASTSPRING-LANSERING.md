# FastSpring – lanseringsguide för FloorballDJ

Det här dokumentet är den gemensamma checklistan för den dag då FastSpring-kontot är godkänt och FloorballDJ ska börja säljas. Betalningskoden är förberedd, men köp ska inte aktiveras innan samtliga tester nedan är godkända.

Lägg aldrig lösenord, API-nycklar, webhook-hemligheter eller privata signeringsnycklar i Git eller i det här dokumentet.

## Beslutad produktmodell

- Engångsköp, inte abonnemang.
- En standardlicens får vara aktiv på en dator åt gången.
- Licensen kan flyttas genom att den gamla datorn avaktiveras.
- Köpt licens får ett signerat offlinebevis som normalt förnyas var 30:e dag.
- Den automatiska provperioden är separat och gäller en gång per fysisk dator.
- Admin-/masterlicenser skapas endast manuellt i FloorballDJ:s skyddade adminvy och säljs inte i den publika butiken.

## Det som redan finns i koden

- FastSpring fulfillment-endpoint: `POST https://floorballdj-licensing-api.netlify.app/api/v1/fastspring/license`
- Endpointen skyddas med separat HTTP Basic Authentication.
- Samma FastSpring-order kan skickas igen utan att skapa dubbla licenser.
- Ett lyckat svar är licensnyckeln som en enda rad ren text, avsedd för FastSprings kvitto/orderbekräftelse.
- Följande fält tas emot: `email`, `name`, `company`, `product`, `reference`, `subscription` och `account`.
- `email` och den unika orderreferensen `reference` är obligatoriska.
- Köpta licenser registreras med betalprovider `fastspring`, orderstatus `completed` och en aktiveringsgräns på en dator.

## 1. Slutför FastSpring-kontot

1. Slutför identitets-, skatte- och utbetalningskontrollerna som FastSpring begär.
2. Lägg in supportadress, produktwebbplats och korrekta företags-/säljaruppgifter.
3. Skapa först en testprodukt för ett engångsköp.
4. Bestäm produktens publika namn, pris, valuta, supportvillkor och återbetalningspolicy.
5. Spara FastSprings produkt-ID här när det är känt: `____________________________`.

## 2. Konfigurera Remote License Fulfillment

Skapa två nya, slumpmässiga hemligheter och lägg dem i Netlify för licensing-sajten:

| Netlify-variabel | Innehåll |
| --- | --- |
| `FASTSPRING_FULFILLMENT_USERNAME` | Unikt användarnamn endast för FastSpring fulfillment |
| `FASTSPRING_FULFILLMENT_PASSWORD` | Långt slumpmässigt lösenord endast för FastSpring fulfillment |

Markera båda som hemliga och ge dem endast Functions/Runtime-scope när Netlify-planen tillåter det. Samma uppgifter anges i FastSprings Remote Server Request-inställning.

Konfigurera anropet så här:

- Metod: `POST`
- URL: `https://floorballdj-licensing-api.netlify.app/api/v1/fastspring/license`
- Autentisering: HTTP Basic med variablerna ovan
- Obligatoriska parametrar:
  - `email` = kundens e-postadress
  - `reference` = FastSprings unika orderreferens
- Rekommenderade parametrar:
  - `name` = kundens namn
  - `company` = förening eller företag
  - `product` = FastSprings produkt-ID
  - `account` = FastSprings kund-/konto-ID
  - `subscription` = lämnas tomt för engångsköp, men stöds av endpointen
- Svar: behandla hela textsvarsraden som licensnyckeln och inkludera den i orderbekräftelsen.

## 3. Återbetalning och chargeback – blockerar publik försäljning

Den nuvarande fulfillment-endpointen skapar licensen efter köp. Innan köpknappen får aktiveras måste en separat, signerad FastSpring-webhook implementeras och verifieras för minst:

- genomfört köp/order completed
- fullständig återbetalning
- chargeback/tvist
- annullerad eller återkallad order där det är relevant

Webhooken ska:

1. verifiera FastSprings signatur innan någon data används,
2. spara FastSprings event-ID så att samma event bara behandlas en gång,
3. matcha ordern mot `external_order_id`,
4. spärra licensen vid återbetalning eller chargeback,
5. logga händelsen utan kortuppgifter eller onödiga personuppgifter,
6. svara säkert även när samma event skickas på nytt.

FastSprings verkliga webhookformat och signeringshemlighet ska hämtas från det godkända kontot. Implementera inte antagna fältnamn från exempel innan de har jämförts med kontots aktuella dokumentation och testevent.

## 4. Sandbox- och end-to-end-test

Följande ska testas med en riktig FastSpring-testorder:

- [ ] Betalningen lyckas och exakt en licens skapas.
- [ ] Licensnyckeln visas i kvittot och skickas till rätt e-postadress.
- [ ] Nyckeln aktiveras på en ren Windows-dator.
- [ ] Samma order/fulfillment-anrop returnerar samma nyckel utan dubblett.
- [ ] En standardlicens nekas på dator nummer två medan första aktiveringen är aktiv.
- [ ] Avaktivering på första datorn gör det möjligt att aktivera den andra.
- [ ] Återbetalning spärrar licensen.
- [ ] Chargeback spärrar licensen.
- [ ] Dubbla webhookevent ändrar inte tillståndet flera gånger.
- [ ] Adminvyn visar kund, order, licens och maskin korrekt.
- [ ] Programmet beter sig säkert under offlineperiod och efter spärrning.
- [ ] Supportflödet för borttappad nyckel har testats.

## 5. Juridik och kundinformation – blockerar publik försäljning

Innan lansering behöver webbplatsen publicerade och granskade sidor för:

- integritetspolicy och vilka person-/maskinuppgifter som sparas,
- licensvillkor/EULA,
- köp-, support- och återbetalningsvillkor,
- kontaktuppgifter och korrekt juridisk säljaridentitet,
- information om att FastSpring är betalnings-/Merchant-of-Record-part där det är korrekt för kontot.

Skriv inte in preliminära företagsuppgifter som om de vore juridiskt fastställda. Kontrollera svensk konsument-, bokförings- och dataskyddshantering med relevant rådgivare före försäljning.

## 6. Slå på köp på webbplatsen

Först när alla blockerande punkter ovan är klara:

1. Lägg FastSprings publika checkout-URL i `website/assets/site-config.js` som `checkoutUrl`.
2. Ange det slutliga priset i `priceLabel`.
3. Ändra `purchasesEnabled` till `true`.
4. Publicera webbplatsen och gör ett sista köp från den publika sidan.
5. Kontrollera licensmejl, aktivering, adminvy, kvitto och återbetalning en sista gång.

## 7. Produktionsövervakning och återställning

- Följ felkvot och svarstid för Netlify Functions.
- Följ misslyckade fulfillment- och webhookevent i FastSpring.
- Behåll en enkel supportlogg för order-ID, åtgärd och tidpunkt utan onödiga personuppgifter.
- Ha en dokumenterad rutin för att stänga av `purchasesEnabled` om fulfillment eller databasen får problem.
- Rotera omedelbart en hemlighet som har visats i chatt, skärmbild, logg eller Git.
- Kör Supabase Security Advisor efter databasändringar och kontrollera att licenstabellerna fortfarande är privata.

## Värden att fylla i när kontot är live

| Uppgift | Värde |
| --- | --- |
| FastSpring store/storefront | `____________________________` |
| Produkt-ID | `____________________________` |
| Publik checkout-URL | `____________________________` |
| Supportadress | `____________________________` |
| Slutligt pris och valuta | `____________________________` |
| Webhook-endpoint | `Inte implementerad ännu` |
| Senast godkända sandbox-test | `____________________________` |
| Ansvarig för lanseringsgodkännande | `____________________________` |

## Lanseringsregel

Om fulfillment, signerad återbetalningshantering, juridiska sidor eller en riktig end-to-end-testorder saknas ska `purchasesEnabled` vara `false`. Det påverkar inte fortsatt betatestning, manuellt skapade licenser eller framtida utveckling.
