// Vestiê · acompanha um lote de fotos processado em segundo plano.
(() => {
    const box = document.querySelector("[data-photo-batch]");
    if (!box) return;

    const bar = box.querySelector("[data-batch-bar]");
    const fill = box.querySelector("[data-batch-fill]");
    const count = box.querySelector("[data-batch-count]");
    const status = box.querySelector("[data-batch-status]");
    const next = box.querySelector("[data-batch-next]");
    const INTERVAL = 1000;
    const MAX_ERRORS = 30;
    let errors = 0;

    const render = s => {
        fill.style.width = s.percent + "%";
        bar.setAttribute("aria-valuenow", s.percent);
        count.textContent = `${s.processed} de ${s.total} foto(s)`;
        if (s.state === "running") status.textContent = "Cuidando das fotos...";
        if (!s.finished) return;
        status.textContent = s.message;
        box.classList.toggle("is-failed", s.state === "failed");
        if (s.nextUrl) {
            next.href = s.nextUrl;
            if (s.nextUrl.includes("/Edit")) next.textContent = next.dataset.reviewLabel;
            next.hidden = false;
        }
    };

    const poll = async () => {
        try {
            const res = await fetch(box.dataset.statusUrl, { headers: { Accept: "application/json" }, cache: "no-store" });
            if (res.status === 404) {
                status.textContent = "Não encontrei esse lote. O app pode ter reiniciado; confira no Meu Closet o que já foi salvo.";
                return;
            }
            if (!res.ok) throw new Error(String(res.status));
            const s = await res.json();
            errors = 0;
            render(s);
            if (!s.finished) setTimeout(poll, INTERVAL);
        } catch {
            if (++errors >= MAX_ERRORS) {
                status.textContent = "Perdi a conexão com o app. O lote continua; atualize a página daqui a pouco.";
                return;
            }
            setTimeout(poll, INTERVAL);
        }
    };

    poll();
})();
