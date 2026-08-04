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
    // A touch longer than the 0.25s expand transition, so the last pixels are followed.
    var FOLLOW_MS = 400;

    function scrollerOf(node) {
        for (var n = node.parentElement; n && n !== document.body; n = n.parentElement) {
            var oy = getComputedStyle(n).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight + 4) return n;
        }
        return document.scrollingElement || document.documentElement;
    }

    // Track the expansion as it happens rather than waiting for it to finish. The card grows over
    // the transition, so following it frame by frame reads as one movement - the view opening with
    // the panel - where measuring once at the end is a separate jump after the fact.
    //
    // Each frame moves by whatever is currently needed, which is small while the card is still
    // growing, so no easing of our own is wanted: the transition supplies the pacing.
    // Returns false only when there is nothing to wait for. Not-yet-expanded is NOT that: Blazor
    // has not re-rendered on the frame the click lands, so the class arrives a frame or two later -
    // treating its absence as "done" made the loop quit before it ever started, which is why
    // dropping the fixed delay appeared to disable this entirely.
    function step(header) {
        var wrapper = header.nextElementSibling;
        if (!wrapper || !wrapper.classList.contains('expand-wrapper')) return false;
        if (!wrapper.classList.contains('expanded')) return true;

        var card = header.parentElement;
        if (!card) return false;

        var sc = scrollerOf(card);
        var isDoc = sc === document.scrollingElement || sc === document.documentElement;
        var paneTop = isDoc ? 0 : sc.getBoundingClientRect().top;
        var paneBottom = isDoc ? window.innerHeight : sc.getBoundingClientRect().bottom;

        var r = card.getBoundingClientRect();
        var below = r.bottom - paneBottom;
        if (below > 1) {
            // Never past the card's own top: scrolling further would hide the header just clicked.
            var headroom = Math.max(0, r.top - paneTop);
            var by = Math.min(below, headroom);
            if (by > 0) sc.scrollTop += by;
        }
        return true;
    }

    document.addEventListener('click', function (e) {
        var header = e.target.closest && e.target.closest('.card-header-collapsible');
        if (!header) return;
        // A control living in the header - a filter pill, a link, a badge - does its own thing, so
        // on an already-open card there is nothing newly revealed to follow. On a CLOSED one the
        // same click may well open it, and then following is the whole point: filtering a card you
        // cannot see is the one case where the view should move. Decided from the target because
        // this listener runs in the CAPTURE phase, where a component's own stopPropagation cannot
        // reach it.
        var control = e.target.closest('button, a, select, input, label');
        var opened = header.nextElementSibling;
        var wasOpen = !!opened && opened.classList.contains('expanded');
        if (control && header.contains(control) && wasOpen) return;
        // Blazor re-renders before the transition starts, so begin on the next frame and run for a
        // little longer than the 0.25s expand to catch the final pixels.
        var until = performance.now() + FOLLOW_MS;
        requestAnimationFrame(function frame(now) {
            if (!step(header)) return;
            if (now < until) requestAnimationFrame(frame);
        });
    }, true);
})();
