// Live Traffic row motion: rows that change place slide there instead of jumping (FLIP), and the
// expanded row ([data-flip-open]) holds exactly where it is on screen while the list changes around
// it, as long as it is in view; the user's own scrolling moves it, and scrolling it nearly out of
// view, or a screen or more of scrolling around, lets it go.
// A MutationObserver runs after each DOM update and before the browser paints, so the held row never
// shows in its new place first. Each [data-flip] row is matched to its last state by key.

const DURATION = 300;
const MAX_SLIDE = 240; // px: about four rows
const MIN_VISIBLE = 40; // px of the expanded row still on screen for it to keep its place

const RELEASE_SCREENS = 1; // the user's own scrolling, in screen heights, that lets the expanded row go

const last = new WeakMap(); // container -> { pos: Map(key -> y in the list), index: Map(key -> i), anchorKey, anchorY, released }
const scrolls = new WeakMap(); // scroller -> { lastTop, own, moved }: how far the user, not the hold, has scrolled
let observer = null;

/**
 * Counts the user's scrolling on a scroller. The hold's own scrollBy is recorded in `own` and taken
 * back out, so holding the row never counts as the user scrolling.
 */
function track(scroller) {
    let s = scrolls.get(scroller);
    if (s) return s;
    s = { lastTop: scroller.scrollTop, own: 0, moved: 0 };
    const target = scroller === document.scrollingElement || scroller === document.documentElement ? window : scroller;
    target.addEventListener('scroll', () => {
        const top = scroller.scrollTop;
        s.moved += Math.abs(top - s.lastTop - s.own);
        s.own = 0;
        s.lastTop = top;
    }, { passive: true });
    scrolls.set(scroller, s);
    return s;
}

const reduced = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;

/** The top and bottom of the scroller's visible area, in viewport pixels. */
function viewOf(scroller) {
    if (scroller === document.scrollingElement || scroller === document.documentElement) return [0, window.innerHeight];
    const r = scroller.getBoundingClientRect();
    return [Math.max(0, r.top), Math.min(window.innerHeight, r.bottom)];
}

/**
 * The element that actually scrolls the page: the app scrolls inside its layout, not the window.
 * The layout's .page-content first: a .table-responsive (overflow-x auto, so overflow-y computes auto)
 * overflowing by a pixel passed the search below on some passes only, and measuring the row against
 * two scrollers by turns read the user's own scroll as the list moving and scrolled the page back.
 */
function scrollerOf(el) {
    const page = el.closest('.page-content');
    if (page) return page;
    for (let p = el.parentElement; p; p = p.parentElement) {
        if (p.classList.contains('table-responsive')) continue;
        const y = getComputedStyle(p).overflowY;
        if ((y === 'auto' || y === 'scroll') && p.scrollHeight > p.clientHeight) return p;
    }
    return document.scrollingElement || document.documentElement;
}

/** Watches the page root and settles every [data-flip] list after each update. Safe to call again. */
export function watch(rootSelector, listSelector) {
    const root = document.querySelector(rootSelector);
    if (!root) return;
    observer?.disconnect();
    observer = new MutationObserver(() => settle(root, listSelector));
    observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-flip-open', 'class'] });
    settle(root, listSelector);
}

function settle(root, listSelector) {
    for (const container of root.querySelectorAll(listSelector)) {
        // A hidden layout (the desktop table on a phone) has no positions to keep.
        if (!container.offsetParent) continue;
        const top = container.getBoundingClientRect().top;
        const rows = [...container.querySelectorAll('[data-flip]')];
        const pos = new Map(rows.map(r => [r.dataset.flip, r.getBoundingClientRect().top - top]));
        const index = new Map(rows.map((r, i) => [r.dataset.flip, i]));

        const anchorRow = container.querySelector('[data-flip][data-flip-open]');
        const openKey = anchorRow?.dataset.flip ?? null;
        const scroller = anchorRow ? scrollerOf(container) : null;
        // The anchor's place in the scrolled content: unchanged by scrolling, changed by the list moving.
        const anchorY = anchorRow ? anchorRow.getBoundingClientRect().top + scroller.scrollTop : null;

        const before = last.get(container);
        // A newly expanded row starts held and the user's scrolling counts from zero; a screen or more
        // of it lets the row go for as long as it stays expanded.
        let released = false;
        if (anchorRow) {
            const s = track(scroller);
            if (before?.anchorKey !== openKey) s.moved = 0;
            else released = before.released;
            const [viewTop, viewBottom] = viewOf(scroller);
            if (s.moved > (viewBottom - viewTop) * RELEASE_SCREENS) released = true;
        }
        last.set(container, { pos, index, anchorKey: openKey, anchorY, released, scroller });
        if (!before) continue;

        // The expanded row keeps its place on screen while it was in view just before this update:
        // the user can scroll freely, and scrolling it nearly out of view, or scrolling around a lot,
        // lets it go. Holding it scrolls the page, which carries the list with it by however far the
        // anchor moved inside the list; rows add that back so they slide rather than jump.
        let carry = 0;
        let held = false;
        // Positions measured against another scroller are not comparable: hold nothing on such a pass.
        if (anchorRow && !released && before.anchorKey === openKey && before.anchorY != null && before.scroller === scroller
            && Math.abs(anchorY - before.anchorY) >= 1) {
            const wasTop = before.anchorY - scroller.scrollTop;
            const height = anchorRow.getBoundingClientRect().height;
            const [viewTop, viewBottom] = viewOf(scroller);
            const visible = Math.min(wasTop + height, viewBottom) - Math.max(wasTop, viewTop);
            if (visible >= Math.min(MIN_VISIBLE, height / 2)) {
                const s = track(scroller);
                const from = scroller.scrollTop;
                scroller.scrollBy({ top: anchorY - before.anchorY, behavior: 'instant' });
                s.own += scroller.scrollTop - from;
                carry = (pos.get(openKey) ?? 0) - (before.pos.get(openKey) ?? pos.get(openKey) ?? 0);
                held = true;
            }
        }
        if (reduced()) continue;

        for (const row of rows) {
            const key = row.dataset.flip;
            if (held && key === openKey) continue;
            if (carry === 0 && before.index.get(key) === index.get(key)) continue;
            const from = before.pos.get(key);
            if (from === undefined) continue;
            const delta = from - pos.get(key) + carry;
            if (Math.abs(delta) < 1) continue;
            row.getAnimations?.().forEach(a => a.cancel());
            // A long jump would sweep through the list and push past its edges: fade in place instead.
            row.animate(
                Math.abs(delta) > MAX_SLIDE
                    ? [{ opacity: 0.3 }, { opacity: 1 }]
                    : [{ transform: `translateY(${delta}px)` }, { transform: 'translateY(0)' }],
                { duration: DURATION, easing: 'ease-out' });
        }
    }
}
