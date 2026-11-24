// Settings: each card's bottom button row (the last .action-buttons directly in its .card-body) is
// its save row. On desktop, while a card is on screen and that row is not, a floating copy of the
// row's Save sits where the real one would, at the bottom of the card's visible area, and clicks it.
// An edit inside a card marks it unsaved until that button's save succeeds; leaving Settings then asks.

const DESKTOP = '(min-width: 769px)';
const MIN_VISIBLE = 160;   // px of a card on screen before a floating Save is worth showing
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

const label = b => b.textContent.trim();

// The row's Save by its label (a Save can be styled secondary beside a primary Test), else its primary.
function saveButton(card) {
    const row = saveRow(card);
    if (!row) return null;
    return [...row.querySelectorAll('button')].find(b => /^(Sav(e|ing)|Updat(e|ing)|Connect(ing)?)\b/i.test(label(b)))
        ?? row.querySelector('.btn-primary, .btn-success');
}

// Any of these in the save row settles the card's edits (UniFi Console Connection saves on Connect);
// Test, Download, or Disconnect does not.
const SETTLES = /^(Sav(e|ing)|Updat(e|ing)|Connect(ing)?|Clear(ing)?)\b/i;

// Floated only for a plain save row: a lone Save, or Save with Cancel. A row that also tests,
// connects, or deploys is left where it is.
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

// After a save click, the card's own message says how it went. A new error or warning keeps the card
// unsaved, unless its text says the settings were saved ("Saved, but InfluxDB is unreachable").
// A floating Save also shows a check, cross, or mark beside itself, and scrolls a problem into view.
const SUCCESS = '.alert-success';
const PROBLEM = '.alert-danger, .alert-error, .alert-warning, .form-error';
const RESULT_WAIT = 15000;     // ms a save has to report before we stop listening
const RESULT_SHOWN = 2500;     // ms the check or cross stays up
// A save that neither disables its button nor posts a new message is judged after this long, by the
// message the card still shows (none: done). A failure that reports later, inside RESULT_WAIT, marks
// the card unsaved again.
const QUIET = 1500;
const ICONS = {
    success: '<path d="M20 6 9 17l-5-5"/>',
    error: '<path d="M18 6 6 18M6 6l12 12"/>',
    warning: '<path d="M12 8v5M12 17h.01"/>',
};
// card -> { before: Map(message element -> text), button, since, dirty, sawBusy, cleared, float }.
// A save on a card with no edits (dirty false) only drives the floating result, never the mark.
let settling = new Map();
let result = null;             // the check or cross element
let resultAnchor = null;       // the floating button it sits beside
let resultTimer = 0;

function messages(card) {
    const seen = new Map();
    for (const el of card.querySelectorAll(`${SUCCESS}, ${PROBLEM}`)) seen.set(el, el.textContent.trim());
    return seen;
}

function showResult(btn, kind) {
    if (!btn || btn.hidden) return;
    if (!result) {
        result = document.createElement('span');
        result.className = 'settings-floating-result';
        document.body.appendChild(result);
    }
    result.className = `settings-floating-result is-${kind}`;
    result.innerHTML = `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[kind]}</svg>`;
    resultAnchor = btn;
    result.hidden = false;
    placeResult();
    clearTimeout(resultTimer);
    resultTimer = setTimeout(() => { if (result) result.hidden = true; resultAnchor = null; }, RESULT_SHOWN);
}

// Follows its button, which moves as the card re-renders and the page scrolls.
function placeResult() {
    if (!result || result.hidden) return;
    if (!resultAnchor || resultAnchor.hidden) { result.hidden = true; return; }
    const r = resultAnchor.getBoundingClientRect();
    // Just outside the card's right edge, clear of its border.
    const card = [...floats].find(([, b]) => b === resultAnchor)?.[0];
    const edge = card ? card.getBoundingClientRect().right : r.right;
    result.style.left = `${Math.max(r.right, edge) + 8}px`;
    result.style.top = `${r.top + r.height / 2 - 14}px`;
}

const reportsSaved = el => /\bsaved\b/i.test(el.textContent) && !/\bnot saved\b/i.test(el.textContent);

function startSettle(card, button) {
    settling.set(card, {
        before: messages(card), button, since: Date.now(), dirty: unsaved.has(card),
        sawBusy: false, cleared: false, float: false,
    });
    setTimeout(schedule, QUIET + 50);
}

