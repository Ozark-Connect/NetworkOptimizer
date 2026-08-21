// Wait for a chart tab's container before mounting into it.
//
// Blazor can call mount() before it has rendered the tab's markup - switching site while already
// on a chart tab is what does it. Every module read getElementById once and returned on null, and
// nothing ever re-mounted, so the tab stayed blank with no chart cards at all: no headers, no
// reserved space. Waiting a moment for the element costs nothing in the normal case, where it is
// already there and this resolves without yielding.
//
// Resolves null if it never appears, so callers keep their existing early return.
export function awaitContainer(elId, timeoutMs = 5000) {
    const present = document.getElementById(elId);
    if (present) return Promise.resolve(present);

    return new Promise(resolve => {
        let settled = false;
        const finish = el => {
            if (settled) return;
            settled = true;
            observer.disconnect();
            clearTimeout(timer);
            resolve(el);
        };
        const observer = new MutationObserver(() => {
            const el = document.getElementById(elId);
            if (el) finish(el);
        });
        observer.observe(document.body, { childList: true, subtree: true });
        const timer = setTimeout(() => finish(document.getElementById(elId)), timeoutMs);
    });
}
