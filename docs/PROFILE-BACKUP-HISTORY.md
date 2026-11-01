# Beständig lokal profilhistorik

Originalprofilen skrivs bara vid manuell sparning. Autosparning, Spara inte och återställning skriver inte över originalet.

## Det användaren ser

- Varje profilfil har en separat lokal backupmapp under `%LOCALAPPDATA%\FloorballDJ\Profilbackuper`.
- Den senaste profilkopian heter exempelvis `Dalen backup.floorballdj.json`.
- Daterade, oföränderliga kopior ligger i mappens `Revisioner`. Metadata innehåller typ, tidpunkt, sammanfattning och ändringslogg.
- **Hjälp → Backup och revisionshistorik** visar den aktuella profilens versioner. Listan kan filtreras på sparade versioner eller osparade ändringar och sökas efter datum, låtnamn eller ändring.
- **Återställ för granskning** skyddar först nuvarande arbete, stoppar uppspelningen och öppnar vald version som en osparad ändring. Originalet ersätts först med Spara.

Profilkopiorna innehåller profilens data, inklusive Autoplay-kö och inställningar. Sökvägar till ljud och externa spellistor är absoluta så att den separata backupfilen kan öppnas från sin lokala mapp. Själva externa filerna kopieras med den befintliga kompletta flyttbackupen.

## Bevarande

- Sparade originalversioner och manuella sparningar behålls.
- Slutläget efter **Spara inte** behålls, även när användaren stänger före autosparningens fördröjning.
- Arbetsläget före en återställning behålls. Ett redan skyddat manuellt läge nedgraderas inte till en vanlig autosparning.
- De 100 senaste tätare automatiska revisionerna behålls separat. Rotationen tar aldrig bort de skyddade punkterna ovan.
- Kraschåterställningens tillfälliga arbetskopia kan rensas utan att historiken raderas. Att avstå från kraschåterställning skyddar också det senaste återställningsinnehållet i historiken.
- Befintliga äldre original- och arbetskopierevisioner importeras utan att de gamla filerna raderas.

## Identitet och skrivning

Historiken skiljer profilfiler genom deras normaliserade fullständiga sökvägar. Två filer med samma namn i olika mappar blandas inte. Spara som till en ny fil förgrenar historiken; fortsatta ändringar i kopian påverkar inte originalets historik. Flytt eller namnbyte utanför programmet skapar en ny filidentitet vid nästa öppning.

Backupskrivningar och historikläsning använder samma ordnade bakgrundskö och processlås som profilsparningen. Originalet får inte ersättas om den föregående versionen och den nya kopian inte kan säkerhetskopieras. Före manuell överskrivning kontrolleras originalets faktiska innehåll, även om ett annat program har bevarat filens storlek och datum.

Historiken är lokal på datorn. Skyddade punkter tas inte bort automatiskt och kan därför växa över tid.

## Verifiering

`--profile-history-checks` kontrollerar manuell överskrivning, Spara inte före autosparning, omstart, granskning före sparning, medie- och spellistsökvägar, separata profiler, Spara som, rotation, äldre historik, avstådd kraschåterställning, backupfel samt originaländringar med oförändrade filmetadata. WPF-kontroller täcker filter, sökning, ändringslogg och återställning på svenska/engelska vid 1240 × 780 och 1040 × 680. Kontrollerna ingår i den samlade regressionssviten och i releaseflödet.
