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
  async function act(id, action) {
    await api(`/threads/${id}`, { method: "PATCH", body: JSON.stringify({ action }) });
    await load();
  }
  async function load() {
    try {
      const data = await api();
      login.hidden = true; panel.hidden = false; list.replaceChildren(); status.textContent = `${data.threads.length} trådar · ${data.openReports} öppna rapporter`;
      data.threads.forEach((thread) => {
        const card = node("article", "admin-thread-card");
        const copy = node("div"); copy.append(node("strong", "", thread.title), node("span", "", `${thread.nickname} · ${thread.status} · ${thread.reportCount} rapporter`), node("p", "", thread.body));
        const actions = node("div", "admin-thread-actions");
        [[thread.status === "hidden" ? "Visa" : "Dölj", thread.status === "hidden" ? "show" : "hide"], [thread.locked ? "Lås upp" : "Lås", thread.locked ? "unlock" : "lock"], ["Radera", "delete"]].forEach(([label, action]) => {
          const button = node("button", action === "delete" ? "danger" : "", label); button.type = "button"; button.addEventListener("click", () => act(thread.id, action).catch((error) => status.textContent = error.message)); actions.append(button);
        });
        card.append(copy, actions); list.append(card);
      });
    } catch (error) { status.textContent = error.message; login.hidden = false; panel.hidden = true; }
  }
  login.addEventListener("submit", (event) => { event.preventDefault(); key = login.elements.key.value.trim(); sessionStorage.setItem("floorballdj-community-admin-key", key); load(); });
  if (key) load();
})();
