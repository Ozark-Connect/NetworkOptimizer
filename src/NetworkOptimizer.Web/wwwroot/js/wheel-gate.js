// Wheel gate for maps: the mouse wheel zooms a map only after the pointer has rested on it, or
// after a click on it, and stops when the pointer leaves. A page scroll that carries the pointer
// over a map keeps scrolling the page: a wheel event restarts the wait only when the page moved the
// map since the last one. When the map stands still under the wheel (the page is at its end), the
// wait runs out and the wheel zooms.
//
// Touch gate (opts.touchSurfaces): the same idea for a finger. A one-finger drag scrolls the page
// until the map is armed by a tap, by a press held still for HOLD_MS, by a sideways swipe, or by two fingers; a touch
// outside the map disarms it. Armed by touch, the map shows a ring.
// A classic script, so the eval-built and global maps can use it as well as the ES modules.
(function () {
    'use strict';

    var REST_MS = 350;
    // A held press arms touch sooner than the wheel's rest: well clear of iOS's text selection (~500 ms).
    var HOLD_MS = 200;
    // Matches the maps' own tap tolerance: a press that moves further is a drag, not a hold or a tap.
    var SLOP_PX = 8;

    /**
     * Arms the touch side of a gate. The surfaces take `touch-action: pan-y` so the browser can scroll
     * the page through them; an armed map cancels the touchmove instead, which stops the scroll.
     */
    function touchGate(host, opts, exempt) {
        var surfaces = (opts.touchSurfaces() || []).filter(Boolean);
        var saved = surfaces.map(function (el) { return el.style.touchAction; });
        surfaces.forEach(function (el) { el.style.touchAction = 'pan-y'; });

        var armed = false;
        var press = null;      // the one finger down while disarmed: {id, x, y, moved, claimed}
        var hold = null;
        var down = new Set();  // touch pointers down on the map
        var ring = null;

        function isTouch(e) { return e.pointerType === 'touch'; }
        function open() { return armed || exempt() || !!(opts.touchExempt && opts.touchExempt()); }
        // A press on something that takes its own drag (a draggable marker, an editor handle that cancels its
        // touchstart) belongs to it, armed or not.
        function claimed() { return !!(press && press.claimed); }

        function showRing(on) {
            if (on && !ring && getComputedStyle(host).position !== 'static') {
                ring = document.createElement('div');
                ring.className = 'map-touch-ring';
                host.appendChild(ring);
            } else if (!on && ring) {
                ring.remove();
                ring = null;
            }
        }
        function onOutside(e) {
            if (isTouch(e) && !host.contains(e.target)) disarm();
        }
        function arm() {
            clearTimeout(hold);
            hold = null;
            if (armed) return;
            armed = true;
            showRing(!exempt());
            // For a map's own controls to follow, as the Signal Map's Pan tool does (app.css).
            host.classList.add('map-touch-armed');
            document.addEventListener('pointerdown', onOutside, true);
        }
        function disarm() {
            armed = false;
            showRing(false);
            host.classList.remove('map-touch-armed');
            document.removeEventListener('pointerdown', onOutside, true);
        }

        function onDown(e) {
            if (!isTouch(e)) return;
            // The lift is heard on the document: the element the finger went down on can be gone by then
            // (Leaflet swaps tiles mid-gesture), and a lost lift would count every later finger as a second one.
            if (down.size === 0) {
                document.addEventListener('pointerup', onUp, true);
                document.addEventListener('pointercancel', onUp, true);
            }
            down.add(e.pointerId);
            if (down.size > 1) { press = null; arm(); return; }
            if (open()) return;
            press = { id: e.pointerId, x: e.clientX, y: e.clientY, moved: false,
                claimed: !!(e.target.closest && e.target.closest('.leaflet-marker-draggable')) };
            clearTimeout(hold);
            if (!press.claimed) hold = setTimeout(function () { if (press && !press.moved) arm(); }, HOLD_MS);
        }
        // Bubble, after the target's own handler: one that cancels its touchstart has taken the gesture.
        function onTouchStart(e) {
            if (press && e.touches.length === 1 && e.defaultPrevented) {
                press.claimed = true;
                clearTimeout(hold);
                hold = null;
            }
        }
        // Capture, ahead of the map: a disarmed one-finger move never reaches it, so the map holds
        // still while the browser decides to scroll the page.
        function onMove(e) {
            if (!isTouch(e) || open() || claimed() || down.size > 1) return;
            if (press && e.pointerId === press.id && !press.moved) {
                var dx = Math.abs(e.clientX - press.x), dy = Math.abs(e.clientY - press.y);
                // Sideways is intent: the page has no sideways scroll, so the browser leaves the gesture to
                // the map (touch-action pan-y), and this same swipe pans it.
                if (dx >= SLOP_PX && dx > dy) { arm(); return; }
                if (Math.hypot(dx, dy) >= SLOP_PX) {
                    press.moved = true;
                    clearTimeout(hold);
                    hold = null;
                }
            }
            e.stopPropagation();
        }
        function onUp(e) {
            if (!isTouch(e) || !down.delete(e.pointerId)) return;
            if (down.size === 0) {
                document.removeEventListener('pointerup', onUp, true);
                document.removeEventListener('pointercancel', onUp, true);
            }
            if (press && e.pointerId === press.id) {
                // A tap: the press ended where it started, before the hold ran out.
                if (e.type === 'pointerup' && !press.moved && !press.claimed && !armed) arm();
                press = null;
                clearTimeout(hold);
                hold = null;
            }
        }
        // Not passive: an armed map, or any two-finger gesture, must be able to cancel the page scroll.
        // Capture: a disarmed one-finger move never reaches a map that cancels it itself (Leaflet on
        // native touch events), so the browser keeps the scroll.
        function onTouchMove(e) {
            if (e.touches.length > 1) arm();
            if (open() || claimed()) { if (e.cancelable) e.preventDefault(); }
            else e.stopPropagation();
        }

        host.addEventListener('pointerdown', onDown, true);
        host.addEventListener('pointermove', onMove, true);
        host.addEventListener('touchstart', onTouchStart, { passive: true });
        host.addEventListener('touchmove', onTouchMove, { passive: false, capture: true });

        return function dispose() {
            clearTimeout(hold);
            disarm();
            surfaces.forEach(function (el, i) { el.style.touchAction = saved[i]; });
            host.removeEventListener('pointerdown', onDown, true);
            host.removeEventListener('pointermove', onMove, true);
            host.removeEventListener('touchstart', onTouchStart);
            host.removeEventListener('touchmove', onTouchMove, { capture: true });
            document.removeEventListener('pointerup', onUp, true);
            document.removeEventListener('pointercancel', onUp, true);
        };
    }

    window.WheelGate = {
        REST_MS: REST_MS,

        /**
         * Gates wheel zoom on a map, and with opts.touchSurfaces its one-finger touch drag.
         * @param {HTMLElement} host - the element the pointer rests on
         * @param {object} [opts]
         * @param {function(boolean)} [opts.onChange] - called when the gate opens or closes
         * @param {function(): boolean} [opts.exempt] - true while the gate must stay open (fullscreen)
         * @param {function(): HTMLElement[]} [opts.touchSurfaces] - the elements the map sets
         *   `touch-action: none` on; gates touch when given
         * @param {function(): boolean} [opts.touchExempt] - true while a finger belongs to the map anyway
         *   (an editor's drawing tool)
         * @returns {{armed: boolean, dispose: function()}} armed is live: read it in a wheel handler
         */
        attach: function (host, opts) {
            opts = opts || {};
            var open = false;
            var rest = null;

            function set(value) {
                clearTimeout(rest);
                rest = null;
                if (open === value) return;
                open = value;
                if (opts.onChange) opts.onChange(value);
            }
            var lastTop = null;
            function exempt() { return !!(opts.exempt && opts.exempt()); }
            function wait() {
                clearTimeout(rest);
                rest = setTimeout(function () { set(true); }, REST_MS);
            }
            function onEnter() {
                lastTop = host.getBoundingClientRect().top;
                if (exempt()) set(true); else wait();
            }
            function onWheel() {
                if (open) return;
                if (exempt()) { set(true); return; }
                var top = host.getBoundingClientRect().top;
                // The page scrolled the map since the last wheel: still passing over it, so wait again.
                // Standing still, the wait already running is left to finish.
                if (rest === null || top !== lastTop) wait();
                lastTop = top;
            }
            function onLeave() { lastTop = null; set(false); }
            function onDown() { set(true); }

            host.addEventListener('mouseenter', onEnter);
            // Capture, so the gate sees the wheel and the press before the map's own handlers act on them.
            host.addEventListener('wheel', onWheel, { passive: true, capture: true });
            host.addEventListener('mouseleave', onLeave);
            host.addEventListener('pointerdown', onDown, true);
            if (opts.onChange) opts.onChange(false);
            var disposeTouch = opts.touchSurfaces ? touchGate(host, opts, exempt) : null;

            return {
                get armed() { return open || exempt(); },
                dispose: function () {
                    clearTimeout(rest);
                    host.removeEventListener('mouseenter', onEnter);
                    host.removeEventListener('wheel', onWheel, { capture: true });
                    host.removeEventListener('mouseleave', onLeave);
                    host.removeEventListener('pointerdown', onDown, true);
                    if (disposeTouch) disposeTouch();
                }
            };
        },

        /** Gates a Leaflet map's scroll wheel zoom, and with opts.touch its one-finger drag. */
        leaflet: function (map, opts) {
            var container = map.getContainer();
            var options = Object.assign({}, opts, {
                onChange: function (armed) {
                    if (armed) map.scrollWheelZoom.enable(); else map.scrollWheelZoom.disable();
                }
            });
            if (opts && opts.touch) {
                options.touchSurfaces = function () { return [container]; };
                // A map whose dragging is off (an editor's drawing mode) has no pan to gate: the finger is the tool's.
                // The caller's own touchExempt adds tools that keep dragging on (the Signal Map's Select).
                var callerExempt = opts.touchExempt;
                options.touchExempt = function () { return !map.dragging.enabled() || !!(callerExempt && callerExempt()); };
            }
            return window.WheelGate.attach(container, options);
        }
    };
})();
