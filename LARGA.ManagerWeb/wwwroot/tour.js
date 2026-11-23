// Page tours (spotlight walkthrough). A thin wrapper around driver.js (wwwroot/lib/driver.js,
// pinned, MIT - see its README). Pages never call this: TourService.cs does, through IJSRuntime.
//
// Steps arrive from C# as plain data: { target, title, body, faqHref }.
//  - target:  the page's data-tour="..." name, or null for a centred note (no element to point at).
//  - faqHref: set on the last step; renders the "Open Help & FAQs" link.
// A step whose element isn't on the page (empty table, hidden button) is skipped, so the tour
// never points at nothing.
//
// "Seen" flags live in localStorage; every read and write is wrapped in try/catch because the
// browser can refuse storage (private window, blocked site data).

const SEEN_PREFIX = 'larga-tour:';
const MODALS = '.report-overlay, .modal-overlay, .profile-overlay, .chat-drawer-backdrop';
const SAFE_NAME = /^[a-z0-9-]+$/i;

let active = null;

function esc(text) {
    const d = document.createElement('div');
    d.textContent = text == null ? '' : String(text);
    return d.innerHTML;
}

function isVisible(el) {
    if (!el || !el.isConnected) return false;
    const rect = el.getBoundingClientRect();
    if (rect.width === 0 && rect.height === 0) return false;
    return getComputedStyle(el).visibility !== 'hidden';
}

function find(target) {
    if (!target || !SAFE_NAME.test(target)) return null;
    const el = document.querySelector('[data-tour="' + target + '"]');
    return isVisible(el) ? el : null;
}

function foundCount(steps) {
    return steps.filter(s => s.target && find(s.target)).length;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

// A page that has just opened is still loading its tables. Wait until the number of findable
// elements stops changing (about a second), up to maxMs, then use whatever is there.
async function waitForPage(steps, maxMs) {
    const started = Date.now();
    let last = -1;
    let stable = 0;
    while (Date.now() - started < maxMs) {
        const n = foundCount(steps);
        stable = n === last ? stable + 1 : 0;
        last = n;
        if (n > 0 && stable >= 3) return;
        await sleep(300);
    }
}

export function hasSeen(key) {
    try {
        return localStorage.getItem(SEEN_PREFIX + key) === '1';
    } catch (e) {
        // Can't remember anything, so don't nag: treat the tour as already seen.
        return true;
    }
}

export function markSeen(key) {
    try {
        localStorage.setItem(SEEN_PREFIX + key, '1');
    } catch (e) { }
}

export function stopTour() {
    if (active) {
        try { active.destroy(); } catch (e) { }
        active = null;
    }
}

// Returns true when a tour actually started (at least one real element was found).
export async function startTour(steps, options) {
    const opts = options || {};
    const factory = window.driver && window.driver.js && window.driver.js.driver;
    if (!factory || !Array.isArray(steps) || steps.length === 0) return false;

    if (opts.automatic) {
        // Never open over a pop-up the user is working in, or on top of another tour.
        if (document.querySelector(MODALS) || active) return false;
        await waitForPage(steps, 6000);
        if (document.querySelector(MODALS) || active) return false;
    }

    stopTour();

    const built = [];
    let realElements = 0;
    for (const s of steps) {
        const popover = { title: esc(s.title), description: '<p>' + esc(s.body) + '</p>' };
        if (s.faqHref) {
            popover.description += '<p><a class="larga-tour-faq" href="' + esc(s.faqHref) + '">More questions? Open Help &amp; FAQs</a></p>';
        }
        if (s.target) {
            const el = find(s.target);
            if (!el) continue; // not on this page right now: skip it
            realElements++;
            built.push({ element: el, popover });
        } else {
            built.push({ popover }); // centred note
        }
    }
    if (realElements === 0) return false;

    const drv = factory({
        showProgress: true,
        progressText: '{{current}} of {{total}}',
        nextBtnText: 'Next',
        prevBtnText: 'Back',
        doneBtnText: 'Done',
        allowClose: true,
        allowKeyboardControl: true,
        smoothScroll: true,
        overlayOpacity: 0.7,
        stagePadding: 6,
        stageRadius: 8,
        popoverClass: 'larga-tour',
        steps: built,
        onPopoverRender: (popover) => {
            popover.wrapper.setAttribute('role', 'dialog');
            popover.wrapper.setAttribute('aria-label', 'Page tour');
            if (!drv.hasNextStep()) return;
            const skip = document.createElement('button');
            skip.type = 'button';
            skip.className = 'larga-tour-skip';
            skip.textContent = 'Skip';
            skip.addEventListener('click', () => drv.destroy());
            popover.footerButtons.prepend(skip);
        },
        onDestroyed: () => { if (active === drv) active = null; }
    });
    active = drv;

    // The FAQ link is a normal link (Blazor handles the navigation); just close the tour.
    document.addEventListener('click', function onClick(e) {
        const link = e.target instanceof Element ? e.target.closest('.larga-tour-faq') : null;
        if (link) {
            document.removeEventListener('click', onClick, true);
            drv.destroy();
        } else if (active !== drv) {
            document.removeEventListener('click', onClick, true);
        }
    }, true);

    drv.drive();
    return true;
}
