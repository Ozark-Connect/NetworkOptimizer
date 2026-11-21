// Per-tab site context (multi-site). The server pins each Blazor circuit to the
// site in the tab's ?site= query parameter; this file keeps that selector alive
// on the browser side of the tab:
//  - noSiteContext.ensureSiteParam(slug), called by SiteTabSync (multi-site only)
//    after every in-app navigation, re-stamps ?site= into the address bar via
//    history.replaceState and remembers the slug in per-tab sessionStorage so
//    refresh / duplicate-tab / reconnect reloads stay on this tab's site.
//  - a fetch wrapper and an anchor-click handler stamp the tab's site onto
//    same-origin /api/ requests (charts, PDF downloads, logout) so API endpoints
//    resolve the same site as the page issuing them.
//  - noSiteContext.stampUrl(url) is for scripts that trigger full-page
//    navigations themselves (e.g. the LAN flow maps) so those keep the pin too.
//  - a load-time backstop: if a navigation dropped ?site= (a spot that forgot to
//    stamp it), restore this tab's remembered site with a one-shot redirect
//    instead of silently falling back to the browser-default cookie.
//
// Single-site safety: sessionStorage is written ONLY by ensureSiteParam, which
// only runs when multi-site is enabled. So on a single-site instance nothing is
// ever remembered, the backstop never fires, and every path below (all guarded
// on a truthy slug) is a no-op.
(function () {
    const STORAGE_KEY = 'no-site-tab';

    function remembered() {
        try { return sessionStorage.getItem(STORAGE_KEY) || ''; } catch (e) { return ''; }
    }
    function remember(s) {
        try { if (s) sessionStorage.setItem(STORAGE_KEY, s); } catch (e) { }
    }
    function cookieSite() {
        const m = document.cookie.match(/(?:^|; )no-site=([^;]*)/);
        return m ? decodeURIComponent(m[1]) : (window.__noSiteDefault || '');
    }

    let slug = new URLSearchParams(window.location.search).get('site');

    // Backstop (see header). A navigation with no ?site= would resolve the
    // browser-default cookie server-side; if this tab is pinned to a different
    // site, restore it. Only fires when a site was remembered (so never on
    // single-site) and it differs from the default, so default-site tabs never
    // pay a reload; the redirect target carries ?site= so it can never loop.
    if (!slug) {
        const want = remembered();
        if (want && want !== cookieSite()) {
            const url = new URL(window.location.href);
            url.searchParams.set('site', want);
            window.location.replace(url.href);
            return;
        }
    }

    function stamp(rawUrl) {
        if (!slug)
            return rawUrl;
        try {
            const url = new URL(rawUrl, window.location.origin);
            if (url.origin !== window.location.origin || url.searchParams.has('site'))
                return rawUrl;
            url.searchParams.set('site', slug);
            return typeof rawUrl === 'string' && !rawUrl.startsWith(url.origin)
                ? url.pathname + url.search + url.hash
                : url.href;
        } catch (e) {
            return rawUrl;
        }
    }

    window.noSiteContext = {
        ensureSiteParam: function (s) {
            slug = s;
            remember(s);
            const url = new URL(window.location.href);
            if (url.searchParams.get('site') === s)
                return;
            url.searchParams.set('site', s);
            history.replaceState(history.state, '', url);
        },
        stampUrl: stamp
    };

    // Anchors to /api/ (PDF download, logout) bypass both Blazor and fetch - the
    // browser issues a plain document request. Rewrite the href at click time so
    // the request carries the tab's site.
    document.addEventListener('click', function (e) {
        if (!slug)
            return;
        const link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.origin !== window.location.origin || !link.pathname.startsWith('/api/'))
            return;
        link.href = stamp(link.href);
    }, true);

    // Site-switch links (the header dropdown and the /sites cards). A plain left
    // click switches this tab and makes the site the browser default; ctrl / cmd /
    // shift / middle clicks fall through to the browser so the link's ?site= href
    // opens in a new tab - pinning that tab without touching this one or the default.
    // Handled here rather than with a Blazor @onclick so a modified click's native
    // new-tab is never preventDefaulted, and so the switch stays a plain document
    // navigation (window.open from a Blazor Server handler would be popup-blocked).
    document.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey)
            return;
        const link = e.target.closest && e.target.closest('a[data-site-switch]');
        if (!link)
            return;
        e.preventDefault();
        if (link.hasAttribute('data-site-current')) {
            // Plain click on the already-active site. Two hosts carry these anchors:
            //  - the header dropdown, where a backdrop is present: just close the
            //    dropdown (don't reload). Clicking the backdrop keeps Blazor's open
            //    state in sync (its @onclick runs CloseDropdown).
            //  - the /sites page, where there's no dropdown: navigate to the card's
            //    href (the site's dashboard) instead of dead-clicking.
            // Reached only for plain left clicks - the modified-click guard above already
            // returned, so ctrl / cmd / shift / middle clicks still open the site in a new
            // tab untouched.
            const backdrop = document.querySelector('.site-switcher-backdrop');
            if (backdrop) {
                backdrop.click();
            } else {
                window.location.assign(link.href);
            }
            return;
        }
        const target = link.getAttribute('data-site-switch');
        document.cookie = 'no-site=' + target + '; path=/; max-age=31536000; SameSite=Lax';
        window.location.assign(link.href);
    }, false);

    // Keyboard navigation for the header site switcher menu: Down/Up move focus
    // through the items (Down from the trigger enters the list), Enter activates
    // natively (anchor click lands in the site-switch handler above), and Space
    // activates too per the ARIA menu pattern - menu items respond to both keys
    // even though bare links are Enter-only. Escape close lives in the Blazor
    // component, which owns the open state.
    document.addEventListener('keydown', function (e) {
        const host = e.target.closest && e.target.closest('.site-switcher');
        if (!host)
            return;
        const menu = host.querySelector('.site-switcher-menu');
        if (!menu)
            return;
        const items = Array.from(menu.querySelectorAll('a[href]'));
        if (items.length === 0)
            return;
        const idx = items.indexOf(document.activeElement);
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            const next = e.key === 'ArrowDown'
                ? (idx + 1) % items.length
                : (idx <= 0 ? items.length - 1 : idx - 1);
            items[next].focus();
        } else if (e.key === ' ' && idx >= 0) {
            e.preventDefault();
            items[idx].click();
        }
    }, false);

    const originalFetch = window.fetch;
    window.fetch = function (input, init) {
        try {
            if (slug) {
                const raw = typeof input === 'string' ? input : (input instanceof URL ? input.href : null);
                if (raw !== null) {
                    const url = new URL(raw, window.location.origin);
                    if (url.origin === window.location.origin && url.pathname.startsWith('/api/'))
                        input = typeof input === 'string' ? stamp(raw) : new URL(stamp(url.href));
                }
            }
        } catch (e) {
            // Malformed input: let the original fetch produce the real error.
        }
        return originalFetch.call(this, input, init);
    };
})();

