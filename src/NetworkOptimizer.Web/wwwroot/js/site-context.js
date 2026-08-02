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
function noHighlight(id, block, variant, radius) {
    if (!document.getElementById(id)) return;
    el.scrollIntoView({ behavior: 'smooth', block: block || 'start' });
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

// A table row: tinted, because an offset ring around a row collides with the rows either side.
window.noHighlightRow = function (id, block) { noHighlight(id, block || 'center', 'nav-highlight-row'); };

// Keeps a long-running card's top pinned to the view while it grows - upstream discovery adds
// sections for a minute or so, and without this the reader chases them down the page. Released the
// moment the user scrolls for themselves: wheel, touch and the scrolling keys are unambiguous
// intent, unlike a scroll event, which our own scrolling would also raise.
var anchorRelease = null;

window.noAnchorTop = function (id) {
    window.noAnchorRelease();
    var el = document.getElementById(id);
    if (!el) return;

    // Re-resolved every tick rather than captured: the card re-renders as discovery adds sections,
    // and Blazor can swap the element out. Holding the original node meant the pin let go the first
    // time that happened - one scroll, then nothing, which is exactly what it looked like.
    // PIN THE TOP: hold the card's top at the top of whatever actually scrolls, correcting every
    // tick, until the reader scrolls for themselves. Earlier attempts measured against
    // window.innerHeight, but this app scrolls .main-content / .page-content, not the document - so
    // the numbers were about the wrong box. Find the real scroller and work in its coordinates.
    var scrollerOf = function (node) {
        for (var n = node.parentElement; n && n !== document.body; n = n.parentElement) {
            var oy = getComputedStyle(n).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight + 4) return n;
        }
        return document.scrollingElement || document.documentElement;
    };

    var ticks = 0;
    var named = false;
    var keep = function () {
        var cur = document.getElementById(id);
        if (!cur) return false;
        var sc = scrollerOf(cur);
        var isDoc = sc === document.scrollingElement || sc === document.documentElement;
        var paneTop = isDoc ? 0 : sc.getBoundingClientRect().top;
        var drift = cur.getBoundingClientRect().top - paneTop;
        if (!named) {
            named = true;
            console.log('[anchor] scroller=' + (isDoc ? 'document' : (sc.tagName + '.' + sc.className))
                + ' scrollHeight=' + sc.scrollHeight + ' clientHeight=' + sc.clientHeight);
        }
        if (Math.abs(drift) > 2) {
            var before = sc.scrollTop;
            sc.scrollTop = before + drift;
            console.log('[anchor] drift=' + Math.round(drift)
                + ' scrollTop ' + Math.round(before) + ' -> ' + Math.round(sc.scrollTop));
        }
        return true;
    };
    var timer = setInterval(function () {
        ticks++;
        if (!keep()) console.log('[anchor] no element for ' + id + ' this tick (' + ticks + ')');
    }, 300);
    console.log('[anchor] pinning top of ' + id);

    var give = function () { console.log('[anchor] released by user scroll'); window.noAnchorRelease(); };
    var keys = { PageDown: 1, PageUp: 1, ArrowDown: 1, ArrowUp: 1, Home: 1, End: 1, ' ': 1 };
    var onKey = function (e) { if (keys[e.key]) give(); };
    window.addEventListener('wheel', give, { passive: true });
    window.addEventListener('touchmove', give, { passive: true });
    window.addEventListener('keydown', onKey, true);

    anchorRelease = function () {
        clearInterval(timer);
        window.removeEventListener('wheel', give);
        window.removeEventListener('touchmove', give);
        window.removeEventListener('keydown', onKey, true);
        anchorRelease = null;
    };
    keep();
};

window.noAnchorRelease = function () {
    if (anchorRelease) anchorRelease();
};
