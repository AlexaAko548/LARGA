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

    // While something on the page is marked aria-busy="true", every mutation in this batch is
    // that action's own "I'm working" rendering (the busy element's label/spinner, AND anything
    // else wired to the same flag - e.g. a confirmation dialog's Cancel/Close buttons going
    // disabled alongside its Confirm button, which aren't themselves aria-busy and so aren't
    // inside one either) - not the server's answer. By the time this callback runs the DOM is
    // already in its post-mutation state, so once the busy element's flag actually clears (or it
    // gets removed entirely, e.g. a confirmation modal closing), this stops ignoring updates.
    // Blazor renders aria-busy="@true" as aria-busy="" (bool true -> empty value), so match
    // "present and not false" - the same selector app.css uses for the button spinner.
    function anyElementBusy() {
        return !!document.querySelector('[aria-busy]:not([aria-busy="false"])');
    }

    // Any page update other than our own bar, while nothing is still (locally) busy, is the
    // server's answer.
    const observer = new MutationObserver(mutations => {
        if (!waiting) return;
        if (anyElementBusy()) return;
        if (mutations.some(m => m.target !== bar && !(m.target instanceof Node && bar && bar.contains(m.target)))) stop();
    });
    function observe() { observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'disabled', 'aria-busy', 'value'] }); }
    if (document.body) observe(); else document.addEventListener('DOMContentLoaded', observe);
})();
