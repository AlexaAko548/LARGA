// Live password strength for inputs marked data-strength-input="<meter id>" (Request Access page).
// Only a hint while typing - the server checks the same rules (InputValidator.ValidateStrongPassword)
// when the form is sent. Listens on the document, so it also works after Blazor's enhanced navigation.
(function () {
    const LEVELS = [
        { label: 'Too weak', css: 'weak' },
        { label: 'Weak', css: 'weak' },
        { label: 'Fair', css: 'fair' },
        { label: 'Good', css: 'good' },
        { label: 'Strong', css: 'strong' },
    ];

    function describe(pw) {
        const missing = [];
        if (pw.length < 8) missing.push('8+ characters');
        if (!/[A-Z]/.test(pw)) missing.push('an uppercase letter');
        if (!/[a-z]/.test(pw)) missing.push('a lowercase letter');
        if (!/[0-9]/.test(pw)) missing.push('a number');
        if (/\s/.test(pw)) missing.push('no spaces');

        let score = 4 - Math.min(4, missing.length);
        if (missing.length === 0 && (pw.length >= 12 || /[^A-Za-z0-9]/.test(pw))) score = 4;
        else if (missing.length === 0) score = 3;
        return { score, missing };
    }

    document.addEventListener('input', e => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement) || !input.dataset.strengthInput) return;
        const meter = document.getElementById(input.dataset.strengthInput);
        if (!meter) return;

        const pw = input.value;
        const label = meter.querySelector('.password-strength-label');
        meter.classList.remove('weak', 'fair', 'good', 'strong');
        if (!pw) {
            meter.style.setProperty('--strength', '0');
            label.textContent = 'At least 8 characters, with an uppercase letter, a lowercase letter and a number.';
            return;
        }

        const { score, missing } = describe(pw);
        const level = LEVELS[score];
        meter.classList.add(level.css);
        meter.style.setProperty('--strength', String((score + 1) / 5));
        label.textContent = missing.length ? `${level.label} - add ${missing.join(', ')}` : level.label;
    });
})();
