import { getStore } from "@netlify/blobs";
import { createHash, randomBytes, randomUUID, timingSafeEqual } from "node:crypto";

const jsonHeaders = { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" };
const validId = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const response = (status, data) => new Response(JSON.stringify(data), { status, headers: jsonHeaders });
const clean = (value) => typeof value === "string" ? value.trim() : "";
const hash = (value) => createHash("sha256").update(value).digest("hex");
const threadKey = (id) => `threads/${id}.json`;
const replyKey = (threadId, id) => `replies/${threadId}/${id}.json`;

function settings() {
  const adminKey = Netlify.env.get("COMMUNITY_ADMIN_API_KEY");
  return {
    adminKey,
    // Separate variables can be supplied later. Until then, namespaced
    // derivatives of the strong admin secret keep fingerprints and edit
    // tokens independent without blocking the community launch.
    ipPepper: Netlify.env.get("COMMUNITY_IP_PEPPER") || (adminKey ? `ip:${adminKey}` : ""),
    editPepper: Netlify.env.get("COMMUNITY_EDIT_TOKEN_PEPPER") || (adminKey ? `edit:${adminKey}` : ""),
    turnstileSecret: Netlify.env.get("COMMUNITY_TURNSTILE_SECRET_KEY"),
    postingEnabled: Netlify.env.get("COMMUNITY_POSTING_ENABLED") !== "false"
  };
}

function communityStore() {
  return getStore({ name: "floorballdj-community", consistency: "strong" });
}

async function getJSON(store, key) {
  return store.get(key, { type: "json" });
}

async function listJSON(store, prefix) {
  const { blobs } = await store.list({ prefix });
  const values = await Promise.all(blobs.map(({ key }) => getJSON(store, key)));
  return values.filter(Boolean);
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

async function enforceRateLimit(store, fingerprint, action, limit, windowSeconds) {
  const prefix = `rate/${action}/${fingerprint}/`;
  const now = Date.now();
  const cutoff = now - windowSeconds * 1000;
  const events = await listJSON(store, prefix);
  const recent = events.filter((event) => Date.parse(event.createdAt) >= cutoff);
  const expired = events.filter((event) => Date.parse(event.createdAt) < now - 86_400_000);
  await Promise.all(expired.map((event) => store.delete(`${prefix}${event.id}.json`)));
  if (recent.length >= limit) return false;
  const id = `${now}-${randomUUID()}`;
  await store.setJSON(`${prefix}${id}.json`, { id, createdAt: new Date(now).toISOString() });
  return true;
}

async function findReply(store, id) {
  const replies = await listJSON(store, "replies/");
  return replies.find((reply) => reply.id === id) || null;
}

async function reportsFor(store, kind, id) {
  return listJSON(store, `reports/${kind}/${id}/`);
}

async function listThreads(store, limit, offset) {
  const threads = (await listJSON(store, "threads/"))
    .filter((thread) => thread.status === "visible")
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))
    .slice(offset, offset + limit);
  return Promise.all(threads.map(async (thread) => {
    const replies = (await listJSON(store, `replies/${thread.id}/`)).filter((reply) => reply.status === "visible");
    return {
      id: thread.id, nickname: thread.nickname, title: thread.title,
      bodyPreview: thread.body.slice(0, 260), locked: thread.locked,
      createdAt: thread.createdAt, updatedAt: thread.updatedAt, replyCount: replies.length
    };
  }));
}

async function getThread(store, id) {
  if (!validId.test(id)) return null;
  const thread = await getJSON(store, threadKey(id));
  if (!thread || thread.status !== "visible") return null;
  const replies = (await listJSON(store, `replies/${id}/`))
    .filter((reply) => reply.status === "visible")
    .sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt))
    .map(({ editTokenHash, authorFingerprint, status, threadId, ...reply }) => reply);
  const { editTokenHash, authorFingerprint, status, ...safeThread } = thread;
  return { ...safeThread, replies };
}

