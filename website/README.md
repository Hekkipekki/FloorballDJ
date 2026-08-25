# FloorballDJ Website

Static public landing page for FloorballDJ. It is intentionally separate from the private licensing API.

The Swedish site is served from `/`, while the complete English version is available under `/en/`.

## Launch switches

Edit `assets/site-config.js`:

- Set `downloadsEnabled` to `true` and add `downloadUrl` when a signed public installer is available.
- Set `purchasesEnabled` to `true`, add `checkoutUrl`, and set `priceLabel` after the payment webhook flow has passed production tests.

Never place Supabase secrets, the license signing key, or `LICENSE_ADMIN_API_KEY` in this site.

Before enabling purchases, follow `../docs/FASTSPRING-LANSERING.md`. The public checkout must remain disabled until license fulfillment and signed refund/chargeback handling have passed end-to-end tests.

## Local preview

Run `node scripts/serve.mjs` and open `http://127.0.0.1:4173`.
