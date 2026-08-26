# FloorballDJ Website

Static public landing page for FloorballDJ. It is intentionally separate from the private licensing API.

English is the default language and `/` redirects to the complete English site under `/en/`.
The Swedish home page is available under `/sv/`; the remaining Swedish pages keep their established routes.

## Launch switches

Edit `assets/site-config.js`:

- Set `downloadsEnabled` to `true` and add `downloadUrl` when a signed public installer is available.
- Set `purchasesEnabled` to `true`, add `checkoutUrl`, and set `priceLabel` after the payment webhook flow has passed production tests.

Never place Supabase secrets, the license signing key, or `LICENSE_ADMIN_API_KEY` in this site.

Before enabling purchases, follow `../docs/FASTSPRING-LANSERING.md`. The public checkout must remain disabled until license fulfillment and signed refund/chargeback handling have passed end-to-end tests.

## Local preview

Run `node scripts/serve.mjs` and open `http://127.0.0.1:4173`.

## Deployment

The current Netlify site has no Git build settings and is deployed from this directory with `npx netlify deploy --prod --dir .`. A GitHub push alone does not update the public site. If continuous deployment is enabled later, connect `Hekkipekki/FloorballDJ`, use `main` as the production branch, `website` as the base directory, no build command, and `.` as the publish directory.