async function adminOverview(store) {
  const threads = (await listJSON(store, "threads/"))
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  let openReports = 0;
  const detailed = await Promise.all(threads.map(async (thread) => {
    const threadReports = (await reportsFor(store, "thread", thread.id)).filter((report) => !report.resolvedAt);
    const replies = (await listJSON(store, `replies/${thread.id}/`)).sort((a, b) => Date.parse(a.createdAt) - Date.parse(b.createdAt));
    const safeReplies = await Promise.all(replies.map(async (reply) => {
      const reports = (await reportsFor(store, "reply", reply.id)).filter((report) => !report.resolvedAt);
      openReports += reports.length;
      return { id: reply.id, nickname: reply.nickname, body: reply.body, status: reply.status, createdAt: reply.createdAt, reportCount: reports.length };
    }));
    openReports += threadReports.length;
    return {
      id: thread.id, nickname: thread.nickname, title: thread.title, body: thread.body,
      status: thread.status, locked: thread.locked, createdAt: thread.createdAt,
      reportCount: threadReports.length, replies: safeReplies
    };
  }));
  return { threads: detailed, openReports };
}

async function updateModeratedContent(store, kind, id, action) {
  if (!validId.test(id)) return false;
  const key = kind === "thread" ? threadKey(id) : null;
  const item = kind === "thread" ? await getJSON(store, key) : await findReply(store, id);
  if (!item) return false;
  if (action === "hide") item.status = "hidden";
  else if (action === "show") item.status = "visible";
  else if (action === "delete") { item.status = "deleted"; item.body = "[Raderat av moderator]"; }
  else if (kind === "thread" && action === "lock") item.locked = true;
  else if (kind === "thread" && action === "unlock") item.locked = false;
  else return false;
  item.updatedAt = new Date().toISOString();
  await store.setJSON(kind === "thread" ? key : replyKey(item.threadId, item.id), item);
  return true;
}

function mapError(error) {
  console.error("Community API error", error);
  return response(500, { error: "Communityt kunde inte behandla begäran." });
}