/** Settles the card's save on the first problem or success among these messages; false when there is neither. */
function settleOn(card, s, candidates) {
    const problem = candidates.find(el => el.matches(PROBLEM));
    const ok = candidates.find(el => el.matches(SUCCESS));
    if (!problem && !ok) return false;
    settling.delete(card);
    if (s.dirty) {
        if (problem && !reportsSaved(problem)) unsaved.add(card);
        else unsaved.delete(card);
    }
    if (!s.float) return true;
    if (problem) {
        showResult(floats.get(card), problem.matches('.alert-warning') ? 'warning' : 'error');
        problem.scrollIntoView({ behavior: 'smooth', block: 'center' });
    } else {
        showResult(floats.get(card), 'success');
    }
    return true;
}

function checkSettles() {
    const now = Date.now();
    for (const [card, s] of settling) {
        if (!card.isConnected || now - s.since > RESULT_WAIT) { settling.delete(card); continue; }
        if (s.button.isConnected && s.button.disabled) continue;
        // A repeat save can leave the same message in place: once the button has been busy and is back,
        // whatever message the card shows is this save's.
        const fresh = [...messages(card)].filter(([el, text]) => s.sawBusy || s.before.get(el) !== text).map(([el]) => el);
        if (settleOn(card, s, fresh)) continue;
        // No message: a busy spell that has ended is a finished save. A button that never goes busy
        // gets QUIET, and stays watched in case its failure reports late.
        if (s.sawBusy) { settling.delete(card); if (s.dirty) unsaved.delete(card); }
        else if (!s.cleared && now - s.since >= QUIET) {
            // A save that finishes in one render repeats its last message unchanged, with no busy spell:
            // nothing in the card moved, so the message it still shows is this save's too.
            if (settleOn(card, s, [...messages(card).keys()])) continue;
            s.cleared = true;
            if (s.dirty) unsaved.delete(card);
        }
    }
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
            if (!real || real.disabled) return;
            real.click(); // onClick starts the settle
            const s = settling.get(card);
            if (s) s.float = true;
        });
        document.body.appendChild(btn);
        floats.set(card, btn);
    }
    return btn;
}

function place() {
    frame = 0;
    if (!container) return;
    checkSettles();
    const desktop = window.matchMedia(DESKTOP).matches;
    // The page scrolls inside .page-content, with the footer below it: that box, not the window, is
    // the visible area.
    const area = (container.closest('.page-content') ?? document.documentElement).getBoundingClientRect();
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
        // In the card's lower right, where the row ends (as though Save were alone in it), inset from the
        // visible area's bottom by the card's own bottom padding. Fixed positions count from the window.
        const body = real.closest('.card-body');
        const inset = parseFloat(getComputedStyle(body).paddingBottom) || 0;
        btn.style.right = `${window.innerWidth - rowRect.right}px`;
        btn.style.minWidth = `${real.getBoundingClientRect().width}px`;
        btn.style.bottom = `${window.innerHeight - visibleBottom + inset}px`;
    }
    placeResult();
    // Cards Blazor removed take their floating buttons with them.
    for (const [card, btn] of floats) {
        if (!card.isConnected) { btn.remove(); floats.delete(card); unsaved.delete(card); settling.delete(card); }
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
    markUnsaved(card);
}

// An edit made while a save is out is not covered by that save, whatever it reports.
function markUnsaved(card) {
    settling.delete(card);
    unsaved.add(card);
}

// data-marks-unsaved: a button that changes the card's form without an input event (a preset).
function onClick(e) {
    const card = cardOf(e.target);
    if (!card) return;
    const button = e.target.closest('button');
    // Once a quiet save has counted as done, a message after another button (Test) is that button's.
    checkSettles();
    const s = settling.get(card);
    if (s?.cleared && button && button !== s.button) settling.delete(card);
    if (button && saveRow(card)?.contains(button) && SETTLES.test(label(button))) {
        if (!button.disabled) startSettle(card, button);
    }
    else if (e.target.closest('[data-marks-unsaved]') && saveButton(card)) markUnsaved(card);
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
    // The busy flip can come and go within one frame, so it is caught here rather than in place().
    observer = new MutationObserver(records => {
        for (const s of settling.values()) {
            if (records.some(r => r.target === s.button && r.attributeName === 'disabled' && s.button.disabled))
                s.sawBusy = true;
        }
        schedule();
    });
    observer.observe(container, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'disabled'] });
    schedule();
}

/** Asked by the page before Blazor navigates; true when leaving is fine. Settings to Settings always is. */
export function confirmLeave(url) {
    try {
        if (new URL(url, location.href).pathname.toLowerCase().startsWith('/settings')) return true;
    } catch { /* an unparseable target is treated as leaving */ }
    checkSettles();
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
    result?.remove();
    result = null;
    settling = new Map();
    clearTimeout(resultTimer);
    unsaved = new Set();
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    container = null;
}
