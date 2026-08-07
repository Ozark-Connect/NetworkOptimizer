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
    var el = document.getElementById(id);
    if (!el) return;
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

// Scroll with no ring, for something the user just caused to appear. The ring answers "which of
// these is the one you were sent to" - a question that only exists when a link brought you from
// somewhere else. A form that opened under the button you pressed needs no such answer, and
// flagging it would say something arrived that the user already knows they asked for.
window.noScrollTo = function (id, block) {
    var el = document.getElementById(id);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: block || 'start' });
};

// Holds an element at the same place on screen while something ABOVE it changes height, for a
// control the user is about to press again. Filtering the Latency Targets bar re-picks the chart
// series above it, and the statistics table that grows or shrinks with them drags the whole card -
// pills included - out from under the cursor. What the user is looking at is the fixed point here,
// not the scroll offset, so the page moves to keep the element still rather than the other way
// round. Scroll-anchoring the browser does on its own stops at the first layout; the table settles
// over several, once the new series have loaded, so this watches for a window.
window.noHoldScroll = function (id, windowMs) {
    var el = document.getElementById(id);
    if (!el) return;
    // The page does not scroll the window. .page-content owns the scrollbar on desktop and
    // .main-content does on mobile, so window.scrollBy moves nothing and does not error either -
    // it just silently does nothing at all. scrollRestoration already resolves which of the two it
    // is, so it is asked rather than the rule being written out a second time here.
    var box = window.scrollRestoration && window.scrollRestoration.container
        ? window.scrollRestoration.container()
        : null;
    var startTop = el.getBoundingClientRect().top;
    var until = performance.now() + (windowMs || 1500);
    var released = false;
    function release() { released = true; }
    // The user's own scroll wins outright - they have just moved the fixed point themselves. Only
    // input events, never 'scroll', which our own compensation raises.
    window.addEventListener('wheel', release, { passive: true });
    window.addEventListener('touchmove', release, { passive: true });
    window.addEventListener('keydown', release);
    function tick(now) {
        if (released || now >= until) {
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