export default async (request) => {
  const config = settings();
  const store = communityStore();
  const url = new URL(request.url);
  const route = url.searchParams.get("route") || url.pathname.replace(/^\/api\/community\/?/, "");
  const parts = route.split("/").filter(Boolean);

  if (parts[0] === "health") {
    return response(200, { configured: Boolean(config.ipPepper && config.editPepper), postingEnabled: config.postingEnabled });
  }
  if (!config.ipPepper || !config.editPepper) {
    return response(503, { error: "Communityt håller på att konfigureras." });
  }

  try {
    if (parts[0] === "admin") {
      if (!config.adminKey) return response(503, { error: "Community-administrationen är inte konfigurerad." });
      if (!secureAdminMatch(request, config.adminKey)) return response(401, { error: "Ogiltig administratörsnyckel." });
      if (request.method === "GET" && parts.length === 1) return response(200, await adminOverview(store));
      if (request.method === "PATCH" && ["threads", "replies"].includes(parts[1]) && parts[2]) {
        const body = await request.json().catch(() => ({}));
        const allowed = parts[1] === "threads" ? ["hide", "show", "lock", "unlock", "delete"] : ["hide", "show", "delete"];
        if (!allowed.includes(body.action)) return response(400, { error: "Okänd åtgärd." });
        return response(200, { ok: await updateModeratedContent(store, parts[1] === "threads" ? "thread" : "reply", parts[2], body.action) });
      }
      return response(404, { error: "Administrationsfunktionen finns inte." });
    }

    if (request.method === "GET" && parts[0] === "threads" && !parts[1]) {
      const limit = Math.min(50, Math.max(1, Number(url.searchParams.get("limit")) || 25));
      const offset = Math.max(0, Number(url.searchParams.get("offset")) || 0);
      return response(200, await listThreads(store, limit, offset));
    }
    if (request.method === "GET" && parts[0] === "threads" && parts[1]) {
      const thread = await getThread(store, parts[1]);
      return thread ? response(200, thread) : response(404, { error: "Tråden finns inte längre." });
    }

    if (request.method === "POST" && parts[0] === "reports" && parts[1] && parts[2]) {
      const body = await request.json().catch(() => ({}));
      const reason = clean(body.reason);
      if (!["thread", "reply"].includes(parts[1]) || !validId.test(parts[2])) return response(404, { error: "Inlägget finns inte." });
      if (reason.length < 3 || reason.length > 500) return response(400, { error: "Beskriv problemet med 3–500 tecken." });
      const fingerprint = clientFingerprint(request, config.ipPepper);
      if (!await enforceRateLimit(store, fingerprint, "report", 6, 3600)) return response(429, { error: "Du har skickat flera rapporter på kort tid." });
      const exists = parts[1] === "thread" ? await getJSON(store, threadKey(parts[2])) : await findReply(store, parts[2]);
      if (!exists) return response(404, { error: "Inlägget finns inte." });
      const id = randomUUID();
      await store.setJSON(`reports/${parts[1]}/${parts[2]}/${id}.json`, { id, reason, reporterFingerprint: fingerprint, createdAt: new Date().toISOString(), resolvedAt: null });
      return response(201, { ok: true });
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
      const now = new Date().toISOString();
      if (kind === "reply") {
        if (!validId.test(parts[1])) return response(404, { error: "Tråden finns inte längre." });
        const thread = await getJSON(store, threadKey(parts[1]));
        if (!thread || thread.status !== "visible") return response(404, { error: "Tråden finns inte längre." });
        if (thread.locked) return response(409, { error: "Tråden är låst för nya svar." });
        if (!await enforceRateLimit(store, fingerprint, "reply", 12, 3600)) return response(429, { error: "Du har skickat flera svar på kort tid." });
        const id = randomUUID();
        await store.setJSON(replyKey(parts[1], id), {
          id, threadId: parts[1], nickname: clean(body.nickname), body: clean(body.body),
          editTokenHash: tokenHash, authorFingerprint: fingerprint, status: "visible", createdAt: now, updatedAt: now
        });
        return response(201, { id, createdAt: now, editToken: token });
      }
      if (parts[0] !== "threads" || parts[1]) return response(404, { error: "Funktionen finns inte." });
      if (!await enforceRateLimit(store, fingerprint, "thread", 4, 3600)) return response(429, { error: "Du har skapat flera trådar på kort tid." });
      const id = randomUUID();
      await store.setJSON(threadKey(id), {
        id, nickname: clean(body.nickname), title: clean(body.title), body: clean(body.body),
        editTokenHash: tokenHash, authorFingerprint: fingerprint, status: "visible", locked: false, createdAt: now, updatedAt: now
      });
      return response(201, { id, createdAt: now, editToken: token });
    }

    if (request.method === "DELETE" && ["threads", "replies"].includes(parts[0]) && parts[1]) {
      const token = request.headers.get("x-edit-token") || "";
      if (!token) return response(401, { error: "Redigeringsnyckeln saknas i den här webbläsaren." });
      const kind = parts[0] === "threads" ? "thread" : "reply";
      const item = kind === "thread" ? await getJSON(store, threadKey(parts[1])) : await findReply(store, parts[1]);
      if (!item || item.editTokenHash !== hash(`${config.editPepper}:${token}`) || item.status !== "visible") {
        return response(403, { error: "Inlägget kan inte raderas från den här webbläsaren." });
      }
      item.status = "deleted";
      item.body = "[Borttaget av författaren]";
      item.updatedAt = new Date().toISOString();
      await store.setJSON(kind === "thread" ? threadKey(item.id) : replyKey(item.threadId, item.id), item);
      return response(200, { ok: true });
    }

    return response(404, { error: "Community-funktionen finns inte." });
  } catch (error) {
    return mapError(error);
  }
};

export const config = { path: "/api/community/*" };
