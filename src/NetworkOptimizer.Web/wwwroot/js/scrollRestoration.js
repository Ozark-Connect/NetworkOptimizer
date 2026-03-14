// Scroll Restoration for Blazor Server
// Mobile uses .main-content as scroll container, desktop uses .page-content

(function() {
    const scrollPositions = new Map();
    let isPopState = false;

    // Detect back/forward navigation
    window.addEventListener('popstate', function() {
        isPopState = true;
    });

    function getScrollContainer() {
        if (window.innerWidth <= 768) return document.querySelector('.main-content');
        return document.querySelector('.page-content');
    }

    // Called from C# before navigation
    window.scrollRestoration = {
        savePosition: function(path) {
            const container = getScrollContainer();
            if (container) {
                scrollPositions.set(path, container.scrollTop);
            }
        },

        // Called from C# after navigation
        restoreOrScrollToTop: function(path) {
            var container = getScrollContainer();
            if (!container) return;
            var hasFragment = !!window.location.hash;

            if (isPopState) {
                var saved = scrollPositions.get(path);
                container.scrollTop = saved !== undefined ? saved : 0;
                isPopState = false;
                return;
            }

            if (hasFragment) {
                // Fragment navigation: hide nav bar, no scroll padding, let browser scroll to fragment
                if (window.__setScrollState) window.__setScrollState(true);
            } else {
                // Page navigation: show nav bar, scroll to top
                if (window.__setScrollState) window.__setScrollState(false);
                container.scrollTop = 0;
            }
        }
    };
})();
