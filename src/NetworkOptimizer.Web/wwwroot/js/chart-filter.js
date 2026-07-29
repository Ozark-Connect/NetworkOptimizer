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
 */
export function renderFilterReset(container, isFiltered, onReset) {
    if (!container) return;

    const existing = container.querySelector('.wan-filter-reset');
    if (!isFiltered) {
        existing?.remove();
        return;
    }
    if (existing) return;

    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'wan-filter-reset';
    // The standard clear-filter glyph: funnel with an X. Stroked, currentColor, 24 viewBox -
    // the same idiom as the X icons already in the app, so it inherits hover colour for free.
    btn.innerHTML =
        '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" '
        + 'stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
        + '<path d="M3 5h14l-5.25 6.2V17l-3.5 1.75v-7.55z"/>'
        + '<path d="m16.5 16.5 4.5 4.5m0-4.5-4.5 4.5"/>'
        + '</svg>';
    btn.setAttribute('aria-label', 'Clear filter');
    btn.setAttribute('data-tooltip', 'Clear filter');
    btn.addEventListener('click', (e) => {
        // The row itself carries a delegated chip handler. This is not a chip, so that handler
        // ignores it anyway - stopping here keeps it from having to know that.
        e.stopPropagation();
        onReset();
    });
    container.appendChild(btn);
}

/// True when at least one entry has been switched off, which is what makes a reset meaningful.
export function isFiltered(visibility) {
    return Object.values(visibility ?? {}).some(v => v === false);
}
