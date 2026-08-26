import { createHash, randomBytes, timingSafeEqual } from "node:crypto";

const jsonHeaders = { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" };

const response = (status, data) => new Response(JSON.stringify(data), { status, headers: jsonHeaders });
const clean = (value) => typeof value === "string" ? value.trim() : "";
const hash = (value) => createHash("sha256").update(value).digest("hex");

function settings() {
  return {
    url: Netlify.env.get("COMMUNITY_SUPABASE_URL")?.replace(/\/$/, ""),
    secret: Netlify.env.get("COMMUNITY_SUPABASE_SECRET_KEY"),
    adminKey: Netlify.env.get("COMMUNITY_ADMIN_API_KEY"),
    ipPepper: Netlify.env.get("COMMUNITY_IP_PEPPER"),
    editPepper: Netlify.env.get("COMMUNITY_EDIT_TOKEN_PEPPER"),
    turnstileSecret: Netlify.env.get("COMMUNITY_TURNSTILE_SECRET_KEY"),
    postingEnabled: Netlify.env.get("COMMUNITY_POSTING_ENABLED") === "true"
  };
}

async function rpc(config, name, body = {}) {
  const result = await fetch(`${config.url}/rest/v1/rpc/${name}`, {
    method: "POST",
    headers: {
      apikey: config.secret,
      authorization: `Bearer ${config.secret}`,
      "content-type": "application/json"
    },
    body: JSON.stringify(body)
  });
  const payload = await result.json().catch(() => null);
  if (!result.ok) {
    const error = new Error(payload?.message || "Community-databasen svarade inte.");
    error.code = payload?.message;
    throw error;
  }
  return payload;
}

function clientFingerprint(request, pepper) {
  const raw = request.headers.get("x-nf-client-connection-ip") ||
    request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() || "unknown";
  return hash(`${pepper}:${raw}`);
}

function secureAdminMatch(request, expected) {
  const supplied = request.headers.get("authorization")?.replace(/^Bearer\s+/i, "") || "";
  if (!supplied || !expected) return false;
  const left = Buffer.from(hash(supplied), "hex");
  const right = Buffer.from(hash(expected), "hex");
  return timingSafeEqual(left, right);
}

async function validateTurnstile(config, token, request) {
  if (!config.turnstileSecret) return true;
  if (!token) return false;
  const form = new URLSearchParams({ secret: config.turnstileSecret, response: token });
  const ip = request.headers.get("x-nf-client-connection-ip");
  if (ip) form.set("remoteip", ip);
  const verify = await fetch("https://challenges.cloudflare.com/turnstile/v0/siteverify", { method: "POST", body: form });
  return Boolean((await verify.json().catch(() => null))?.success);
}

function validatePost(kind, body) {
  const nickname = clean(body.nickname);
  const title = clean(body.title);
  const content = clean(body.body);
  if (nickname.length < 2 || nickname.length > 40) return "Smeknamnet måste vara 2–40 tecken.";
  if (kind === "thread" && (title.length < 4 || title.length > 120)) return "Rubriken måste vara 4–120 tecken.";
  const minimum = kind === "thread" ? 10 : 2;
  const maximum = kind === "thread" ? 5000 : 3000;
  if (content.length < minimum || content.length > maximum) return `Texten måste vara ${minimum}–${maximum} tecken.`;
  if (clean(body.website)) return "Inlägget kunde inte skickas.";
  return null;
}

function mapError(error) {
  if (error?.code?.includes("RATE_LIMIT")) return response(429, { error: "Du har skickat flera inlägg på kort tid. Vänta en stund och försök igen." });
  if (error?.code?.includes("THREAD_LOCKED")) return response(409, { error: "Tråden är låst för nya svar." });
  if (error?.code?.includes("THREAD_NOT_FOUND")) return response(404, { error: "Tråden finns inte längre." });
  console.error("Community API error", error);
  return response(500, { error: "Communityt kunde inte behandla begäran." });
}

export default async (request) => {
  const config = settings();
  const url = new URL(request.url);
  const route = url.searchParams.get("route") || url.pathname.replace(/^\/api\/community\/?/, "");
  const parts = route.split("/").filter(Boolean);

  if (parts[0] === "health") {
    return response(200, { configured: Boolean(config.url && config.secret), postingEnabled: config.postingEnabled });
  }
  if (!config.url || !config.secret || !config.ipPepper || !config.editPepper) {
    return response(503, { error: "Communityt håller på att konfigureras." });
  }

  try {
    if (parts[0] === "admin") {
      if (!secureAdminMatch(request, config.adminKey)) return response(401, { error: "Ogiltig administratörsnyckel." });
      if (request.method === "GET" && parts.length === 1)
        return response(200, await rpc(config, "community_admin_overview"));
      if (request.method === "PATCH" && parts[1] === "threads" && parts[2]) {
        const body = await request.json().catch(() => ({}));
        const allowed = ["hide", "show", "lock", "unlock", "delete"];
        if (!allowed.includes(body.action)) return response(400, { error: "Okänd åtgärd." });
        return response(200, { ok: await rpc(config, "community_admin_update_thread", { p_id: parts[2], p_action: body.action }) });
      }
      return response(404, { error: "Administrationsfunktionen finns inte." });
    }

    if (request.method === "GET" && parts[0] === "threads" && !parts[1]) {
      const limit = Math.min(50, Math.max(1, Number(url.searchParams.get("limit")) || 25));
      const offset = Math.max(0, Number(url.searchParams.get("offset")) || 0);
      return response(200, await rpc(config, "community_list_threads", { p_limit: limit, p_offset: offset }));
    }
    if (request.method === "GET" && parts[0] === "threads" && parts[1]) {
      const thread = await rpc(config, "community_get_thread", { p_id: parts[1] });
      return thread ? response(200, thread) : response(404, { error: "Tråden finns inte längre." });
    }

    if (request.method === "POST" && parts[0] === "reports" && parts[1] && parts[2]) {
      const body = await request.json().catch(() => ({}));
      const reason = clean(body.reason);
      if (reason.length < 3 || reason.length > 500) return response(400, { error: "Beskriv problemet med 3–500 tecken." });
      const ok = await rpc(config, "community_report_content", {
        p_kind: parts[1], p_id: parts[2], p_reason: reason,
        p_fingerprint: clientFingerprint(request, config.ipPepper)
      });
      return ok ? response(201, { ok: true }) : response(404, { error: "Inlägget finns inte." });
    }

    if (request.method === "POST") {
      if (!config.postingEnabled) return response(503, { error: "Nya inlägg öppnas snart." });
      const body = await request.json().catch(() => ({}));
      if (!await validateTurnstile(config, body.turnstileToken, request)) return response(400, { error: "Verifieringen misslyckades. Försök igen." });
      const kind = parts[0] === "threads" && parts[1] && parts[2] === "replies" ? "reply" : "thread";
      const validation = validatePost(kind, body);
      if (validation) return response(400, { error: validation });
      const token = randomBytes(32).toString("base64url");
      const tokenHash = hash(`${config.editPepper}:${token}`);
      const fingerprint = clientFingerprint(request, config.ipPepper);
      if (kind === "reply") {
        const created = await rpc(config, "community_create_reply", {
          p_thread_id: parts[1], p_nickname: clean(body.nickname), p_body: clean(body.body),
          p_edit_token_hash: tokenHash, p_fingerprint: fingerprint
        });
        return response(201, { ...created, editToken: token });
      }
      if (parts[0] !== "threads" || parts[1]) return response(404, { error: "Funktionen finns inte." });
      const created = await rpc(config, "community_create_thread", {
        p_nickname: clean(body.nickname), p_title: clean(body.title), p_body: clean(body.body),
        p_edit_token_hash: tokenHash, p_fingerprint: fingerprint
      });
      return response(201, { ...created, editToken: token });
    }

    if (request.method === "DELETE" && ["threads", "replies"].includes(parts[0]) && parts[1]) {
      const token = request.headers.get("x-edit-token") || "";
      if (!token) return response(401, { error: "Redigeringsnyckeln saknas i den här webbläsaren." });
      const ok = await rpc(config, "community_delete_own_content", {
        p_kind: parts[0] === "threads" ? "thread" : "reply",
        p_id: parts[1], p_edit_token_hash: hash(`${config.editPepper}:${token}`)
      });
      return ok ? response(200, { ok: true }) : response(403, { error: "Inlägget kan inte raderas från den här webbläsaren." });
    }

    return response(404, { error: "Community-funktionen finns inte." });
  } catch (error) {
    return mapError(error);
  }
};
