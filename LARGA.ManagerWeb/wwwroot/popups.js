// Dropdowns behave like a normal site's: only one open at a time, and a click anywhere else (or
// Escape) closes it. Each dropdown keeps its own open/closed state in Blazor, so this doesn't
// change state itself - it clicks the dropdown's own toggle button, the same as the user would.
//
// Markup: a wrapper with data-popup, holding the toggle button (data-popup-toggle) and, while
// open, the panel (data-popup-panel). Escape also closes the topmost pop-up window or drawer
// through its own close/cancel button.
(function () {
    function openPopups() {
        return Array.from(document.querySelectorAll('[data-popup]'))
            .filter(p => p.querySelector('[data-popup-panel]'));
    }

    function close(popup) {
        const toggle = popup.querySelector('[data-popup-toggle]');
        if (toggle) toggle.click();
    }

    // Capture phase: runs before the click reaches its target, so the dropdown that was open
    // closes first and the clicked one (if any) then opens - one click, not two.
    document.addEventListener('click', e => {
        if (!e.isTrusted) return; // our own toggle clicks below
        for (const popup of openPopups()) {
            if (!popup.contains(e.target)) close(popup);
        }
    }, true);

    const MODAL_SELECTORS = '.report-overlay, .modal-overlay, .profile-overlay, .chat-drawer-backdrop';
    const CLOSE_SELECTORS = '.close-modal, .modal-close-btn, [data-modal-close]';

    document.addEventListener('keydown', e => {
        if (e.key !== 'Escape' || e.defaultPrevented) return;

        const popups = openPopups();
        if (popups.length) {
            popups.forEach(close);
            return;
        }

        // The pop-up window or drawer added last is the one on top.
        const layers = document.querySelectorAll(MODAL_SELECTORS);
        const top = layers[layers.length - 1];
        if (!top) return;
        if (top.classList.contains('chat-drawer-backdrop')) {
            top.click();
            return;
        }
        const button = top.querySelector(CLOSE_SELECTORS) || top.querySelector('.cancel-btn');
        if (button && !button.disabled) button.click();
    });
})();
