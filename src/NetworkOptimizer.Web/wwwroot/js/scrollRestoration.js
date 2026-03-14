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
            const container = getScrollContainer();
            if (!container) return;

            // Reset top bar state on navigation - it's always visible at top
            if (window.innerWidth <= 768) {
                var topBar = document.querySelector('.top-bar');
                if (topBar) topBar.classList.remove('top-bar-hidden');
                container.style.scrollPaddingTop = '70px';
            }

            if (isPopState) {
                // Back/forward: restore saved position
                const saved = scrollPositions.get(path);
                container.scrollTop = saved !== undefined ? saved : 0;
                isPopState = false;
            } else if (!window.location.hash) {
                // Forward navigation: scroll to top
                container.scrollTop = 0;
            }
        }
    };
})();
