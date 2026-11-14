// Fits a speed test trace (SpeedTestDetails) to its container: measures the rendered hops and
// link labels, breaks the trace into rows that fit, and picks how each row turns into the next.
// The component renders its first pass from a character estimate; this corrects it and keeps it
// correct on resize. Below 769px the trace stacks vertically and nothing here applies.
window.traceFit = (function () {
    const GAP = 8;              // .path-row gap
    const MAX_HOPS = 6;         // the component's own row limits, so a wide page breaks where it did
    const MAX_CHARS = 142;
    const HOOK_STUB = 50;       // a hooked turn's shortest top segment, with its corner inset
    const HOOK_ENTRY = 62;      // how far a hooked turn pushes the next row's first hop inward
    const TURN_ROOMY = 0, TURN_HOOK = 1, TURN_HOOK_LABEL_BELOW = 2;

    const watched = new WeakMap();

    function measure(el) {
        const hops = [], links = [];
        el.querySelectorAll('.path-hop[data-hop]').forEach(h => {
            hops[+h.dataset.hop] = { width: h.offsetWidth, chars: (h.querySelector('.hop-name')?.textContent.length || 0) + 9 };
        });
        el.querySelectorAll('.path-connector[data-link]').forEach(c => {
            const label = c.querySelector('.connector-speed');
            links[+c.dataset.link] = Math.max(label ? label.offsetWidth : 0, 24) + 7;
        });
        const style = getComputedStyle(el);
        const width = el.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight) - 4;
        return { hops, links, width };
    }

    function fit(m) {
        const n = m.hops.length;
        const starts = [0], turns = [];
        let i = 0, entry = 0;
        while (i < n) {
            let used = entry + m.hops[i].width, chars = m.hops[i].chars, j = i;
            while (j + 1 < n) {
                const next = used + (m.links[j] || 31) + 2 * GAP + m.hops[j + 1].width;
                const last = j + 2 === n;
                const fits = next + (last ? 0 : GAP + HOOK_STUB) <= m.width;
                if (!fits || j + 1 - i >= MAX_HOPS || chars + m.hops[j + 1].chars > MAX_CHARS) break;
                used = next; chars += m.hops[j + 1].chars; j++;
            }
            if (j + 1 >= n) break;
            // The turn out of this row. Roomy: the top segment carries the label and the corner
            // drops onto the next row's first hop. Otherwise it hooks around the row's end.
            const free = m.width - used - GAP;
            const label = m.links[j] || 31;
            const spare = free - label;
            const cornerInset = 0.3 * spare - 8;
            const lands = cornerInset >= 34 && cornerInset <= 24 + m.hops[j + 1].width - 10;
            const turn = lands ? TURN_ROOMY : (free >= label + 44 ? TURN_HOOK : TURN_HOOK_LABEL_BELOW);
            turns.push(turn);
            entry = turn === TURN_ROOMY ? 0 : HOOK_ENTRY;
            i = j + 1;
            starts.push(i);
        }
        return { starts, turns };
    }

    function run(el) {
        const state = watched.get(el);
        if (!state || !el.isConnected) return;
        if (window.matchMedia('(max-width: 768px)').matches) return;
        const m = measure(el);
        if (m.hops.length === 0 || m.width <= 0 || m.hops.includes(undefined)) return;
        const result = fit(m);
        const key = result.starts.join(',') + '|' + result.turns.join(',');
        if (key === el.dataset.fit) return;
        // A page scrollbar that appears or goes as the trace changes height can flip the fit
        // back and forth. Stop after a few flips inside a short window.
        const now = Date.now();
        state.sent = state.sent.filter(t => now - t < 2000);
        if (state.sent.length >= 4) return;
        state.sent.push(now);
        state.ref.invokeMethodAsync('SetTraceFit', result.starts, result.turns).catch(() => { });
    }

    function schedule(el) {
        const state = watched.get(el);
        if (!state) return;
        clearTimeout(state.timer);
        state.timer = setTimeout(() => run(el), 80);
    }

    return {
        /** Starts fitting a .path-visualization element; ref is the component's DotNetObjectReference. */
        watch(el, ref) {
            if (!el) return;
            this.unwatch(el);
            const state = { ref, sent: [], timer: 0 };
            state.resize = new ResizeObserver(() => schedule(el));
            state.resize.observe(el);
            state.mutation = new MutationObserver(() => schedule(el));
            state.mutation.observe(el, { childList: true, subtree: true, characterData: true });
            watched.set(el, state);
            if (document.fonts?.ready) document.fonts.ready.then(() => schedule(el));
            run(el);
        },
        unwatch(el) {
            const state = el && watched.get(el);
            if (!state) return;
            clearTimeout(state.timer);
            state.resize.disconnect();
            state.mutation.disconnect();
            watched.delete(el);
        },
        // Exposed for the layout harness.
        _fit: fit,
        _measure: measure,
    };
})();
