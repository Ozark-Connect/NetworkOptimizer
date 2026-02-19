// Stepped distance scale bar for Leaflet maps
// Usage:
//   var bar = SteppedScaleBar.create(map, steps);  // steps = 3 or 5
//   SteppedScaleBar.setSteps(bar, 5);              // change step count
//   SteppedScaleBar.remove(bar);                   // cleanup

window.SteppedScaleBar = {

    create: function (map, steps) {
        var Control = L.Control.extend({
            options: { position: 'bottomleft' },
            onAdd: function () {
                return L.DomUtil.create('div', 'stepped-scale-bar');
            }
        });
        var ctrl = new Control();
        ctrl.addTo(map);

        var state = { map: map, control: ctrl, steps: steps || 3 };
        var update = function () { SteppedScaleBar._update(state); };
        map.on('zoomend moveend', update);
        state._handler = update;
        update();
        return state;
    },

    setSteps: function (state, steps) {
        if (!state) return;
        state.steps = steps;
        this._update(state);
    },

    remove: function (state) {
        if (!state) return;
        if (state._handler) state.map.off('zoomend moveend', state._handler);
        if (state.control && state.map) state.map.removeControl(state.control);
    },

    _update: function (state) {
        var ctrl = state.control;
        var m = state.map;
        if (!ctrl || !ctrl._container || !m) return;
        var el = ctrl._container;
        var steps = state.steps;

        // meters-per-pixel at map center
        var size = m.getSize();
        if (size.x === 0) return;
        var p1 = m.containerPointToLatLng([0, size.y / 2]);
        var p2 = m.containerPointToLatLng([size.x, size.y / 2]);
        var mPerPx = m.distance(p1, p2) / size.x;

        // Target pixel width scales with step count
        var targetPx = steps <= 3 ? 180 : 280;
        var totalFeet = (mPerPx * targetPx) * 3.28084;

        // Pick a nice round number per step
        var nice = [1, 2, 5, 10, 15, 20, 25, 50, 100, 150, 200, 250, 500, 1000, 2000, 5000, 10000];
        var raw = totalFeet / steps;
        var stepFt = nice[nice.length - 1];
        for (var i = 0; i < nice.length; i++) {
            if (nice[i] >= raw) { stepFt = nice[i]; break; }
        }

        var barPx = (stepFt * steps / 3.28084) / mPerPx;
        var segPx = barPx / steps;

        var html = '<div class="stepped-scale-segments">';
        for (var s = 0; s < steps; s++) {
            var cls = s % 2 === 0 ? 'stepped-scale-seg-filled' : 'stepped-scale-seg-empty';
            html += '<div class="stepped-scale-seg ' + cls + '" style="width:' + segPx.toFixed(1) + 'px"></div>';
        }
        html += '</div><div class="stepped-scale-labels">';
        for (var s = 0; s <= steps; s++) {
            var ft = stepFt * s;
            var label = ft === 0 ? '0' : (ft >= 5280 ? (ft / 5280).toFixed(1) + ' mi' : ft + ' ft');
            html += '<span>' + label + '</span>';
        }
        html += '</div>';
        el.innerHTML = html;
    }
};
