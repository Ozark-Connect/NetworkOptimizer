// Expanding a card reveals content that is often below the fold, leaving the reader to scroll to
// see what their own click just produced. This nudges the view so the newly revealed content is
// visible.
//
// Deliberately conservative, because it runs app-wide on a shared pattern:
//   - only on EXPAND, never on collapse
//   - only ever scrolls DOWN, and never further than putting the card's top at the top of the pane,
//     so the header you just clicked can never be pushed off screen
//   - does nothing when the card already fits
//   - reads layout and sets scrollTop; it never writes padding, margin, or classes, so it cannot
//     leave anything behind on elements it does not own
//
// One delegated listener rather than per-component wiring: every collapsible in the app is a
// .card-header-collapsible followed by an .expand-wrapper, so they all get this for free.
(function () {
    'use strict';

    // The expand animates grid-template-rows over 0.25s; measure once it has settled.
    var SETTLE_MS = 320;

    function scrollerOf(node) {
        for (var n = node.parentElement; n && n !== document.body; n = n.parentElement) {
            var oy = getComputedStyle(n).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight + 4) return n;
        }
        return document.scrollingElement || document.documentElement;
    }

    function reveal(header) {
        var wrapper = header.nextElementSibling;
        // Collapsed, or not the pattern we expect - nothing to do.
        if (!wrapper || !wrapper.classList.contains('expand-wrapper')) return;
        if (!wrapper.classList.contains('expanded')) return;

        var card = header.parentElement;
        if (!card) return;

        var sc = scrollerOf(card);
        var isDoc = sc === document.scrollingElement || sc === document.documentElement;
        var paneTop = isDoc ? 0 : sc.getBoundingClientRect().top;
        var paneBottom = isDoc ? window.innerHeight : sc.getBoundingClientRect().bottom;

        var r = card.getBoundingClientRect();
        var below = r.bottom - paneBottom;
        if (below <= 2) return;

        // Never past the card's own top: scrolling further would hide the header just clicked.
        var headroom = Math.max(0, r.top - paneTop);
        sc.scrollTop += Math.min(below, headroom);
    }

    document.addEventListener('click', function (e) {
        var header = e.target.closest && e.target.closest('.card-header-collapsible');
        if (!header) return;
        // After the component has re-rendered and the transition has run.
        setTimeout(function () { reveal(header); }, SETTLE_MS);
    }, true);
})();
