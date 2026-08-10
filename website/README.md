# FloorballDJ Website

Static public landing page for FloorballDJ. It is intentionally separate from the private licensing API.

## Launch switches

Edit `assets/site-config.js`:

- Set `downloadsEnabled` to `true` and add `downloadUrl` when a signed public installer is available.
- Set `purchasesEnabled` to `true`, add `checkoutUrl`, and set `priceLabel` after the payment webhook flow has passed production tests.

Never place Supabase secrets, the license signing key, or `LICENSE_ADMIN_API_KEY` in this site.

## Local preview

Run `node scripts/serve.mjs` and open `http://127.0.0.1:4173`.
