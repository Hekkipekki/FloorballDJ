(() => {
  const config = window.FLOORBALLDJ_SITE_CONFIG ?? {};
  const header = document.querySelector("[data-header]");
  const nav = document.querySelector("[data-nav]");
  const navToggle = document.querySelector("[data-nav-toggle]");

  const closeNav = () => {
    nav?.classList.remove("is-open");
    navToggle?.setAttribute("aria-expanded", "false");
  };

  navToggle?.addEventListener("click", () => {
    const open = nav?.classList.toggle("is-open") ?? false;
    navToggle.setAttribute("aria-expanded", String(open));
  });
  nav?.querySelectorAll("a").forEach((link) => link.addEventListener("click", closeNav));

  window.addEventListener("scroll", () => {
    header?.classList.toggle("is-scrolled", window.scrollY > 24 || header.hasAttribute("data-solid-header"));
  }, { passive: true });

  document.querySelectorAll("[data-year]").forEach((node) => {
    node.textContent = String(new Date().getFullYear());
  });
  document.querySelectorAll("[data-version]").forEach((node) => {
    node.textContent = config.currentVersion || "Beta";
  });

  const purchase = document.querySelector("[data-purchase-cta]");
  const price = document.querySelector("[data-price-label]");
  if (price && config.priceLabel) price.textContent = config.priceLabel;
  if (purchase && config.purchasesEnabled && config.checkoutUrl) {
    purchase.href = config.checkoutUrl;
    purchase.textContent = "Köp FloorballDJ";
    purchase.removeAttribute("aria-disabled");
  } else {
    purchase?.addEventListener("click", (event) => event.preventDefault());
  }

  const downloadButton = document.querySelector("[data-download-button]");
  const downloadHero = document.querySelector("[data-download-cta]");
  const downloadNote = document.querySelector("[data-download-note]");
  if (config.downloadsEnabled && config.downloadUrl) {
    if (downloadButton) {
      downloadButton.href = config.downloadUrl;
      downloadButton.textContent = `Ladda ned ${config.currentVersion || "FloorballDJ"}`;
      downloadButton.removeAttribute("aria-disabled");
    }
    if (downloadHero) downloadHero.href = config.downloadUrl;
    if (downloadNote) downloadNote.textContent = "Provperioden startar automatiskt första gången programmet öppnas. Betaversionen är ännu inte kodsignerad, så Windows kan visa en SmartScreen-varning.";
  } else {
    downloadButton?.addEventListener("click", (event) => event.preventDefault());
  }

  const observer = new IntersectionObserver((entries) => {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      }
    });
  }, { threshold: 0.12 });
  document.querySelectorAll(".reveal").forEach((node) => observer.observe(node));
})();
