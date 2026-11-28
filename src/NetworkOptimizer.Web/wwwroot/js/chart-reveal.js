// A chart tab's loading overlay: up from mount until the page arms it (its deep-link framing is done) and the
// newest load started since has settled. View only: it never starts, orders, delays, or awaits a load.
// The container carries data-charts-ready once revealed; a later poll never hides it again, only hold() does.

export function createReveal() {
    let el = null;
    let started = 0;
    let settled = 0;
    let armed = false;

    const sync = () => {
        if (el && armed && settled >= started) el.setAttribute('data-charts-ready', '');
    };

    return {
        /** A new mount: overlay up, counts reset. */
        attach(container) {
            el = container;
            started = 0;
            settled = 0;
            armed = false;
            el?.removeAttribute('data-charts-ready');
        },
        /** Unmount. */
        detach() {
            el?.removeAttribute('data-charts-ready');
            el = null;
            armed = false;
        },
        /**
         * Counts a load without touching it: returns the same promise, so its caller's timing and errors are unchanged.
         * @param {Promise<any>} load
         */
        track(load) {
            const n = ++started;
            load.finally(() => {
                if (n > settled) settled = n;
                sync();
            }).catch(() => { });
            return load;
        },
        /** The page's framing is done: reveal once the newest load settles. */
        arm() {
            armed = true;
            sync();
        },
        /** Overlay back up until the next arm(), for framing the page runs after the first reveal. */
        hold() {
            armed = false;
            el?.removeAttribute('data-charts-ready');
        },
    };
}
