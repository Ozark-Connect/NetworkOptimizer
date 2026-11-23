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
    release(pop);
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

/** Stops following the button: call when the popover closes. With no argument, releases every popover. */
export function release(pop) {
    for (const [p, update] of placed) {
        if (pop && p !== pop) continue;
        window.removeEventListener('scroll', update, true);
        window.removeEventListener('resize', update);
        placed.delete(p);
    }
}
