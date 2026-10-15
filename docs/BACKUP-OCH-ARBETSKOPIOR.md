# Flytta FloorballDJ mellan föreningens datorer

## USB eller enstaka överflyttning

1. Välj **Profil → Spara flyttpaket för USB eller Drive…**.
2. Spara och namnge profilen om programmet ber om det.
3. Lägg `.fdjbackup`-filen på USB eller i Google Drive.
4. På nästa dator: välj **Profil → Öppna flyttpaket…**.

Paketet innehåller profil, deck, registrerade Autoplay-spellistor, den aktuella Autoplay-kön och alla ljud som dessa använder. Även köljud som inte finns i något deck tas med. Profilnamnet behålls. Ljudfilerna kopieras utan omkodning.

## Återkommande överflyttning med Google Drive

1. Installera och konfigurera Google Drive för datorn på båda datorerna.
2. Hemma: välj **Profil → Google Drive / synkmapp → Spara till synkmapp** och välj föreningens mapp. Platsen koms ihåg till nästa gång. Samma flöde fungerar med en USB-mapp.
3. Låt Google Drive slutföra uppladdningen innan du lämnar datorn.
4. På föreningsdatorn: välj **Hämta från synkmapp…**, peka ut mappen och välj version efter profilnamn, datum och dator.
5. Låt importen bli klar. Filerna kontrolleras och kopieras till datorns lokala lagring. Välj därefter den datorns ljudutgångar.

Enhetsbokstäverna får skilja sig: C: hemma och G: eller H: i föreningen. Sökvägarna i den flyttbara uppsättningen är relativa. Efter import behöver uppspelningen varken Google Drive, internet eller USB-minnet.

Varje sparad synkversion är separat. Om två datorer gör ändringar kan båda versionerna väljas; ändringarna slås inte automatiskt ihop. Identiska ljudfiler återanvänds i synkmappen. Tidigare versioner och deras ljud raderas inte automatiskt.

**”Versionen är skapad” betyder att FloorballDJ är färdig lokalt. Google Drive sköter själva molnsynkningen.** Programmet hävdar inte att uppladdningen är klar. En fil som inte hunnit synkas, saknas eller har ändrats gör att importkontrollen stoppar överföringen.

## Autosparning och manuellt sparade profiler

- Ändringar autosparas i en lokal arbetskopia per profil. Den ligger utanför synkmappen och visas inte bland namngivna profiler.
- **Spara / Spara som…** uppdaterar den namngivna profilen. Efter lyckad sparning tas arbetskopian och dess tillfälliga historik bort.
- Vid avslut, öppning av annan profil eller nytt projekt visas **Spara**, **Spara inte**, **Avbryt** om ändringar finns.
- **Spara inte** tar bort arbetskopian och lämnar den sparade profilen orörd. **Avbryt**, avbruten namngivning eller misslyckad sparning behåller programmet och ändringarna.
- Efter krasch kan arbetskopian återställas vid nästa programstart. Du granskar och sparar den sedan själv.
- Även den aktuella Autoplay-kön och dess Shuffle/Loop-val ingår i arbetskopian och profilen. Pågående uppspelning återupptas inte automatiskt.
- Spelräknare sparas inte och orsakar ingen sparvarning. Varje programstart börjar med en ny session där alla låtar är ospelade.

Licens och datorns ljudenhetsval behöver hanteras på respektive dator. Oanvänd musik som inte finns i deck, registrerade spellistor eller aktuell kö tas inte med.

Den äldre kompletta flyttbackupen i mappformat fungerar också och kan återställas som tidigare.

## Verifiering

Kontrollerna i `FloorballDJ.PerformanceSmoke --transfer-draft-checks` täcker bland annat omflyttade mapprötter efter borttagning av originalfiler, spellistor med ljud utanför decken, aktuell kö, paketfiler, återanvändning av ljud, val av äldre synkversion, saknade/skadade filer, separata arbetskopior, sparfel, återställning och verkliga stängningsval. Dialogerna renderas på svenska och engelska vid körning med `--output`.

Tester med lokala mappar simulerar överföringen. De intygar inte att Google Drives klient har slutfört en faktisk uppladdning mellan två fysiska datorer.
