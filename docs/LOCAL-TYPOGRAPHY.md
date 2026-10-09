# Titelstorlek per dator

Titelstorlek under Inställningar → Typografi sparas i datorns `profile-preferences.json` under FloorballDJ:s lokala användarmapp. Den gäller alla musikprofiler på den datorn. Typsnitt och övriga profilinställningar behåller tidigare beteende.

Vid första starten används den först öppnade äldre profilens titelstorlek (9–40 px). Utan äldre profil används 15 px. Därefter ändrar profilbyte, import, revisionsåterställning och flyttbackup inte datorns val. Nya profilfiler och flyttbackuper innehåller inte titelstorleken. Äldre profilfiler kan fortfarande läsas.

Inställningsfönstrets förhandsvisning använder ett separat utkast. Avbryt ändrar inget; Spara uppdaterar både synliga låtknappar och datorns inställning. Om lokal lagring misslyckas visas ett fel.

Kontroller: två separata datorinställningar mot samma profil; engångsmigrering; sparning, profilbyte, äldre revision och flyttbackup; riktigt inställningsfönster med Spara/Avbryt; omstart och intervallgränser. Ingår i `FloorballDJ.PerformanceSmoke`, kan köras separat med `--local-font-checks`.
