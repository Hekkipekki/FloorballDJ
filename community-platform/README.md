# FloorballDJ Community (äldre Supabase-alternativ)

> Webbplatsens aktiva community använder nu Netlify Blobs via
> `website/netlify/functions/community.mjs`. Den här katalogen behålls endast
> som dokumentation för det tidigare, separata Supabase-upplägget och ska inte
> distribueras till produktionswebbplatsen.

Det här är den tidigare separata databasdelen för webbplatsens community. Den
används inte av den aktiva webbplatsen och delar inga tabeller eller
hemligheter med licenssystemet.

## Säkerhetsmodell

- Besökare behöver inget konto och anger endast ett smeknamn.
- Webbläsaren pratar enbart med Netlify-funktionen under `/api/community/*`.
- Den aktiva lösningen lagrar trådar, svar, rapporter och begränsningsdata i en
  site-scoped Netlify Blobs-butik.
- IP-adressen sparas aldrig. Servern lagrar endast ett saltat fingeravtryck för hastighetsbegränsning.
- Varje inlägg får en slumpmässig redigeringsnyckel. Klartexten sparas bara lokalt i den webbläsare som skapade inlägget.
- Administratören kan dölja, visa, låsa och radera trådar via `/community/admin/`.

## Aktiv konfiguration

Servervariablerna dokumenteras i `website/.env.example`. Den aktiva funktionen
finns i `website/netlify/functions/community.mjs` och publiceras tillsammans
med webbplatsen. Cloudflare Turnstile kan aktiveras senare som ett extra lager
mot automatiserad spam.

Administratörsnyckeln ska vara separat från licensadministrationens nyckel.
