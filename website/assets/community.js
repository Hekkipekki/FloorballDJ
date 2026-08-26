(() => {
  const root = document.querySelector("[data-community]");
  if (!root) return;
  const english = document.documentElement.lang.startsWith("en");
  const words = english ? {
    loading: "Loading discussions…", unavailable: "The community is being prepared. Reading and posting will open soon.",
    empty: "No topics yet. Be the first to start one.", replies: "replies", locked: "Locked", by: "By",
    emptyTitle: "Start the first conversation",
    emptyBody: "Ask a question, share a match-day workflow or suggest an improvement to FloorballDJ.",
    emptyAction: "Create the first topic",
    reply: "Reply", report: "Report", remove: "Delete my post", replyPlaceholder: "Write a reply…",
    postReply: "Post reply", nickname: "Nickname", confirmDelete: "Delete this post?", reportPrompt: "Briefly describe the problem:",
    saved: "Published. The edit key is saved in this browser.", error: "Something went wrong. Please try again."
  } : {
    loading: "Laddar diskussioner…", unavailable: "Communityt förbereds. Läsning och nya inlägg öppnas snart.",
    empty: "Det finns inga trådar ännu. Bli först med att starta en.", replies: "svar", locked: "Låst", by: "Av",
    emptyTitle: "Starta den första diskussionen",
    emptyBody: "Ställ en fråga, dela ett arbetssätt från matchdagen eller föreslå en förbättring av FloorballDJ.",
    emptyAction: "Skapa första tråden",
    reply: "Svara", report: "Rapportera", remove: "Radera mitt inlägg", replyPlaceholder: "Skriv ett svar…",
    postReply: "Publicera svar", nickname: "Smeknamn", confirmDelete: "Radera det här inlägget?", reportPrompt: "Beskriv kort vad som är fel:",
    saved: "Publicerat. Redigeringsnyckeln sparades i den här webbläsaren.", error: "Något gick fel. Försök igen."
  };
  const status = root.querySelector("[data-community-status]");
  const list = root.querySelector("[data-thread-list]");
  const detail = root.querySelector("[data-thread-detail]");
  const dialog = root.querySelector("[data-thread-dialog]");
  const form = root.querySelector("[data-thread-form]");
  const tokens = JSON.parse(localStorage.getItem("floorballdj-community-edit-tokens") || "{}");
  let postingEnabled = false;

  const el = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };
  const date = (value) => new Intl.DateTimeFormat(english ? "en-GB" : "sv-SE", { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const api = async (path, options = {}) => {
    const response = await fetch(`/api/community${path}`, { ...options, headers: { "content-type": "application/json", ...(options.headers || {}) } });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload.error || words.error);
    return payload;
  };
  const saveToken = (kind, id, token) => {
    tokens[`${kind}:${id}`] = token;
    localStorage.setItem("floorballdj-community-edit-tokens", JSON.stringify(tokens));
  };
  const setStatus = (message, kind = "") => {
    status.textContent = message;
    status.dataset.kind = kind;
    status.hidden = !message;
  };

  function showEmptyCommunity() {
    list.replaceChildren(el("div", "community-empty community-empty-list", words.empty));
    const emptyState = el("div", "community-empty community-empty-detail");
    emptyState.append(el("strong", "", words.emptyTitle), el("span", "", words.emptyBody));
    if (postingEnabled) {
      const start = el("button", "button button-primary", words.emptyAction);
      start.type = "button";
      start.addEventListener("click", () => { dialog.showModal(); form.elements.nickname.focus(); });
      emptyState.append(start);
    }
    detail.replaceChildren(emptyState);
  }

  async function loadThreads(selectId) {
    setStatus(words.loading);
    try {
      const threads = await api("/threads");
      list.replaceChildren();
      if (!threads.length) showEmptyCommunity();
      threads.forEach((thread) => {
        const button = el("button", "thread-card");
        button.type = "button";
        button.dataset.threadId = thread.id;
        button.append(el("span", "thread-title", thread.title));
        button.append(el("span", "thread-preview", thread.bodyPreview));
        const meta = el("span", "thread-meta");
        meta.append(el("b", "", thread.nickname), document.createTextNode(` · ${date(thread.createdAt)} · ${thread.replyCount} ${words.replies}`));
        if (thread.locked) meta.append(el("i", "thread-lock", words.locked));
        button.append(meta);
        button.addEventListener("click", () => showThread(thread.id));
        list.append(button);
      });
      setStatus("");
      if (selectId) await showThread(selectId);
    } catch (error) {
      setStatus(error.message || words.unavailable, "error");
    }
  }

  const actionButton = (label, handler, danger = false) => {
    const button = el("button", `text-action${danger ? " danger" : ""}`, label);
    button.type = "button";
    button.addEventListener("click", handler);
    return button;
  };

  async function removeOwn(kind, id, threadId) {
    if (!confirm(words.confirmDelete)) return;
    try {
      await api(`/${kind === "thread" ? "threads" : "replies"}/${id}`, { method: "DELETE", headers: { "x-edit-token": tokens[`${kind}:${id}`] || "" } });
      delete tokens[`${kind}:${id}`];
      localStorage.setItem("floorballdj-community-edit-tokens", JSON.stringify(tokens));
      await loadThreads(kind === "reply" ? threadId : null);
      if (kind === "thread") detail.replaceChildren(el("div", "community-empty", words.empty));
    } catch (error) { setStatus(error.message, "error"); }
  }

  async function report(kind, id) {
    const reason = prompt(words.reportPrompt);
    if (!reason) return;
    try { await api(`/reports/${kind}/${id}`, { method: "POST", body: JSON.stringify({ reason }) }); setStatus(english ? "Report sent." : "Rapporten skickades.", "success"); }
    catch (error) { setStatus(error.message, "error"); }
  }

  async function showThread(id) {
    try {
      const thread = await api(`/threads/${id}`);
      list.querySelectorAll(".thread-card").forEach((node) => node.classList.toggle("is-active", node.dataset.threadId === id));
      detail.replaceChildren();
      const head = el("header", "discussion-head");
      head.append(el("p", "discussion-meta", `${words.by} ${thread.nickname} · ${date(thread.createdAt)}`), el("h2", "", thread.title));
      const actions = el("div", "discussion-actions");
      actions.append(actionButton(words.report, () => report("thread", thread.id)));
      if (tokens[`thread:${thread.id}`]) actions.append(actionButton(words.remove, () => removeOwn("thread", thread.id), true));
      head.append(actions); detail.append(head, el("p", "discussion-body", thread.body));
      const replies = el("section", "reply-list");
      thread.replies.forEach((reply) => {
        const item = el("article", "reply-card");
        const replyHead = el("div", "reply-head");
        replyHead.append(el("strong", "", reply.nickname), el("time", "", date(reply.createdAt)));
        const replyActions = el("span", "reply-actions");
        replyActions.append(actionButton(words.report, () => report("reply", reply.id)));
        if (tokens[`reply:${reply.id}`]) replyActions.append(actionButton(words.remove, () => removeOwn("reply", reply.id, thread.id), true));
        replyHead.append(replyActions); item.append(replyHead, el("p", "", reply.body)); replies.append(item);
      });
      detail.append(replies);
      if (!thread.locked && postingEnabled) detail.append(buildReplyForm(thread.id));
      else if (thread.locked) detail.append(el("div", "community-notice", english ? "This topic is locked." : "Tråden är låst för nya svar."));
    } catch (error) { setStatus(error.message, "error"); }
  }

  function buildReplyForm(threadId) {
    const replyForm = el("form", "reply-form");
    const nickname = el("input"); nickname.name = "nickname"; nickname.placeholder = words.nickname; nickname.required = true; nickname.minLength = 2; nickname.maxLength = 40;
    const body = el("textarea"); body.name = "body"; body.placeholder = words.replyPlaceholder; body.required = true; body.maxLength = 3000; body.rows = 4;
    const submit = el("button", "button button-primary", words.postReply); submit.type = "submit";
    replyForm.append(nickname, body, submit);
    replyForm.addEventListener("submit", async (event) => {
      event.preventDefault(); submit.disabled = true;
      try {
        const created = await api(`/threads/${threadId}/replies`, { method: "POST", body: JSON.stringify({ nickname: nickname.value, body: body.value }) });
        saveToken("reply", created.id, created.editToken); await loadThreads(threadId); setStatus(words.saved, "success");
      } catch (error) { setStatus(error.message, "error"); } finally { submit.disabled = false; }
    });
    return replyForm;
  }

  root.querySelector("[data-new-thread]").addEventListener("click", () => {
    if (!postingEnabled) { setStatus(words.unavailable, "error"); return; }
    dialog.showModal(); form.elements.nickname.focus();
  });
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const errorNode = form.querySelector("[data-form-error]"); errorNode.textContent = "";
    const submit = form.querySelector('[type="submit"]'); submit.disabled = true;
    const data = Object.fromEntries(new FormData(form));
    try {
      const created = await api("/threads", { method: "POST", body: JSON.stringify(data) });
      saveToken("thread", created.id, created.editToken); dialog.close(); form.reset(); await loadThreads(created.id); setStatus(words.saved, "success");
    } catch (error) { errorNode.textContent = error.message; } finally { submit.disabled = false; }
  });

  (async () => {
    try {
      const health = await api("/health"); postingEnabled = Boolean(health.postingEnabled);
      if (!health.configured) { setStatus(words.unavailable, "error"); return; }
      await loadThreads();
      if (!postingEnabled) setStatus(english ? "Reading is open. New posts will open soon." : "Läsning är öppen. Nya inlägg öppnas snart.");
    } catch { setStatus(words.unavailable, "error"); }
  })();
})();