// Scroll to a card and ring it briefly, the one behavior every in-page jump in Monitoring shares
// (findings, advisories, an event count). Kept here because this file already loads app-wide and
// the callers are spread across pages and components.
// A landing scrolls to what the URL names, so a Back restore of the remembered position stands down.
function noYieldRestore() {
    if (window.scrollRestoration && window.scrollRestoration.yieldToLanding) window.scrollRestoration.yieldToLanding();
}

function noHighlight(id, block, variant, radius) {
    var el = document.getElementById(id);
    if (!el) return;
    noYieldRestore();
    // false: ring it where it is, the caller has scrolled already.
    if (block !== false) el.scrollIntoView({ behavior: 'smooth', block: block || 'start' });
    // The caller says what shape it is pointing at; the default suits a card.
    if (radius) el.style.setProperty('--nav-highlight-radius', radius);
    el.classList.add(variant);
    // Next frame, so the class that defines the transition is applied before the color changes -
    // set together, the browser has nothing to animate from.
    requestAnimationFrame(function () {
        el.classList.add('nav-highlight-on');
        setTimeout(function () {
            el.classList.remove('nav-highlight-on');
            // Take the rest off once the fade has run, rather than leaving it on the element for
            // good: the base class carries a transition and a radius override, so a highlighted
            // table row would keep fading its hover afterwards and a wrapper would keep the
            // corners the ring gave it.
            setTimeout(function () {
                el.classList.remove(variant);
                el.style.removeProperty('--nav-highlight-radius');
            }, 1000);
        }, 1800);
    });
}

