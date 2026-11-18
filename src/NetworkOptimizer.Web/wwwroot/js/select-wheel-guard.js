// Chromium closes an open <select> list on every wheel event it handles, including the zero-delta
// "wheel ended" event it sends itself 500 ms after the last wheel event. Page scripts never see that
// event, so a list opened inside the window shuts on its own. A press inside the window is held and
// the list opens once the window has passed. Never open it at once and reopen: the list would flash.
// While held, the select carries .select-wheel-wait (a spinner in place of its chevron, app.css).
(function () {
    'use strict';

    if (!/Chrome\//.test(navigator.userAgent)) return;

    var WHEEL_END_MS = 500;   // Chromium's kDefaultMouseWheelLatchingTransaction
    var MARGIN_MS = 60;       // for the event to reach the page after the timer fires
    var WAIT_CLASS = 'select-wheel-wait';
    var lastWheel = -Infinity;
    var pending = 0;
    var held = null;

    function remaining() { return lastWheel + WHEEL_END_MS + MARGIN_MS - performance.now(); }

    window.addEventListener('wheel', function () { lastWheel = performance.now(); }, { capture: true, passive: true });

    function release() {
        clearTimeout(pending);
        if (held) held.classList.remove(WAIT_CLASS);
        held = null;
    }

    function openWhenClear(select) {
        clearTimeout(pending);
        pending = setTimeout(function () {
            // The reader moved on, or the page re-rendered the control away.
            if (!select.isConnected || select.disabled || document.activeElement !== select) { release(); return; }
            // More wheel input since the press restarts Chromium's timer.
            if (remaining() > 0) { openWhenClear(select); return; }
            release();
            try { select.showPicker(); }
            catch (err) { console.warn('Select list did not open after the wheel window', err); }
        }, Math.max(0, remaining()));
    }

    window.addEventListener('mousedown', function (e) {
        var select = e.target;
        if (e.button !== 0 || !(select instanceof HTMLSelectElement)) return;
        if (select.multiple || select.size > 1 || select.disabled || typeof select.showPicker !== 'function') return;
        if (remaining() <= 0) return;
        e.preventDefault();
        release();
        held = select;
        select.classList.add(WAIT_CLASS);
        select.focus({ preventScroll: true });
        openWhenClear(select);
    }, true);

    // Focus leaving the held select drops the hold at once, not when its timer next runs.
    window.addEventListener('focusout', function (e) { if (e.target === held) release(); }, true);
})();
