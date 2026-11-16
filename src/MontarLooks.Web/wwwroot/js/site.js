// Vestiê · pequenas interações (sem dependências).
(() => {
    const reduce = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    // ---------- Toast (role=status). Sem JS o aviso fica no topo da página. ----------
    const hideToast = t => {
        t.classList.add("is-leaving");
        setTimeout(() => t.remove(), reduce ? 0 : 250);
    };
    const armToast = (t, ms = 5000) => {
        let timer = setTimeout(() => hideToast(t), ms);
        // Pausa enquanto a pessoa lê (mouse em cima ou foco no botão).
        t.addEventListener("mouseenter", () => clearTimeout(timer));
        t.addEventListener("focusin", () => clearTimeout(timer));
        t.addEventListener("mouseleave", () => { clearTimeout(timer); timer = setTimeout(() => hideToast(t), 2500); });
        t.querySelector("[data-toast-close]")?.addEventListener("click", () => hideToast(t));
    };
    let stack = document.querySelector(".toast-stack");
    const ensureStack = () => {
        if (!stack) { stack = document.createElement("div"); stack.className = "toast-stack"; document.body.appendChild(stack); }
        return stack;
    };
    document.querySelectorAll("[data-toast]").forEach(t => {
        // Mensagem vinda do servidor: vai para a pilha flutuante e é anunciada depois de entrar na região viva.
        const text = t.querySelector(".app-toast-text");
        const msg = text.textContent;
        text.textContent = "";
        ensureStack().appendChild(t);
        setTimeout(() => { text.textContent = msg; }, 60);
        armToast(t);
    });
    /** Mostra um aviso rápido. Uso: toast("Salvo!") ou toast("Ops", "warn"). */
    window.toast = (msg, kind = "ok") => {
        const t = document.createElement("div");
        t.className = "app-toast" + (kind === "warn" ? " app-toast-warn" : "");
        t.setAttribute("role", "status");
        t.setAttribute("aria-live", "polite");
        t.innerHTML = '<span class="app-toast-text"></span><button type="button" class="app-toast-close" aria-label="Fechar aviso" data-toast-close>×</button>';
        ensureStack().appendChild(t);
        setTimeout(() => { t.querySelector(".app-toast-text").textContent = msg; }, 60);
        armToast(t, 4000);
        return t;
    };

    // ---------- Entrada suave: só cartões que estão abaixo da dobra, quando chegam na tela ----------
    if (!reduce && "IntersectionObserver" in window) {
        const io = new IntersectionObserver(entries => entries.forEach(e => {
            if (!e.isIntersecting) return;
            e.target.classList.add("is-in");
            io.unobserve(e.target);
        }), { rootMargin: "0px 0px -8% 0px" });
        const fold = window.innerHeight;
        let n = 0;
        document.querySelectorAll("main .card, .wardrobe-card").forEach(el => {
            if (el.getBoundingClientRect().top < fold) return; // o que já está à vista não pisca
            el.classList.add("reveal");
            el.style.setProperty("--i", n++ % 4);
            io.observe(el);
        });
    }

    // ---------- Pétalas para comemorar (escolher um look, curtir...) ----------
    window.petals = (n = 18) => {
        if (reduce) return;
        for (let k = 0; k < n; k++) {
            const p = document.createElement("span");
            p.className = "petal";
            p.setAttribute("aria-hidden", "true");
            p.style.left = Math.random() * 100 + "vw";
            p.style.setProperty("--drift", (Math.random() * 160 - 80) + "px");
            p.style.animationDuration = 1.6 + Math.random() * 1.6 + "s";
            p.style.width = p.style.height = 10 + Math.random() * 12 + "px";
            document.body.appendChild(p);
            setTimeout(() => p.remove(), 3500);
        }
    };

    // Formulários com data-celebrate: pétalas primeiro, depois envia.
    document.querySelectorAll("form[data-celebrate]").forEach(f => f.addEventListener("submit", e => {
        if (reduce || f.dataset.sent) return;
        e.preventDefault(); f.dataset.sent = "1"; petals(26);
        f.querySelectorAll("button").forEach(b => b.disabled = true);
        setTimeout(() => f.submit(), 650);
    }));

    // Guarda-roupa: busca instantânea enquanto digita.
    const q = document.querySelector("[data-instant-filter]");
    if (q) q.addEventListener("input", () => {
        const term = q.value.trim().toLowerCase();
        document.querySelectorAll(".wardrobe-card").forEach(c => {
            const box = c.closest("form") ?? c;
            box.style.display = !term || c.textContent.toLowerCase().includes(term) ? "" : "none";
        });
    });

    // Cores do avatar atualizam a prévia ao vivo.
    document.querySelectorAll("[data-live]").forEach(inp => inp.addEventListener("input", () => {
        document.querySelectorAll(".mannequin").forEach(m => m.style.setProperty("--" + inp.dataset.live, inp.value));
    }));

    // ---------- Navegação por seções (Configurações): destaca a seção visível ----------
    document.querySelectorAll("[data-section-nav]").forEach(nav => {
        const links = [...nav.querySelectorAll('a[href^="#"]')];
        const map = new Map(links.map(a => [a.getAttribute("href").slice(1), a]));
        const setActive = id => links.forEach(a => {
            const on = a === map.get(id);
            a.classList.toggle("active", on);
            if (on) a.setAttribute("aria-current", "true"); else a.removeAttribute("aria-current");
        });
        if (!("IntersectionObserver" in window)) return;
        const io = new IntersectionObserver(entries => {
            const vis = entries.filter(e => e.isIntersecting).sort((a, b) => a.boundingClientRect.top - b.boundingClientRect.top)[0];
            if (vis) {
                setActive(vis.target.id);
                // no celular a barra rola de lado: mantém a aba ativa à vista
                const a = map.get(vis.target.id);
                if (a && nav.scrollWidth > nav.clientWidth) nav.scrollTo({ left: a.offsetLeft - 16, behavior: reduce ? "auto" : "smooth" });
            }
        }, { rootMargin: "-30% 0px -60% 0px" });
        map.forEach((_, id) => { const s = document.getElementById(id); if (s) io.observe(s); });
        if (location.hash && map.has(location.hash.slice(1))) setActive(location.hash.slice(1));
    });
})();
