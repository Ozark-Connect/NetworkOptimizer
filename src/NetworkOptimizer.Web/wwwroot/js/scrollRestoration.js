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
            var popState = isPopState;
            isPopState = false;

            // Notify scroll listener to reset its state
            if (window.__resetScrollState) window.__resetScrollState();

            requestAnimationFrame(function() {
                var container = getScrollContainer();
                if (!container) return;

                if (popState) {
                    var saved = scrollPositions.get(path);
                    container.scrollTop = saved !== undefined ? saved : 0;
                } else if (!window.location.hash) {
                    container.scrollTop = 0;
                }
            });
        }
    };
})();