// A card or section: ringed. Pass a CSS length as `radius` for a target that is not card-shaped,
// e.g. "var(--border-radius)" for a form or control inside a card.
window.noHighlightTarget = function (id, block, radius) { noHighlight(id, block, 'nav-highlight', radius); };

// A link landing on a chart in a card whose header is sticky on a desktop (.chart-card-sticky-header): there the
// header stays in view, so the chart is centered. Narrower, the header does not stick, so the card's top lands at the
// top (its controls and the way back in reach) and the chart is ringed where it is. quiet: no ring, for a jump the
// reader made from a card about this very chart (Live View's Analyze), where it says nothing.
window.noLandOnChart = function (cardId, chartId, quiet) {
    var narrow = window.matchMedia('(max-width: 1024px)').matches;
    var target = document.getElementById(narrow ? cardId : chartId);
    if (target) noYieldRestore();
    if (quiet) {
        if (target) target.scrollIntoView({ behavior: 'smooth', block: narrow ? 'start' : 'center' });
    } else if (narrow) {
        if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        noHighlight(chartId, false, 'nav-highlight');
    } else {
        noHighlight(chartId, 'center', 'nav-highlight');
    }
};

// Where the reader was on a tab they jump away from and back to (Live View, Analyze, Watch in Live View). A page's tabs
// share one scroll box, so without this the jump back lands at the depth reached on the other tab. Saved per tab
// for the session as the card at the middle of the view and its distance from the top, not a raw offset: the cards
// above load and grow after the return, and an offset drifted further down on every round trip. Restored by keeping
// that card there until the layout holds still; with nothing saved, the top. A touch, wheel, or key stops it.
// The loss event stepper sits centered between a chart header's scope pills and its controls only while all three
// share a line: once the header wraps (many WANs, a narrow window), it takes its own line instead, and the controls
// keep their place on the right. CSS cannot tell a wrapped row from a full one, so this measures, on every resize.
window.noLossNavFit = function (header) {
    if (typeof header === 'string') header = document.querySelector(header);
    if (!header) return;
    function fit() {
        var slot = header.querySelector('.loss-nav-slot');
        if (!slot) return;
        slot.classList.remove('loss-nav-own-line');
        var before = header.querySelector('.wan-selector') || header.querySelector('.card-title');
        var after = header.querySelector('.chart-header-controls');
        var mid = function (el) { var r = el.getBoundingClientRect(); return r.top + r.height / 2; };
        var m = mid(slot);
        var shared = (!before || Math.abs(mid(before) - m) < 10) && (!after || Math.abs(mid(after) - m) < 10);
        if (!shared) slot.classList.add('loss-nav-own-line');
    }
    if (!header._noLossNavFit) {
        header._noLossNavFit = new ResizeObserver(fit);
        header._noLossNavFit.observe(header);
    }
    fit();
};

