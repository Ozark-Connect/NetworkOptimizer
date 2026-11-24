// Pins an lt-fpop popover to the button that opened it with fixed positioning, so a parent's
// overflow (a card, a table's scroll box) does not clip it, and keeps it there on scroll and resize.
// A phone's bottom sheet (app.css) is left alone.

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
            return;
        }
        const r = anchor.getBoundingClientRect();
        const width = pop.offsetWidth;
        const height = pop.offsetHeight;
        pop.style.position = 'fixed';
        const fitsAbove = r.top - height - 8 >= 8;
        pop.style.top = above && fitsAbove ? `${r.top - height - 8}px` : `${r.bottom + 8}px`;
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
 * field), on a phone's bottom sheet and a desktop popover alike, and re-pins a placed popover at its new
 * height. Call once, when the popover first renders.
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
        if (!from || window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        animating = true;
        // Runs before paint, so the new height never shows unanimated.
        const a = pop.animate([{ height: `${from}px`, overflow: 'hidden' }, { height: `${next}px`, overflow: 'hidden' }],
            { duration: 180, easing: 'ease-out' });
        const done = () => { animating = false; prev = pop.offsetHeight; };
        a.onfinish = done;
        a.oncancel = done;
    });
    ro.observe(pop);
    watched.set(pop, ro);
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
