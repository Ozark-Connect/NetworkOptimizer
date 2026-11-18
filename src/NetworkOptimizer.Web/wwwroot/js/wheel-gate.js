// Wheel gate for maps: the mouse wheel zooms a map only after the pointer has rested on it, or
// after a click on it, and stops when the pointer leaves. A page scroll that carries the pointer
// over a map keeps scrolling the page: a wheel event restarts the wait only when the page moved the
// map since the last one. When the map stands still under the wheel (the page is at its end), the
// wait runs out and the wheel zooms.
// A classic script, so the eval-built and global maps can use it as well as the ES modules.
(function () {
    'use strict';

    var REST_MS = 350;

    window.WheelGate = {
        REST_MS: REST_MS,

        /**
         * Gates wheel zoom on a map.
         * @param {HTMLElement} host - the element the pointer rests on
         * @param {object} [opts]
         * @param {function(boolean)} [opts.onChange] - called when the gate opens or closes
         * @param {function(): boolean} [opts.exempt] - true while the gate must stay open (fullscreen)
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

            return {
                get armed() { return open || exempt(); },
                dispose: function () {
                    clearTimeout(rest);
                    host.removeEventListener('mouseenter', onEnter);
                    host.removeEventListener('wheel', onWheel, { capture: true });
                    host.removeEventListener('mouseleave', onLeave);
                    host.removeEventListener('pointerdown', onDown, true);
                }
            };
        },

        /** Gates a Leaflet map's scroll wheel zoom. */
        leaflet: function (map, opts) {
            var options = Object.assign({}, opts, {
                onChange: function (armed) {
                    if (armed) map.scrollWheelZoom.enable(); else map.scrollWheelZoom.disable();
                }
            });
            return window.WheelGate.attach(map.getContainer(), options);
        }
    };
})();
