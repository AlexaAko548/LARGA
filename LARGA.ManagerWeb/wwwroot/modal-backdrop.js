// A pop-up closes when its dark backdrop is clicked. A press that starts inside the pop-up (selecting
// text, dragging a slider) and is released over the backdrop also counts as a backdrop click to the
// browser, which closed the pop-up mid-edit. Only a press that both starts and ends on the backdrop
// closes it now. Runs before Blazor sees the click (window, capture phase).
(function () {
    const BACKDROPS = '.report-overlay, .modal-overlay, .profile-overlay, .chat-drawer-backdrop, .manager-menu-backdrop';
    let pressedOn = null;

    window.addEventListener('pointerdown', e => { pressedOn = e.target; }, true);

    window.addEventListener('click', e => {
        const target = e.target;
        const pressStart = pressedOn;
        pressedOn = null;
        if (!(target instanceof Element) || !target.matches(BACKDROPS)) return;
        // Keyboard "clicks" have no pointer press; leave those alone.
        if (pressStart !== null && pressStart !== target) {
            e.stopImmediatePropagation();
            e.preventDefault();
        }
    }, true);
})();
