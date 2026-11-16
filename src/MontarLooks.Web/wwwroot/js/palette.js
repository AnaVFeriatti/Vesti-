// MontarLooks · Paleta pessoal: teste do tecido no rosto + ajuda da foto (tudo local, sem dependências).
(() => {
    const drape = document.querySelector("[data-drape]");
    if (!drape) return;

    const GOLD = "linear-gradient(135deg, #fbe7a1, #c9962b 35%, #f6d365 60%, #a8771c)";
    const SILVER = "linear-gradient(135deg, #f7f8fa, #a7adb4 35%, #e3e6ea 60%, #7d848c)";
    let season = null;
    try { season = JSON.parse(drape.dataset.season || "null"); } catch { season = null; }

    // Each mode is a list of pairs [left, right]; "Trocar" walks through the pairs.
    const modes = {
        metal: [[{ c: GOLD, n: "Dourado" }, { c: SILVER, n: "Prateado" }],
                [{ c: "linear-gradient(135deg, #f3c9a8, #b76e4c 50%, #e8b08c)", n: "Ouro rosé" }, { c: "linear-gradient(135deg, #fff, #c9ced4 50%, #8e959c)", n: "Platina" }]],
        temp: [[{ c: "#e07b24", n: "Laranja (quente)" }, { c: "#c2185b", n: "Fúcsia (frio)" }],
               [{ c: "#d4a017", n: "Mostarda (quente)" }, { c: "#1f4fd1", n: "Azul-royal (frio)" }],
               [{ c: "#c19a6b", n: "Camelo (quente)" }, { c: "#8d99a6", n: "Cinza-azulado (frio)" }],
               [{ c: "#f3e5c8", n: "Creme (quente)" }, { c: "#ffffff", n: "Branco puro (frio)" }]],
        chroma: [[{ c: "#ff1493", n: "Pink vivo" }, { c: "#c99aa5", n: "Rosa antigo" }],
                 [{ c: "#0066ff", n: "Azul elétrico" }, { c: "#7f95ad", n: "Azul acinzentado" }],
                 [{ c: "#00a86b", n: "Esmeralda" }, { c: "#9caa83", n: "Sálvia" }]],
        value: [[{ c: "#f6a5c0", n: "Rosa claro" }, { c: "#4b1f45", n: "Berinjela" }],
                [{ c: "#a7c7e7", n: "Azul-bebê" }, { c: "#1d2b4f", n: "Azul-marinho" }],
                [{ c: "#fbf3e4", n: "Marfim" }, { c: "#111111", n: "Preto" }]],
    };
    if (season && season.best && season.avoid) {
        const n = Math.min(season.best.length, season.avoid.length);
        modes.season = Array.from({ length: n }, (_, i) => [
            { c: season.best[i], n: "Da sua paleta" },
            { c: season.avoid[i], n: "Para evitar" }]);
    }

    const ring = k => drape.querySelector(`[data-ring="${k}"]`);
    const cap = k => drape.querySelector(`[data-caption="${k}"]`);
    let mode = "metal", idx = 0;

    function paint(side, o) {
        ring(side).style.background = o.c;
        cap(side).textContent = o.n;
    }
    function show() {
        const pairs = modes[mode];
        const [a, b] = pairs[idx % pairs.length];
        paint("a", a); paint("b", b);
    }
    drape.querySelectorAll("[data-mode]").forEach(btn => btn.addEventListener("click", () => {
        mode = btn.dataset.mode; idx = 0;
        drape.querySelectorAll("[data-mode]").forEach(x => x.setAttribute("aria-pressed", String(x === btn)));
        show();
    }));
    drape.querySelector("[data-next]")?.addEventListener("click", () => { idx++; show(); });
    drape.querySelectorAll("[data-hex]").forEach(btn => btn.addEventListener("click", () =>
        paint("a", { c: btn.dataset.hex, n: btn.dataset.name })));
    show();

    function useFace(url) {
        drape.querySelectorAll(".pal-face").forEach(img => { img.src = url; });
        drape.querySelector(".pal-drape").hidden = false;
        drape.querySelector(".pal-drape-empty").hidden = true;
    }

    // ---------- photo assist ----------
    const form = document.getElementById("pal-analyze");
    const input = document.getElementById("pal-photo");
    const out = document.getElementById("pal-photo-result");
    if (!form || !input || !out) return;

    const esc = s => String(s).replace(/[&<>"']/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));

    function prefill(question, value) {
        const radio = document.querySelector(`input[name="q_${question}"][value="${CSS.escape(value)}"]`);
        if (!radio) return;
        radio.checked = true;
        const badge = radio.closest("fieldset")?.querySelector(".pal-photo-badge");
        if (badge) badge.hidden = false;
    }

    input.addEventListener("change", async () => {
        const file = input.files && input.files[0];
        if (!file) return;
        useFace(URL.createObjectURL(file));
        out.innerHTML = '<span class="small text-muted">Analisando a foto…</span>';
        const data = new FormData(form);
        try {
            const res = await fetch(form.action, { method: "POST", body: data, headers: { "Accept": "application/json" } });
            const json = await res.json();
            if (!json.ok) { out.innerHTML = `<span class="small text-danger">${esc(json.message)}</span>`; return; }
            const r = json.result;
            prefill("fundo", r.skinBase);
            prefill("pele", r.skinDepth);
            out.innerHTML = `<div class="d-flex align-items-center gap-2">
                <span class="pal-skin-dot" style="background:${esc(r.hex)}" aria-hidden="true"></span>
                <div class="small"><strong>Sugestão:</strong> subtom <strong>${esc(r.undertoneLabel)}</strong>, pele <strong>${esc(r.depthLabel)}</strong>.
                Já marquei no teste, confira!
                <span class="text-muted d-block">Lab ${r.l} / ${r.a} / ${r.b} · HSV ${r.hsvH}°, ${r.hsvS}%, ${r.hsvV}%</span></div></div>`;
        } catch {
            out.innerHTML = '<span class="small text-danger">Não foi possível analisar a foto agora.</span>';
        }
    });
})();
