// Show/hide button for password inputs on pages rendered without the live connection (Request Access),
// where a Blazor @onclick never runs. Same markup and look as Login.razor's toggle: a .password-toggle
// button marked data-password-toggle inside .password-field, holding an eye and an eye-off icon. Listens
// on the document, so it also works after Blazor's enhanced navigation.
(function () {
    document.addEventListener('click', e => {
        const toggle = e.target instanceof Element ? e.target.closest('[data-password-toggle]') : null;
        const input = toggle?.closest('.password-field')?.querySelector('input');
        if (!input) return;

        const show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        const label = show ? 'Hide password' : 'Show password';
        toggle.setAttribute('aria-label', label);
        toggle.title = label;
        toggle.querySelector('[data-show-icon]').hidden = show;
        toggle.querySelector('[data-hide-icon]').hidden = !show;
    });
})();