window.noTabScroll = {
    save: function (key) {
        var c = window.scrollRestoration && window.scrollRestoration.container();
        if (!c) return;
        var box = c.getBoundingClientRect();
        var cards = Array.prototype.slice.call(c.querySelectorAll('.card'));
        var mid = box.top + Math.min(box.height, window.innerHeight) / 2;
        var index = cards.findIndex(function (el) { var r = el.getBoundingClientRect(); return r.top <= mid && r.bottom >= mid; });
        if (index < 0) index = cards.findIndex(function (el) { return el.getBoundingClientRect().bottom > box.top; });
        var saved = { top: c.scrollTop };
        if (index >= 0) {
            var el = cards[index];
            // By name where the card has one: a card above it that renders late shifts every index after it.
            saved.sel = el.id ? '#' + CSS.escape(el.id)
                : el.dataset.tour ? '.card[data-tour="' + CSS.escape(el.dataset.tour) + '"]' : null;
            saved.card = index;
            saved.offset = el.getBoundingClientRect().top - box.top;
        }
        try { sessionStorage.setItem('no.tabScroll.' + key, JSON.stringify(saved)); } catch (e) { }
    },
    restore: function (key) {
        var saved = null;
        try {
            saved = JSON.parse(sessionStorage.getItem('no.tabScroll.' + key) || 'null');
            sessionStorage.removeItem('no.tabScroll.' + key);
        } catch (e) { }
        noYieldRestore();
        var started = Date.now(), timer = null, steadySince = 0, lastTop = null;
        var events = ['touchstart', 'wheel', 'pointerdown', 'keydown'];
        function stop() {
            clearInterval(timer);
            events.forEach(function (e) { window.removeEventListener(e, stop, true); });
        }
        function apply() {
            var c = window.scrollRestoration && window.scrollRestoration.container();
            var now = Date.now();
            if (now - started >= 4000) { stop(); return; }
            if (!c) return;
            var want = 0;
            if (saved && saved.card != null) {
                var el = (saved.sel && c.querySelector(saved.sel)) || c.querySelectorAll('.card')[saved.card];
                if (!el) return;
                want = c.scrollTop + (el.getBoundingClientRect().top - c.getBoundingClientRect().top) - saved.offset;
            } else if (saved) {
                want = saved.top || 0;
            }
            want = Math.max(0, Math.min(want, c.scrollHeight - c.clientHeight));
            if (Math.abs(c.scrollTop - want) > 1) c.scrollTop = want;
            // Done once the card has held its place for half a second: the tab has finished growing above it.
            if (lastTop !== null && Math.abs(want - lastTop) <= 1) {
                if (!steadySince) steadySince = now;
                else if (now - steadySince >= 500) stop();
            } else {
                steadySince = 0;
            }
            lastTop = want;
        }
        events.forEach(function (e) { window.addEventListener(e, stop, { capture: true, passive: true }); });
        timer = setInterval(apply, 100);
        apply();
    },
};

// A table row: tinted, because an offset ring around a row collides with the rows either side.
window.noHighlightRow = function (id, block) { noHighlight(id, block || 'center', 'nav-highlight-row'); };

// A link from another page landing on a row (a speed test ?result=): the charts, map, and cards above it are still
// growing when it renders, so one scroll lands short. Centered at once, then held there while they settle, then tinted.
window.noLandOnRow = function (id) {
    var el = document.getElementById(id);
    if (!el) return;
    noYieldRestore();
    el.scrollIntoView({ behavior: 'instant', block: 'center' });
    window.noHoldScroll(id, 3000);
    noHighlight(id, false, 'nav-highlight-row');
};

// Scroll with no ring, for something that appeared where the reader clicked - a form opening
// under its own button. They know what arrived, so flagging it says nothing.
//
// Ring it whenever the page MOVED to get there: another page, or another card on this one. A
// click in one card that scrolls to a different card has to say which card to work in now,
// and "the user asked for it" does not answer that.
window.noScrollTo = function (id, block) {
    var el = document.getElementById(id);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: block || 'start' });
};

