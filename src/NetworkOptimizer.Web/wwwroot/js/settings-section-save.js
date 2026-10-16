// Settings: each card's bottom button row (the last .action-buttons directly in its .card-body) is
// its save row. On desktop, while a card is on screen and that row is not, a floating copy of the
// row's primary button sits beside the card's right edge, level with the bottom of its visible area,
// and clicks the real one.
// An edit inside a card marks it unsaved until that button is clicked; leaving Settings then asks.

const DESKTOP = '(min-width: 769px)';
const MIN_VISIBLE = 160;   // px of a card on screen before a floating Save is worth showing
const EDGE = 24;           // px inset from the card's right edge and the visible area's bottom
const GAP = 16;            // px between the card's right edge and a floating Save beside it
const TOP_THRESHOLD = 1 / 3; // the card's top must be above this fraction of the window height

let container = null;
let floats = new Map();    // card -> floating button
let unsaved = new Set();   // cards with edits since their last save click
let frame = 0;
let observer = null;
const listeners = [];

function saveRow(card) {
    const rows = card.querySelectorAll(':scope > .card-body > .action-buttons');
    return rows.length ? rows[rows.length - 1] : null;
}

function saveButton(card) {
    return saveRow(card)?.querySelector('.btn-primary, .btn-success') ?? null;
}

// Floated only for a plain save row: a lone Save, or Save with Cancel. A row that also tests,
// connects, or deploys is left where it is.
const label = b => b.textContent.trim();
function floatableSave(card) {
    const row = saveRow(card);
    if (!row) return null;
    const buttons = [...row.querySelectorAll('button')];
    const save = buttons.find(b => /^Sav(e|ing)\b/i.test(label(b)));
    if (!save) return null;
    const others = buttons.filter(b => b !== save);
    return others.length === 0 || (others.length === 1 && /^Cancel$/i.test(label(others[0]))) ? save : null;
}

function cardOf(el) {
    const card = el?.closest?.('.card');
    return card && card.parentElement === container ? card : null;
}

function floatFor(card) {
    let btn = floats.get(card);
    if (!btn) {
        btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-primary settings-floating-save';
        btn.hidden = true;
        btn.addEventListener('click', () => {
            const real = floatableSave(card);
            if (real && !real.disabled) real.click();
        });
        document.body.appendChild(btn);
        floats.set(card, btn);
    }
    return btn;
}

function place() {
    frame = 0;
    if (!container) return;
    const desktop = window.matchMedia(DESKTOP).matches;
    // The page scrolls inside .page-content, with the footer below it: that box, not the window, is
    // the visible area.
    const area = (container.closest('.page-content') ?? document.documentElement).getBoundingClientRect();
    const vw = Math.min(area.right, window.innerWidth);
    const top = Math.max(area.top, 0);
    const vh = Math.min(area.bottom, window.innerHeight);
    for (const card of container.querySelectorAll(':scope > .card')) {
        const real = floatableSave(card);
        const existing = floats.get(card);
        const rect = card.getBoundingClientRect();
        const rowRect = real ? real.closest('.action-buttons').getBoundingClientRect() : null;
        const visibleTop = Math.max(rect.top, top);
        const visibleBottom = Math.min(rect.bottom, vh);
        // Only once the reader is into the card: its top has scrolled into the top third of the area.
        const show = desktop && real && rect.height > 0
            && rect.top <= top + (vh - top) * TOP_THRESHOLD
            && visibleBottom - visibleTop >= MIN_VISIBLE
            && (rowRect.top < top || rowRect.bottom > vh);
        if (!show) {
            if (existing) existing.hidden = true;
            continue;
        }
        const btn = floatFor(card);
        btn.textContent = real.textContent.trim().replace(/\s+/g, ' ');
        btn.disabled = real.disabled;
        btn.hidden = false;
        // Beside the card's right edge, near the bottom of its visible area; inside the card only when
        // the area leaves no room beside it. Fixed positions count from the window's edges.
        const beside = vw - rect.right >= btn.offsetWidth + 2 * GAP;
        btn.style.left = beside ? `${rect.right + GAP}px` : '';
        btn.style.right = beside ? '' : `${window.innerWidth - Math.min(rect.right, vw) + EDGE}px`;
        btn.style.bottom = `${window.innerHeight - visibleBottom + EDGE}px`;
    }
    // Cards Blazor removed take their floating buttons with them.
    for (const [card, btn] of floats) {
        if (!card.isConnected) { btn.remove(); floats.delete(card); unsaved.delete(card); }
    }
}

function schedule() {
    if (!frame) frame = requestAnimationFrame(place);
}

function on(target, type, handler, options) {
    target.addEventListener(type, handler, options);
    listeners.push(() => target.removeEventListener(type, handler, options));
}

function onEdit(e) {
    const t = e.target;
    if (!(t instanceof HTMLInputElement || t instanceof HTMLSelectElement || t instanceof HTMLTextAreaElement)) return;
    if (t.type === 'file' || t.closest('[data-autosave]')) return;
    const card = cardOf(t);
    if (!card || !saveButton(card) || saveRow(card).contains(t)) return;
    unsaved.add(card);
}

// data-marks-unsaved: a button that changes the card's form without an input event (a preset).
function onClick(e) {
    const card = cardOf(e.target);
    if (!card) return;
    if (saveButton(card)?.contains(e.target)) unsaved.delete(card);
    else if (e.target.closest('[data-marks-unsaved]') && saveButton(card)) unsaved.add(card);
}

/** Names of the cards with unsaved edits, for the leave prompt. */
function unsavedTitles() {
    return [...unsaved].filter(c => c.isConnected)
        .map(c => c.querySelector('.card-title')?.textContent.trim() || 'a section');
}

export function mount(el) {
    unmount();
    container = el;
    if (!container) return;
    on(window, 'scroll', schedule, { passive: true, capture: true });
    on(window, 'resize', schedule, { passive: true });
    on(document, 'input', onEdit, true);
    on(document, 'change', onEdit, true);
    on(document, 'click', onClick, true);
    on(window, 'beforeunload', e => {
        if (unsavedTitles().length) { e.preventDefault(); e.returnValue = ''; }
    });
    observer = new MutationObserver(schedule);
    observer.observe(container, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'disabled'] });
    schedule();
}

/** Asked by the page before Blazor navigates; true when leaving is fine. Settings to Settings always is. */
export function confirmLeave(url) {
    try {
        if (new URL(url, location.href).pathname.toLowerCase().startsWith('/settings')) return true;
    } catch { /* an unparseable target is treated as leaving */ }
    const titles = unsavedTitles();
    if (!titles.length) return true;
    return window.confirm(`You have unsaved changes in ${titles.join(', ')}. Leave without saving?`);
}

export function unmount() {
    listeners.splice(0).forEach(off => off());
    observer?.disconnect();
    observer = null;
    for (const btn of floats.values()) btn.remove();
    floats = new Map();
    unsaved = new Set();
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    container = null;
}
