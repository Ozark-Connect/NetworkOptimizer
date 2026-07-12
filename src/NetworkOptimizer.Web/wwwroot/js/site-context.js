// Per-tab site context (multi-site). The server pins each Blazor circuit to the
// site in the tab's ?site= query parameter; this file keeps that selector alive
// on the browser side of the tab:
//  - noSiteContext.ensureSiteParam(slug), called by SiteTabSync after every
//    in-app navigation, re-stamps ?site= into the address bar via
//    history.replaceState so refresh / duplicate-tab / reconnect reloads land
//    back on this tab's site.
//  - a fetch wrapper and an anchor-click handler stamp the tab's site onto
//    same-origin /api/ requests (charts, PDF downloads, logout) so API endpoints
//    resolve the same site as the page issuing them. Requests that already carry
//    an explicit site parameter are left untouched.
//  - noSiteContext.stampUrl(url) is for scripts that trigger full-page
//    navigations themselves (e.g. the LAN flow maps) so those keep the pin too.
// Single-site instances never see a slug here, so every path below is a no-op.
(function () {
    let slug = new URLSearchParams(window.location.search).get('site');

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