// Like noScrollTo with 'start', but never further than puts the end of what matters in it (`endSelector`
// inside it, else its own bottom) at the bottom of the view: past that the view shows only empty space or
// the next card. Already in view whole: no scroll.
window.noScrollToEnd = function (id, endSelector) {
    var el = document.getElementById(id);
    if (!el) return;
    var end = (endSelector && el.querySelector(endSelector)) || el;
    var box = el.parentElement;
    while (box && box !== document.body && !(box.scrollHeight > box.clientHeight
        && /(auto|scroll)/.test(getComputedStyle(box).overflowY))) box = box.parentElement;
    var scroller = box && box !== document.body ? box : document.scrollingElement;
    var view = scroller === document.scrollingElement
        ? { top: 0, bottom: window.innerHeight }
        : scroller.getBoundingClientRect();
    var margin = parseFloat(getComputedStyle(el).scrollMarginTop) || 0;
    var toStart = el.getBoundingClientRect().top - view.top - margin;
    var toEnd = end.getBoundingClientRect().bottom - view.bottom + 16;
    var delta = toStart > 0 ? Math.max(0, Math.min(toStart, toEnd)) : toStart;
    if (delta !== 0) scroller.scrollBy({ top: delta, behavior: 'smooth' });
};

// Holds an element at the same place on screen while something ABOVE it changes height, for a
// control the user is about to press again. Filtering the Latency Targets bar re-picks the chart
// series above it, and the statistics table that grows or shrinks with them drags the whole card -
// pills included - out from under the cursor. What the user is looking at is the fixed point here,
// not the scroll offset, so the page moves to keep the element still rather than the other way
// round. Scroll-anchoring the browser does on its own stops at the first layout; the table settles
// over several, once the new series have loaded, so this watches for a window.
//
// A call for an element already held extends that hold instead of starting another: two loops
// would each correct the same shift. Call it BEFORE the change, since it measures at the call.
var noActiveHolds = {};
window.noHoldScroll = function (id, windowMs) {
    var active = noActiveHolds[id];
    if (active) {
        active.until = Math.max(active.until, performance.now() + (windowMs || 1500));
        return;
    }
    var el = document.getElementById(id);
    if (!el) return;
    // Off screen it is not what the reader is looking at, so holding it would move what is.
    var rect = el.getBoundingClientRect();
    if (rect.bottom <= 0 || rect.top >= window.innerHeight) return;
    // The page does not scroll the window. .page-content owns the scrollbar on desktop and
    // .main-content does on mobile, so window.scrollBy moves nothing and does not error either -
    // it just silently does nothing at all. scrollRestoration already resolves which of the two it
    // is, so it is asked rather than the rule being written out a second time here.
    var box = window.scrollRestoration && window.scrollRestoration.container
        ? window.scrollRestoration.container()
        : null;
    var startTop = rect.top;
    var hold = { until: performance.now() + (windowMs || 1500) };
    noActiveHolds[id] = hold;
    var released = false;
    function release() { released = true; }
    // The user's own scroll wins outright - they have just moved the fixed point themselves. Only
    // input events, never 'scroll', which our own compensation raises.
    window.addEventListener('wheel', release, { passive: true });
    window.addEventListener('touchmove', release, { passive: true });
    window.addEventListener('keydown', release);
    function tick(now) {
        // Gone from the page (the reader navigated away): a detached element measures at 0, so holding
        // it would scroll whatever page came next.
        if (released || now >= hold.until || !el.isConnected) {
            delete noActiveHolds[id];
            window.removeEventListener('wheel', release);
            window.removeEventListener('touchmove', release);
            window.removeEventListener('keydown', release);
            return;
        }
        var delta = el.getBoundingClientRect().top - startTop;
        if (Math.abs(delta) >= 1) {
            // scrollTop assignment rather than scrollBy: scroll-behavior: smooth is set on
            // html/body, and an animated correction would still be running when the next frame
            // measures, so each frame would chase the last one instead of settling.
            if (box) box.scrollTop += delta;
            else window.scrollBy({ top: delta, left: 0, behavior: 'instant' });
        }
        requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
};
