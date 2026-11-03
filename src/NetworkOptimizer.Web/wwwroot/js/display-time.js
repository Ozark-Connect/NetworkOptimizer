// The viewer's clock for times drawn in the browser: charts, their tooltips, and their range pickers.
//
// The server renders every table and label on the viewer's clock (IDisplayTimeZone), and the layout
// writes that zone's IANA id into #no-display-tz. A browser formats on its own zone unless told
// otherwise, so a chart and the table beside it can disagree whenever the two differ.
//
// zone() is undefined while the viewer's zone matches the browser's, which is nearly always: every
// helper then falls through to exactly what the browser did before, and the chart options that take
// a formatter get none, so ApexCharts keeps its own tick and tooltip formatting.
(function () {
    const browserZone = (() => {
        try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return undefined; }
    })();

    function zone() {
        const id = document.getElementById('no-display-tz')?.dataset.tz;
        if (!id || id === browserZone) return undefined;
        // An id this browser's Intl does not know falls back to the browser's zone, not an exception.
        try { new Intl.DateTimeFormat(undefined, { timeZone: id }); } catch { return undefined; }
        return id;
    }

    const formatters = new Map();
    function formatter(options) {
        const z = zone();
        const key = (z || '') + '|' + JSON.stringify(options || {});
        let f = formatters.get(key);
        if (!f) {
            f = new Intl.DateTimeFormat(undefined, z ? { ...(options || {}), timeZone: z } : (options || {}));
            formatters.set(key, f);
        }
        return f;
    }

    // The same Date method the caller used, so a matching zone changes nothing about the output.
    function local(method, value, options) {
        const z = zone();
        return new Date(value)[method](undefined, z ? { ...(options || {}), timeZone: z } : options);
    }

    /** Wall-clock fields of an instant on the viewer's clock, 24-hour. */
    function parts(value) {
        const out = {};
        for (const p of formatter({ year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' })
            .formatToParts(new Date(value))) {
            if (p.type !== 'literal') out[p.type] = Number(p.value);
        }
        if (out.hour === 24) out.hour = 0;
        return out;
    }

    /** The viewer's UTC offset at an instant, in milliseconds. */
    function offsetMs(value) {
        const t = new Date(value).getTime();
        const p = parts(t);
        const asUtc = Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second);
        return asUtc - Math.floor(t / 1000) * 1000;
    }

    /** The instant of a wall-clock time on the viewer's clock. */
    function fromWallClock(year, month, day, hour, minute) {
        const guess = Date.UTC(year, month - 1, day, hour || 0, minute || 0);
        // Two passes settle the offset either side of a DST change.
        let t = guess - offsetMs(guess);
        t = guess - offsetMs(t);
        return t;
    }

    const pad = n => String(n).padStart(2, '0');

    // ApexCharts prints English month names whatever the browser's locale, so these do too: a label
    // on a matching zone reads exactly as ApexCharts' own datetimeFormatter would print it.
    const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    const dayLabel = (p, dayFormat) => dayFormat === 'dd MMM'
        ? `${pad(p.day)} ${MONTHS[p.month - 1]}` : `${MONTHS[p.month - 1]} ${pad(p.day)}`;

    /**
     * An x-axis tick as ApexCharts' datetimeFormatter { hour: 'HH:mm', day: dayFormat } prints it,
     * on the viewer's clock. ApexCharts places ticks on the browser's hours; while the viewer's zone
     * differs, a multi-day window labels every tick with its date, since those ticks no longer fall
     * on the viewer's midnights.
     */
    function axisLabel(timestamp, opts, dayFormat = 'MMM dd') {
        const p = parts(timestamp);
        const w = opts?.w?.globals;
        const spanMs = w ? (w.maxX - w.minX) : 0;
        if ((p.hour === 0 && p.minute === 0) || (zone() && spanMs > 3 * 86400000))
            return dayLabel(p, dayFormat);
        return `${pad(p.hour)}:${pad(p.minute)}`;
    }

    /** A tooltip header as ApexCharts' "MMM dd, HH:mm[:ss]" (or "HH:mm") prints it, on the viewer's clock. */
    function tooltipLabel(value, seconds = true, withDate = true) {
        const p = parts(value);
        const time = `${pad(p.hour)}:${pad(p.minute)}${seconds ? `:${pad(p.second)}` : ''}`;
        return withDate ? `${MONTHS[p.month - 1]} ${pad(p.day)}, ${time}` : time;
    }

    window.noTime = {
        zone,

        /** toLocaleString on the viewer's clock. */
        string: (value, options) => local('toLocaleString', value, options),
        /** toLocaleTimeString on the viewer's clock. */
        time: (value, options) => local('toLocaleTimeString', value, options),
        /** toLocaleDateString on the viewer's clock. */
        date: (value, options) => local('toLocaleDateString', value, options),

        parts,

        /** "2026-11-03T14:05" for a datetime-local input, on the viewer's clock. */
        inputValue(value) {
            const p = parts(value);
            return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`;
        },

        /** A datetime-local input's value read on the viewer's clock, as a Date. */
        fromInputValue(text) {
            const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(text || '');
            if (!m) return new Date(text);
            if (!zone()) return new Date(text);
            return new Date(fromWallClock(+m[1], +m[2], +m[3], +m[4], +m[5]));
        },

        /** The next midnight on the viewer's clock after an instant, in ms. */
        nextMidnight(value) {
            if (!zone()) {
                const d = new Date(value);
                d.setHours(24, 0, 0, 0);
                return d.getTime();
            }
            const p = parts(value);
            const next = new Date(Date.UTC(p.year, p.month - 1, p.day + 1));
            return fromWallClock(next.getUTCFullYear(), next.getUTCMonth() + 1, next.getUTCDate(), 0, 0);
        },

        /**
         * An ApexCharts xaxis.labels.formatter on the viewer's clock, or undefined while it matches
         * the browser's, so ApexCharts keeps its own datetimeFormatter.
         */
        axisFormatter: () => zone() ? (value, timestamp, opts) => axisLabel(timestamp ?? value, opts) : undefined,

        /**
         * An ApexCharts tooltip.x.formatter for "MMM dd, HH:mm[:ss]" on the viewer's clock, or
         * undefined while it matches the browser's, so the tooltip's own format applies.
         */
        tooltipFormatter: (seconds = true) => zone() ? value => tooltipLabel(value, seconds) : undefined,

        // For chart options serialized from C#, which cannot know the browser's zone: these always
        // format, and on a matching zone print what ApexCharts would.
        axisLabel,
        tooltipLabel,
    };
})();
