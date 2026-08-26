(() => {
  const root = document.querySelector("[data-community-admin]");
  if (!root) return;
  const login = root.querySelector("[data-admin-login]");
  const panel = root.querySelector("[data-admin-panel]");
  const list = root.querySelector("[data-admin-threads]");
  const status = root.querySelector("[data-admin-status]");
  let key = sessionStorage.getItem("floorballdj-community-admin-key") || "";
  const api = async (path = "", options = {}) => {
    const response = await fetch(`/api/community/admin${path}`, { ...options, headers: { authorization: `Bearer ${key}`, "content-type": "application/json" } });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload.error || "Begäran misslyckades.");
    return payload;
  };
  const node = (tag, className, text) => { const el = document.createElement(tag); el.className = className || ""; if (text !== undefined) el.textContent = text; return el; };
  async function act(kind, id, action) {
    await api(`/${kind}/${id}`, { method: "PATCH", body: JSON.stringify({ action }) });
    await load();
  }
  const actionButtons = (kind, item, allowLock = false) => {
    const actions = node("div", "admin-thread-actions");
    const choices = [[item.status === "hidden" ? "Visa" : "Dölj", item.status === "hidden" ? "show" : "hide"]];
    if (allowLock) choices.push([item.locked ? "Lås upp" : "Lås", item.locked ? "unlock" : "lock"]);
    choices.push(["Radera", "delete"]);
    choices.forEach(([label, action]) => {
      const button = node("button", action === "delete" ? "danger" : "", label);
      button.type = "button";
      button.addEventListener("click", () => act(kind, item.id, action).catch((error) => status.textContent = error.message));
      actions.append(button);
    });
    return actions;
  };
  async function load() {
    try {
      const data = await api();
      login.hidden = true; panel.hidden = false; list.replaceChildren(); status.textContent = `${data.threads.length} trådar · ${data.openReports} öppna rapporter`;
      data.threads.forEach((thread) => {
        const card = node("article", "admin-thread-card");
        const copy = node("div");
        const threadMeta = node("span", thread.reportCount ? "admin-report-badge" : "", `${thread.nickname} · ${thread.status} · ${thread.reportCount} rapporter`);
        copy.append(node("strong", "", thread.title), threadMeta, node("p", "", thread.body));
        if (thread.replies?.length) {
          const replies = node("div", "admin-reply-list");
          thread.replies.forEach((reply) => {
            const replyCard = node("article", "admin-reply-card");
            const replyCopy = node("div");
            replyCopy.append(node("strong", "", `${reply.nickname} · ${reply.status}${reply.reportCount ? ` · ${reply.reportCount} rapporter` : ""}`), node("p", "", reply.body));
            replyCard.append(replyCopy, actionButtons("replies", reply));
            replies.append(replyCard);
          });
          copy.append(replies);
        }
        card.append(copy, actionButtons("threads", thread, true)); list.append(card);
      });
    } catch (error) { status.textContent = error.message; login.hidden = false; panel.hidden = true; }
  }
  login.addEventListener("submit", (event) => { event.preventDefault(); key = login.elements.key.value.trim(); sessionStorage.setItem("floorballdj-community-admin-key", key); load(); });
  if (key) load();
})();
