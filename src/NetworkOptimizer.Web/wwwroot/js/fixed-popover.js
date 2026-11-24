// Pins an lt-fpop popover to the button that opened it with fixed positioning, so a parent's
// overflow (a card, a table's scroll box) does not clip it, and keeps it there on scroll and resize.
// A phone's bottom sheet (app.css) is left alone. watch() animates any lt-fpop's height changes.

const placed = new Map();

/**
 * @param {HTMLElement} pop
 * @param {HTMLElement} anchor
 * @param {{ above?: boolean }} [opts] above: open over the button (it sits at the bottom of its
 *   card), dropping below only when the space above is too short.
 */
export function place(pop, anchor, opts) {
    unplace(pop);
    if (!pop || !anchor) return;
    const above = !!(opts && opts.above);
    const update = () => {
        if (window.matchMedia('(max-width: 768px)').matches) {
            pop.style.position = pop.style.top = pop.style.left = '';
            delete pop.dataset.placedAbove;
            return;
        }
        const r = anchor.getBoundingClientRect();
        const width = pop.offsetWidth;
        const height = pop.offsetHeight;
        pop.style.position = 'fixed';
        const fitsAbove = r.top - height - 8 >= 8;
        const isAbove = above && fitsAbove;
        pop.style.top = isAbove ? `${r.top - height - 8}px` : `${r.bottom + 8}px`;
        if (isAbove) pop.dataset.placedAbove = ''; else delete pop.dataset.placedAbove;
        pop.style.left = `${Math.max(16, Math.min(r.right - width + (above ? 0 : 40), window.innerWidth - width - 16))}px`;
    };
    update();
    window.addEventListener('scroll', update, true);
    window.addEventListener('resize', update);
    placed.set(pop, update);
}

const watched = new Map();

/**
 * Animates the popover's height whenever its content changes it (loading to loaded, an option that adds a
 * field, a filter list narrowing), on a phone's bottom sheet and a desktop popover alike, and re-pins a
 * placed popover at its new height. Works on an unplaced popover too (Live Traffic's filter editor, which
 * hangs under its chip). Call once, when the popover first renders.
 */
export function watch(pop) {
    if (!pop || watched.has(pop) || typeof ResizeObserver === 'undefined') return;
    let prev = pop.offsetHeight;
    let animating = false;
    const ro = new ResizeObserver(() => {
        if (animating) return;
        const next = pop.offsetHeight;
        if (Math.abs(next - prev) < 2) return;
        placed.get(pop)?.();
        const from = prev;
        prev = next;
        // Not while hidden awaiting its first placement: place() would measure it mid-animation.
        if (!from || getComputedStyle(pop).visibility === 'hidden'
            || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        animating = true;
        // Runs before paint, so the new height never shows unanimated. Grows away from its button: a
        // popover pinned above it keeps its bottom edge there (top moves with the height); one below, or a
        // phone's bottom sheet, keeps the edge it is anchored by.
        const keyframes = [{ height: `${from}px`, overflow: 'hidden' }, { height: `${next}px`, overflow: 'hidden' }];
        if ('placedAbove' in pop.dataset) {
            const top = parseFloat(pop.style.top);
            keyframes[0].top = `${top + next - from}px`;
            keyframes[1].top = `${top}px`;
        }
        const a = pop.animate(keyframes, { duration: 180, easing: 'ease-out' });
        const done = () => { animating = false; prev = pop.offsetHeight; };
        a.onfinish = done;
        a.oncancel = done;
    });
    ro.observe(pop);
    watched.set(pop, ro);
}

/**
 * Plays the popover out before it is removed: a phone's bottom sheet slides down from wherever a drag left
 * it, with its scrim fading; a desktop popover fades. Resolves when done, so the caller removes it then.
 */
export function dismiss(pop, kind) {
    if (!pop || !pop.isConnected || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return Promise.resolve();
    // A dropdown menu (Live Traffic's + Filter) folds back up toward its button on every screen.
    if (kind === 'menu') {
        return pop.animate([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translateY(-4px) scale(0.97)' }],
            { duration: 110, easing: 'ease-in', fill: 'forwards' }).finished.then(() => undefined, () => undefined);
    }
    const sheet = window.matchMedia('(max-width: 768px)').matches;
    const from = getComputedStyle(pop).transform;
    const a = pop.animate(sheet
        ? [{ transform: from === 'none' ? 'translateY(0)' : from }, { transform: 'translateY(100%)' }]
        : [{ opacity: 1 }, { opacity: 0, transform: 'translateY(6px)' }],
        { duration: sheet ? 200 : 120, easing: 'ease-in', fill: 'forwards' });
    const backdrop = pop.previousElementSibling;
    if (sheet && backdrop && backdrop.classList.contains('lt-fpop-backdrop'))
        backdrop.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 200, easing: 'ease-in', fill: 'forwards' });
    return a.finished.then(() => undefined, () => undefined);
}

function unplace(pop) {
    for (const [p, update] of placed) {
        if (pop && p !== pop) continue;
        window.removeEventListener('scroll', update, true);
        window.removeEventListener('resize', update);
        placed.delete(p);
    }
}

/** Stops following the button: call when the popover closes. With no argument, releases every popover. */
export function release(pop) {
    unplace(pop);
    for (const [p, ro] of watched) {
        if (pop && p !== pop) continue;
        ro.disconnect();
        watched.delete(p);
    }
}
