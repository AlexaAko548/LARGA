// Live password strength for inputs marked data-strength-input="<meter id>" (Request Access page).
// Only a hint while typing - the server checks the shared password policy (InputValidator.PasswordRules:
// 6-64 characters, a letter and a number, no spaces) when the form is sent. Listens on the document, so
// it also works after Blazor's enhanced navigation.
(function () {
    const LEVELS = [
        { label: 'Too weak', css: 'weak' },
        { label: 'Weak', css: 'weak' },
        { label: 'Fair', css: 'fair' },
        { label: 'Good', css: 'good' },
        { label: 'Strong', css: 'strong' },
    ];

    function describe(pw) {
        // Same checks as the server (char.IsLetter / char.IsDigit / char.IsWhiteSpace).
        const missing = [];
        if (pw.length < 6) missing.push('6+ characters');
        if (!/\p{L}/u.test(pw)) missing.push('a letter');
        if (!/\p{Nd}/u.test(pw)) missing.push('a number');
        if (/\s/.test(pw)) missing.push('no spaces');
        if (missing.length) return { score: missing.length >= 2 ? 0 : 1, missing };

        // Meets the policy: Fair, then Good / Strong for length, mixed case and symbols.
        const extras = (pw.length >= 10 ? 1 : 0)
            + (/[a-z]/.test(pw) && /[A-Z]/.test(pw) ? 1 : 0)
            + (/[^\p{L}\p{Nd}]/u.test(pw) ? 1 : 0);
        return { score: 2 + Math.min(2, extras), missing };
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
            label.textContent = label.dataset.hint || '';
            return;
        }

        const { score, missing } = describe(pw);
        const level = LEVELS[score];
        meter.classList.add(level.css);
        meter.style.setProperty('--strength', String((score + 1) / 5));
        label.textContent = missing.length ? `${level.label} - needs ${missing.join(', ')}` : level.label;
    });
})();
