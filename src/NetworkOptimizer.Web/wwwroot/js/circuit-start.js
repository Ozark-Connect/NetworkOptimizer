// First circuit start watchdog. Routes renders with prerender off, so the page is blank until the
// circuit starts, and Blazor's reconnect modal only covers a circuit that already existed. A start
// that fails is retried by reloading a few times, then the page says so and offers a Reload button.
// Success is reported by Routes.razor's first render, which calls window.noCircuitStarted.
(function () {
    const START_TIMEOUT_MS = 10000;          // no circuit by then: the start failed
    const REJECTION_GRACE_MS = 2000;         // a Blazor rejection before start fails it sooner
    const RETRY_DELAYS_MS = [1000, 3000, 5000];
    const RETRIES_KEY = 'no.circuitStartRetries';

    let started = false;
    let failed = false;
    let startTimer = null;
    let reloadTimer = null;

    function hasServerRoot() {
        const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_COMMENT);
        while (walker.nextNode()) {
            if (walker.currentNode.textContent.startsWith('Blazor:{"type":"server"')) return true;
        }
        return false;
    }

    // Storage that throws reports the retries as spent, so a page without it never reload-loops.
    function retriesUsed() {
        try { return parseInt(sessionStorage.getItem(RETRIES_KEY) || '0', 10) || 0; }
        catch { return RETRY_DELAYS_MS.length; }
    }

    function setRetriesUsed(n) {
        try {
            if (n > 0) sessionStorage.setItem(RETRIES_KEY, String(n));
            else sessionStorage.removeItem(RETRIES_KEY);
        } catch { }
    }

    function panel() {
        return document.getElementById('circuit-start-failed');
    }

    function show(state) {
        const p = panel();
        if (!p) return;
        p.classList.remove('retrying', 'failed');
        p.classList.add(state);
        if (state === 'failed') p.querySelector('button')?.focus();
    }

    function fail() {
        if (started || failed) return;
        failed = true;
        clearTimeout(startTimer);
        const used = retriesUsed();
        if (used < RETRY_DELAYS_MS.length) {
            setRetriesUsed(used + 1);
            show('retrying');
            reloadTimer = setTimeout(function () { location.reload(); }, RETRY_DELAYS_MS[used]);
            return;
        }
        // Spent: the Reload button (or F5) starts a fresh set of automatic retries.
        setRetriesUsed(0);
        show('failed');
    }

    // A late start still wins: it cancels a pending retry and clears the panel.
    window.noCircuitStarted = function () {
        started = true;
        clearTimeout(startTimer);
        clearTimeout(reloadTimer);
        setRetriesUsed(0);
        panel()?.classList.remove('retrying', 'failed');
    };

    function init() {
        if (started || !hasServerRoot()) return;
        const p = panel();
        p?.querySelector('button')?.addEventListener('click', function () { location.reload(); });
        // A reload in a retry sequence shows the retry state at once instead of a blank page.
        if (retriesUsed() > 0 && retriesUsed() <= RETRY_DELAYS_MS.length) show('retrying');
        startTimer = setTimeout(fail, START_TIMEOUT_MS);
        // A rejected StartCircuit or negotiate surfaces only as an unhandled rejection from Blazor's
        // own script. Other scripts' rejections are not a failed start.
        window.addEventListener('unhandledrejection', function (e) {
            if (started || failed) return;
            const stack = String((e.reason && e.reason.stack) || '');
            if (stack.includes('/_framework/blazor.web.js')) setTimeout(fail, REJECTION_GRACE_MS);
        });
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
