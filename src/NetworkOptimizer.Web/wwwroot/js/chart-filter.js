// Shared behaviour for the filter chip rows under the Monitoring charts.

/**
 * A reset control at the right of a chip row, shown ONLY while something is filtered out.
 *
 * Always-on would be a permanently dead button on the common case, where nothing is hidden and
 * there is nothing to restore. It also has to be re-added after every render: each caller builds
 * its chips by assigning innerHTML, which wipes this element along with them - so call it at the
 * END of the function that renders the chips.
 *
 * Positioned absolutely rather than as a flex child, so the chips stay centred as they were and
 * the control does not shift them when it appears.
 *
 * `label` is the tooltip and the accessible name. It defaults to the chart wording because eight of
 * the nine rows filter chart series; the port stats row filters cards, so it passes its own.
 */
const RESERVED_CLASS = 'wan-filter-badges-reserved';

/**
 * The standard clear-filter glyph: funnel with an X. Stroked, currentColor, 24 viewBox - the same
 * idiom as the X icons already in the app, so it inherits hover colour for free.
 *
 * Exported because the stats tables draw the same control into a table header, where the measured
 * placement below has nothing to measure. Sharing the markup is the point: one control on two
 * surfaces must not become two drawings of it.
 */
export const FILTER_RESET_SVG =
    '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" '
    + 'stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
    + '<path d="M3 5h14l-5.25 6.2V17l-3.5 1.75v-7.55z"/>'
    + '<path d="m16.5 16.5 4.5 4.5m0-4.5-4.5 4.5"/>'
    + '</svg>';

/// Do the two rectangles share any area at all.
function collides(btn, chips) {
    const b = btn.getBoundingClientRect();
    return chips.some(c => {
        const r = c.getBoundingClientRect();
        return b.left < r.right && b.right > r.left && b.top < r.bottom && b.bottom > r.top;
    });
}

/**
 * Put the control somewhere it does not sit on top of a chip.
 *
 * Absolute at the top right is right until the chips reach that corner, and with enough series the
 * row wraps and they do. A wrapped row almost always leaves space at the end of its LAST line
 * though, so that is the second choice - the gap is already there, and the control is least in the
 * way in it. Only when neither line has room does the row give up a gutter, which costs the chips
 * some width and is why it is the fallback rather than the default.
 *
 * Measured, so it needs real geometry. Every Monitoring tab renders at once and is revealed by
 * class, so a chip row on an inactive tab has no layout at all and measures as zero - deciding
 * anything from that would place the control by accident. Those reserve the gutter instead, which
 * cannot overlap whatever the row turns out to look like, and the next render measures properly
 * once the tab is on screen.
 */
function place(container, btn) {
    const chips = [...container.querySelectorAll('.wan-filter-badge')];
    btn.style.top = '0px';
    if (!chips.length || !container.offsetParent || !btn.offsetWidth) {
        container.classList.add(RESERVED_CLASS);
        return;
    }

    container.classList.remove(RESERVED_CLASS);
    const rows = [...new Set(chips.map(c => c.offsetTop))].sort((a, b) => a - b);
    const candidates = rows.length > 1 ? [rows[0], rows[rows.length - 1]] : [rows[0]];

    for (const rowTop of candidates) {
        // Centred on the chips of that line rather than aligned to their top, or it reads as
        // floating above a short row.
        const rowHeight = Math.max(...chips.filter(c => c.offsetTop === rowTop).map(c => c.offsetHeight));
        btn.style.top = `${rowTop + Math.max(0, (rowHeight - btn.offsetHeight) / 2)}px`;
        if (!collides(btn, chips)) return;
    }

    container.classList.add(RESERVED_CLASS);
    btn.style.top = `${rows[0]}px`;
}

export function renderFilterReset(container, isFiltered, onReset, label = 'Clear series filter') {
    if (!container) return;

    const existing = container.querySelector('.wan-filter-reset');
    if (!isFiltered) {
        existing?.remove();
        container.classList.remove(RESERVED_CLASS);
        return;
    }
    // Already there: the row may still have reflowed under it (a tab shown, the window resized,
    // a series come or gone), so the placement is redone rather than left as it was.
    if (existing) {
        place(container, existing);
        return;
    }

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'wan-filter-reset';
    btn.innerHTML = FILTER_RESET_SVG;
    btn.setAttribute('aria-label', label);
    btn.setAttribute('data-tooltip', label);
    btn.addEventListener('click', (e) => {
        // The row itself carries a delegated chip handler. This is not a chip, so that handler
        // ignores it anyway - stopping here keeps it from having to know that.
        e.stopPropagation();
        onReset();
    });
    container.appendChild(btn);
    place(container, btn);
}

/// True when at least one entry has been switched off, which is what makes a reset meaningful.
export function isFiltered(visibility) {
    return Object.values(visibility ?? {}).some(v => v === false);
}
