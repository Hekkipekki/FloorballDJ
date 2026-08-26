# FloorballDJ Community

Det här är den separata databasdelen för webbplatsens öppna community. Den ska kopplas till ett eget Supabase-projekt och delar inga tabeller eller hemligheter med licenssystemet.

## Säkerhetsmodell

- Besökare behöver inget konto och anger endast ett smeknamn.
- Webbläsaren pratar enbart med Netlify-funktionen under `/api/community/*`.
- Supabase-hemligheten lämnar aldrig servern och finns inte i webbplatsens JavaScript.
- Tabellerna ligger i ett privat schema, har RLS aktiverat och saknar rättigheter för `anon` och `authenticated`.
- IP-adressen sparas aldrig. Servern lagrar endast ett saltat fingeravtryck för hastighetsbegränsning.
- Varje inlägg får en slumpmässig redigeringsnyckel. Klartexten sparas bara lokalt i den webbläsare som skapade inlägget.
- Administratören kan dölja, visa, låsa och radera trådar via `/community/admin/`.

## Aktivering

1. Skapa ett separat Supabase-projekt, exempelvis `FloorballDJ Community`.
2. Länka den här katalogen till projektet med Supabase CLI och kör `supabase db push`, eller klistra in migreringen i `supabase/migrations/` i SQL Editor.
3. Lägg in variablerna från `website/.env.example` på FloorballDJ-webbplatsen i Netlify.
4. Låt `COMMUNITY_POSTING_ENABLED` vara `false` medan du provar läsning och administration.
5. Aktivera Cloudflare Turnstile och ange dess servernyckel innan offentlig publicering rekommenderas.
6. Sätt därefter `COMMUNITY_POSTING_ENABLED` till `true` och gör en ny deploy.

Administratörsnyckeln ska vara separat från licensadministrationens nyckel.
