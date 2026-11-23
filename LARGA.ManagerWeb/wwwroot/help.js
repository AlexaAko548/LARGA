// Help & FAQs page: scroll to the category named in the address (/help#ledger) once the
// page has drawn, and flash it so it's easy to spot.
export function scrollToCategory(id) {
    if (!id || !/^[a-z0-9-]+$/i.test(id)) return;
    const el = document.getElementById('faq-' + id);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    el.classList.add('faq-flash');
    setTimeout(() => el.classList.remove('faq-flash'), 1800);
}
