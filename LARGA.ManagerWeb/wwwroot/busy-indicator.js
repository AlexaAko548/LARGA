// LAR-117: a thin progress bar along the top of the page whenever the server is taking a moment
// to answer something the manager did (a click or a changed field) - every Firestore call goes
// through the server in Blazor Server, so this covers all of them, including ones without their
// own "Loading…" placeholder. The answer arrives as a change to the page; until one does (after a
// short grace period, so quick actions never flash it), the bar shows. Capped so a click that
// changes nothing can't leave it running.
(function () {
    const GRACE_MS = 250;
    const MAX_MS = 15000;
    let bar = null;
    let showTimer = null;
    let hideTimer = null;
    let waiting = false;

    function ensureBar() {
        if (bar && document.body.contains(bar)) return bar;
        bar = document.createElement('div');
        bar.id = 'larga-busy-bar';
        bar.setAttribute('role', 'progressbar');
        bar.setAttribute('aria-label', 'Loading');
        bar.hidden = true;
        document.body.appendChild(bar);
        return bar;
    }

    function stop() {
        waiting = false;
        clearTimeout(showTimer);
        clearTimeout(hideTimer);
        if (bar) bar.hidden = true;
    }

    function start() {
        stop();
        waiting = true;
        showTimer = setTimeout(() => { if (waiting) ensureBar().hidden = false; }, GRACE_MS);
        hideTimer = setTimeout(stop, MAX_MS);
    }

    function isInteractive(target) {
        const el = target instanceof Element ? target.closest('button, a[href], [role="button"], input, select, label, summary') : null;
        if (!el) return false;
        if (el.matches(':disabled, [aria-disabled="true"]')) return false;
        // Typing in a text box doesn't wait on the server; its change/submit does.
        if (el.matches('input[type="text"], input[type="search"], input[type="email"], input[type="password"], input[type="number"], textarea')) return false;
        return true;
    }

    document.addEventListener('click', e => { if (isInteractive(e.target)) start(); }, true);
    document.addEventListener('change', e => { if (e.target instanceof Element && e.target.matches('select, input[type="date"], input[type="checkbox"], input[type="radio"], input[type="file"]')) start(); }, true);

    // Any page update other than our own bar is the server's answer.
    const observer = new MutationObserver(mutations => {
        if (!waiting) return;
        if (mutations.some(m => m.target !== bar && !(m.target instanceof Node && bar && bar.contains(m.target)))) stop();
    });
    function observe() { observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'disabled', 'aria-busy', 'value'] }); }
    if (document.body) observe(); else document.addEventListener('DOMContentLoaded', observe);
})();
