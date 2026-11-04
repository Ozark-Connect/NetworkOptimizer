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

    // Offsets change only at DST transitions, which fall on whole quarter hours in every zone, so one
    // read per quarter hour covers a series of any length.
    const QUARTER_HOUR = 900000;
    const offsets = new Map();
    function offsetAt(ms) {
        const bucket = Math.floor(ms / QUARTER_HOUR);
        const key = (zone() || '') + '|' + bucket;
        let off = offsets.get(key);
        if (off === undefined) {
            if (offsets.size > 20000) offsets.clear();
            off = offsetMs(bucket * QUARTER_HOUR);
            offsets.set(key, off);
        }
        return off;
    }

    /**
     * An instant as a time chart's x. While the viewer reads the browser's own clock the chart is
     * ApexCharts' default (real instants, its own local time scale) and x is the instant. Only for
     * another zone is x the viewer's wall clock read as if it were UTC, the one way ApexCharts can
     * place ticks on that zone's midnights; there a DST change shows as the clock does.
     */
    function toChart(ms) {
        return zone() ? shift(ms) : ms;
    }

    /** The instant a time chart's x stands for; the inverse of toChart. */
    function fromChart(x) {
        return zone() ? unshift(x) : x;
    }

    // The wall-clock conversion itself, for a chart whose own datetimeUTC flag says it uses one: a
    // C#-built chart decides that on the server.
    function shift(ms) {
        return ms == null || !Number.isFinite(+ms) ? ms : +ms + offsetAt(+ms);
    }
    function unshift(x) {
        if (x == null || !Number.isFinite(+x)) return x;
        // Two passes settle the offset either side of a DST change.
        const guess = +x - offsetAt(+x);
        return +x - offsetAt(guess);
    }

    /** The instant an x on chart `w` stands for, by the chart's own flag; by the viewer's zone without one. */
    function instantOfChart(x, w) {
        if (!w?.config) return fromChart(x);
        return onWallClock(w) ? unshift(x) : x;
    }

    // A chart drawn on the viewer's wall clock says so with datetimeUTC: ApexCharts then reads its x
    // values with UTC getters, so its own time scale places ticks on the viewer's midnights and hours.
    // ApexCharts defaults datetimeUTC to true, so a chart on real instants must set it false.
    // datetimeUTC false is ApexCharts' own local mode, used whenever the viewer's zone is the browser's.
    function onWallClock(w) {
        return w?.config?.xaxis?.labels?.datetimeUTC === true;
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
     * ApexCharts' datetimeFormatter in the viewer's format, for a wall-clock chart. Its own time scale
     * still picks every tick and every day, month, and year break; only the text follows the viewer.
     * Never pair it with xaxis.labels.formatter: any label formatter turns that time scale off, and
     * ApexCharts then spaces ticks evenly with no breaks at all.
     */
    function datetimeFormatter() {
        const { h24, dayFirst } = format();
        // 24-hour is what the charts always drew. The 12-hour forms are no wider than those ("1 PM"
        // beside "13:00", "10:45 AM" beside "10:45:00"), so ApexCharts lays the axis out the same and
        // its overlap check drops no labels. Hour ticks sit on the hour and minute ticks on the minute,
        // so nothing is lost.
        return h24
            ? { year: 'yyyy', month: "MMM 'yy", day: dayFirst ? 'dd MMM' : 'MMM dd', hour: 'HH:mm', minute: 'HH:mm:ss', second: 'HH:mm:ss' }
            : { year: 'yyyy', month: "MMM 'yy", day: dayFirst ? 'dd MMM' : 'MMM dd', hour: 'h TT', minute: 'h:mm TT', second: 'h:mm:ss TT' };
    }

    // ---- The time axis's labels ----
    //
    // ApexCharts' time scale keeps a label for every hour (24h) or day (30d) that fits, and no setting
    // thins those intervals. A time chart keeps ApexCharts' axis for layout, with its labels and tick
    // marks hidden, and this draws the labels: on natural breaks in the viewer's zone, about one per
    // LABEL_PX, evenly spaced, always including the day or month change.
    const AXIS_CLASS = 'no-time-axis';
    const LABEL_PX = 85;
    const MINUTE = 60000, HOUR = 3600000, DAY = 86400000;
    const STEPS = [
        ['m', 1], ['m', 5], ['m', 10], ['m', 15], ['m', 30],
        ['h', 1], ['h', 2], ['h', 3], ['h', 4], ['h', 6], ['h', 12],
        ['d', 1], ['d', 2], ['d', 3], ['d', 5], ['d', 7], ['d', 14],
        ['M', 1], ['M', 3], ['M', 6], ['y', 1],
    ];
    const STEP_MS = { m: MINUTE, h: HOUR, d: DAY, M: 30 * DAY, y: 365 * DAY };

    function pickStep(spanMs, maxLabels) {
        for (const [unit, n] of STEPS) {
            // A window of several days reads by the day, one of several hours by the hour.
            if (unit === 'm' && spanMs > 3 * HOUR) continue;
            if (unit === 'h' && spanMs > 3 * DAY) continue;
            if (spanMs / (STEP_MS[unit] * n) <= maxLabels) return [unit, n];
        }
        return ['y', 1];
    }

    // Wall-clock instants (the viewer's clock read as UTC) to label, each with whether it is a day,
    // month, or year change, which is set in bold.
    function axisTicks(wallMin, wallMax, unit, n) {
        const ticks = [];
        if (unit === 'm' || unit === 'h') {
            // Every minute and hour step divides a day, so midnight is always one of the ticks.
            const step = STEP_MS[unit] * n;
            for (let w = Math.ceil(wallMin / step) * step; w <= wallMax; w += step) {
                ticks.push({ wall: w, major: w % DAY === 0 });
            }
        } else if (unit === 'd') {
            // Counted in whole steps from the newest month change in view, so that change is labeled
            // and the spacing stays even across it; with none in view, from the window's last midnight.
            const lastMidnight = Math.floor(wallMax / DAY) * DAY;
            const end = new Date(lastMidnight);
            const monthStart = Date.UTC(end.getUTCFullYear(), end.getUTCMonth(), 1);
            const anchor = monthStart >= wallMin ? monthStart : lastMidnight;
            const step = n * DAY;
            const first = anchor - Math.floor((anchor - wallMin) / step) * step;
            for (let w = first; w <= wallMax; w += step) {
                if (w >= wallMin) ticks.push({ wall: w, major: new Date(w).getUTCDate() === 1 });
            }
        } else {
            const start = new Date(wallMin);
            for (let y = start.getUTCFullYear(), m = start.getUTCMonth(); ; m++) {
                const w = Date.UTC(y, m, 1);
                if (w > wallMax) break;
                const month = new Date(w).getUTCMonth();
                const keep = unit === 'y' ? month === 0 : month % n === 0;
                if (w >= wallMin && keep) ticks.push({ wall: w, major: month === 0 });
            }
        }
        return ticks;
    }

    function axisText(wall, unit, major) {
        const d = new Date(wall);
        const month = MONTHS[d.getUTCMonth()];
        const day = pad(d.getUTCDate());
        if (unit === 'y' || (unit === 'M' && major)) return String(d.getUTCFullYear());
        if (unit === 'M') return month;
        // A day axis marks the month change as ApexCharts does: "Nov '26", and the year on January 1st.
        if (unit === 'd' && d.getUTCDate() === 1) {
            return d.getUTCMonth() === 0 ? String(d.getUTCFullYear()) : `${month} '${String(d.getUTCFullYear()).slice(2)}`;
        }
        if (unit === 'd' || major) return format().dayFirst ? `${day} ${month}` : `${month} ${day}`;
        const h = d.getUTCHours(), m = d.getUTCMinutes();
        if (format().h24) return `${pad(h)}:${pad(m)}`;
        const h12 = h % 12 === 0 ? 12 : h % 12;
        const ampm = h < 12 ? 'AM' : 'PM';
        // "2 PM" on an hour axis; a minute axis keeps ":00" so its labels read alike.
        return unit === 'h' ? `${h12} ${ampm}` : `${h12}:${pad(m)} ${ampm}`;
    }

    const SVG = 'http://www.w3.org/2000/svg';
    let axisStyleAdded = false;

    /** Draws a time chart's axis labels; a chart opts in with xaxis.labels.style.cssClass 'no-time-axis'. */
    function drawTimeAxis(chart) {
        const w = chart?.w;
        const labels = w?.config?.xaxis?.labels;
        if (w?.config?.xaxis?.type !== 'datetime' || !String(labels?.style?.cssClass || '').includes(AXIS_CLASS)) return;
        const el = chart.el;
        const inner = el?.querySelector('.apexcharts-inner');
        const g = w.globals;
        inner?.querySelector(':scope > .no-time-axis-g')?.remove();
        if (!inner || !(g.gridWidth > 0) || !(g.maxX > g.minX) || labels.show === false) return;

        if (!axisStyleAdded) {
            axisStyleAdded = true;
            const style = document.createElement('style');
            style.textContent = '.no-time-axis-chart .apexcharts-xaxis-texts-g, .no-time-axis-chart .apexcharts-xaxis-tick { visibility: hidden; }';
            document.head.appendChild(style);
        }
        el.classList.add('no-time-axis-chart');

        // Positions are linear in the chart's own x; the breaks are on the viewer's wall clock.
        const wallChart = onWallClock(w);
        const toWall = x => (wallChart ? x : shift(x));
        const toX = wall => (wallChart ? wall : unshift(wall));
        const wallMin = toWall(g.minX), wallMax = toWall(g.maxX);
        const [unit, n] = pickStep(wallMax - wallMin, Math.max(2, Math.round(g.gridWidth / LABEL_PX)));

        // Font and placement from ApexCharts' own (hidden) labels, so ours sit where its would.
        const ref = el.querySelector('.apexcharts-xaxis-texts-g text');
        const shiftY = ref?.parentNode?.getAttribute('transform') || '';
        const dy = /translate\(\s*[-\d.]+\s*,\s*([-\d.]+)\s*\)/.exec(shiftY)?.[1];
        const offsetY = dy ? parseFloat(dy) : 0;
        const color = Array.isArray(labels.style?.colors) ? labels.style.colors[0] : (labels.style?.colors || '#9ca3af');
        const fontSize = ref?.getAttribute('font-size') || labels.style?.fontSize || '12px';
        const fontFamily = ref?.getAttribute('font-family') || 'Helvetica, Arial, sans-serif';
        const fontWeight = ref?.getAttribute('font-weight') || '400';
        const textY = ref ? parseFloat(ref.getAttribute('y')) + offsetY : g.gridHeight + 18;
        const ticksOn = w.config.xaxis.axisTicks?.show !== false;
        const tickColor = w.config.xaxis.axisTicks?.color || '#78909C';
        const group = document.createElementNS(SVG, 'g');
        group.setAttribute('class', 'no-time-axis-g');
        inner.appendChild(group);

        // A label may hang into the chart's padding, never past the chart itself.
        const leftLimit = -g.translateX;
        const rightLimit = (g.svgWidth || g.translateX + g.gridWidth) - g.translateX;
        let lastRight = -Infinity;
        for (const t of axisTicks(wallMin, wallMax, unit, n)) {
            const x = (toX(t.wall) - g.minX) / (g.maxX - g.minX) * g.gridWidth;
            if (x < 0 || x > g.gridWidth) continue;
            const text = document.createElementNS(SVG, 'text');
            text.setAttribute('x', String(x));
            text.setAttribute('y', String(textY));
            text.setAttribute('text-anchor', 'middle');
            text.setAttribute('fill', color);
            text.setAttribute('font-size', fontSize);
            text.setAttribute('font-family', fontFamily);
            text.setAttribute('font-weight', t.major ? '600' : fontWeight);
            text.setAttribute('class', 'apexcharts-text apexcharts-xaxis-label');
            text.textContent = axisText(t.wall, unit, t.major);
            group.appendChild(text);
            const width = text.getComputedTextLength?.() || 0;
            if (x - width / 2 < leftLimit || x + width / 2 > rightLimit || x - width / 2 < lastRight + 4) {
                text.remove();
                continue;
            }
            lastRight = x + width / 2;
            if (ticksOn) {
                const tick = document.createElementNS(SVG, 'line');
                tick.setAttribute('x1', String(x));
                tick.setAttribute('x2', String(x));
                tick.setAttribute('y1', String(g.gridHeight));
                tick.setAttribute('y2', String(g.gridHeight + 6));
                tick.setAttribute('stroke', tickColor);
                group.insertBefore(tick, text);
            }
        }
    }

    // Every chart on the page gets the hook; drawTimeAxis acts only on the ones that opted in.
    window.Apex = window.Apex || {};
    window.Apex.chart = window.Apex.chart || {};
    window.Apex.chart.events = { ...(window.Apex.chart.events || {}), mounted: drawTimeAxis, updated: drawTimeAxis };

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

        toChart,
        fromChart,
        onWallClock,

        /** An instant as an x on the chart `w`: wall clock for a wall-clock chart, else as is. */
        chartX: (ms, w) => (onWallClock(w) ? shift(ms) : ms),

        /** The instant an x on the chart `w` stands for. */
        instantOf: instantOfChart,

        datetimeFormatter,

        /**
         * xaxis.labels for a time axis on the viewer's clock and in their format, merged over `extra`
         * (style, rotation). Its series must carry toChart x values; datetimeUTC says which kind they are.
         */
        timeAxisLabels: (extra) => ({
            ...(extra || {}),
            datetimeUTC: !!zone(),
            datetimeFormatter: datetimeFormatter(),
            // Opts the axis into drawTimeAxis, which draws the labels ApexCharts' own would crowd.
            style: { ...((extra || {}).style || {}), cssClass: AXIS_CLASS },
        }),

        drawTimeAxis,

        /** An ApexCharts tooltip.x.formatter for a time chart, in the viewer's format. */
        chartTooltipFormatter: (seconds = true) => (value, opts) => tooltipLabel(instantOfChart(value, opts?.w), seconds),

        // For chart options serialized from C# (ChartTimeFormat).
        tooltipLabel,
        /** A day on its own: "Nov 3" or "3 Nov". */
        dateLabel: value => dayText(parts(value)),
    };
})();
