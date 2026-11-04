// The viewer's clock and format for times drawn in the browser: charts, their tooltips, and their
// range pickers.
//
// The server renders every table and label on the viewer's clock and format (IDisplayTimeZone), and
// the layout writes both into #no-display-tz: the zone's IANA id and the format code ("12-mdy",
// "24-dmy"). A browser formats on its own zone and locale unless told otherwise, so without this a
// chart and the table beside it can disagree.
//
// zone() is undefined while the viewer's zone matches the browser's: conversions then use the
// browser's own clock, as before.
(function () {
    const browserZone = (() => {
        try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return undefined; }
    })();

    // What this browser's locale reads: 12- or 24-hour, month or day first.
    const browserFormat = (() => {
        try {
            const cycle = new Intl.DateTimeFormat(undefined, { hour: 'numeric' }).resolvedOptions().hourCycle;
            const parts = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' })
                .formatToParts(new Date(2026, 10, 3)).map(p => p.type);
            const clock = cycle === 'h12' || cycle === 'h11' ? '12' : '24';
            return `${clock}-${parts.indexOf('day') < parts.indexOf('month') ? 'dmy' : 'mdy'}`;
        } catch { return '24-mdy'; }
    })();

    // The server reads it when the circuit opens, so a viewer with no choice of their own gets
    // their browser's format on the very first page.
    try {
        document.cookie = `no-dtf=${browserFormat}; path=/; max-age=31536000; SameSite=Lax`;
        // Offered in the time zone select as "This browser"; IANA ids need no escaping.
        if (browserZone) document.cookie = `no-btz=${browserZone}; path=/; max-age=31536000; SameSite=Lax`;
    } catch { /* cookies blocked: the server's fallback format stands */ }

    const marker = () => document.getElementById('no-display-tz');

    function zone() {
        const id = marker()?.dataset.tz;
        if (!id || id === browserZone) return undefined;
        // An id this browser's Intl does not know falls back to the browser's zone, not an exception.
        try { new Intl.DateTimeFormat(undefined, { timeZone: id }); } catch { return undefined; }
        return id;
    }

    function format() {
        const code = marker()?.dataset.format || browserFormat;
        return { h24: !code.startsWith('12'), dayFirst: code.endsWith('dmy') };
    }

    const formatters = new Map();
    function formatter(options) {
        const z = zone();
        const key = (z || '') + '|' + JSON.stringify(options || {});
        let f = formatters.get(key);
        if (!f) {
            f = new Intl.DateTimeFormat('en-US', z ? { ...(options || {}), timeZone: z } : (options || {}));
            formatters.set(key, f);
        }
        return f;
    }

    // A toLocale*String call in the viewer's format: day-first reads as en-GB, month-first as en-US,
    // and the hour cycle is the viewer's whatever the caller asked for.
    function local(method, value, options) {
        const { h24, dayFirst } = format();
        const z = zone();
        const opts = { ...(options || {}), hourCycle: h24 ? 'h23' : 'h12' };
        delete opts.hour12;
        if (z) opts.timeZone = z;
        return new Date(value)[method](dayFirst ? 'en-GB' : 'en-US', opts);
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
    const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    // The same forms DisplayFormat gives the server: "Nov 3" / "3 Nov", "14:05" / "2:05 PM".
    function dayText(p) {
        return format().dayFirst ? `${p.day} ${MONTHS[p.month - 1]}` : `${MONTHS[p.month - 1]} ${p.day}`;
    }
    function timeText(p, seconds) {
        const sec = seconds ? `:${pad(p.second)}` : '';
        if (format().h24) return `${pad(p.hour)}:${pad(p.minute)}${sec}`;
        const h = p.hour % 12 === 0 ? 12 : p.hour % 12;
        return `${h}:${pad(p.minute)}${sec} ${p.hour < 12 ? 'AM' : 'PM'}`;
    }

    /**
     * An x-axis tick: the date at midnight, else the time. ApexCharts places ticks on the browser's
     * hours; while the viewer's zone differs, a multi-day window labels every tick with its date,
     * since those ticks no longer fall on the viewer's midnights.
     */
    function axisLabel(timestamp, opts) {
        const p = parts(timestamp);
        const w = opts?.w?.globals;
        const spanMs = w ? (w.maxX - w.minX) : 0;
        if ((p.hour === 0 && p.minute === 0) || (zone() && spanMs > 3 * 86400000)) return dayText(p);
        return timeText(p, false);
    }

    /** A tooltip header: "Nov 3, 14:05[:09]", or the time alone. */
    function tooltipLabel(value, seconds = true, withDate = true) {
        const p = parts(value);
        return withDate ? `${dayText(p)}, ${timeText(p, seconds)}` : timeText(p, seconds);
    }

    window.noTime = {
        zone,

        /** The viewer's format: { h24, dayFirst }. */
        format,

        /** A time alone in the viewer's format: "14:05[:09]" or "2:05[:09] PM". */
        timeLabel: (value, seconds = true) => timeText(parts(value), seconds),

        /** toLocaleString on the viewer's clock and format. */
        string: (value, options) => local('toLocaleString', value, options),
        /** toLocaleTimeString on the viewer's clock and format. */
        time: (value, options) => local('toLocaleTimeString', value, options),
        /** toLocaleDateString on the viewer's clock and format. */
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
            if (!m || !zone()) return new Date(text);
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

        /** An ApexCharts xaxis.labels.formatter in the viewer's clock and format. */
        axisFormatter: () => (value, timestamp, opts) => axisLabel(timestamp ?? value, opts),

        /** An ApexCharts tooltip.x.formatter in the viewer's clock and format. */
        tooltipFormatter: (seconds = true) => value => tooltipLabel(value, seconds),

        // For chart options serialized from C# (ChartTimeFormat).
        axisLabel,
        tooltipLabel,
        /** A day on its own: "Nov 3" or "3 Nov". */
        dateLabel: value => dayText(parts(value)),
    };
})();
