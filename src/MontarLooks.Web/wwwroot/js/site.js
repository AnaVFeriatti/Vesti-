// MontarLooks · pequenas interações (sem dependências).
(() => {
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    // Stagger: cards appear one after another.
    document.querySelectorAll(".look-card, .wardrobe-card").forEach((el, i) => el.style.setProperty("--i", Math.min(i, 12)));

    // Petal burst, used to celebrate (choosing a look, liking...).
    window.petals = (n = 18) => {
        if (reduce) return;
        for (let k = 0; k < n; k++) {
            const p = document.createElement("span");
            p.className = "petal";
            p.style.left = Math.random() * 100 + "vw";
            p.style.setProperty("--drift", (Math.random() * 160 - 80) + "px");
            p.style.animationDuration = 1.6 + Math.random() * 1.6 + "s";
            p.style.width = p.style.height = 10 + Math.random() * 12 + "px";
            document.body.appendChild(p);
            setTimeout(() => p.remove(), 3500);
        }
    };

    // Forms marked data-celebrate: petals first, then submit.
    document.querySelectorAll("form[data-celebrate]").forEach(f => f.addEventListener("submit", e => {
        if (reduce || f.dataset.sent) return;
        e.preventDefault(); f.dataset.sent = "1"; petals(26);
        setTimeout(() => f.submit(), 650);
    }));

    // Wardrobe: instant search while typing.
    const q = document.querySelector("[data-instant-filter]");
    if (q) q.addEventListener("input", () => {
        const term = q.value.trim().toLowerCase();
        document.querySelectorAll(".wardrobe-card").forEach(c => {
            const box = c.closest("form") ?? c;
            box.style.display = !term || c.textContent.toLowerCase().includes(term) ? "" : "none";
        });
    });

    // Avatar color pickers update the preview live.
    document.querySelectorAll("[data-live]").forEach(inp => inp.addEventListener("input", () => {
        document.querySelectorAll(".mannequin").forEach(m => m.style.setProperty("--" + inp.dataset.live, inp.value));
    }));
})();
