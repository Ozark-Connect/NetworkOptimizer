// InnerSpace import: previews a building template on the floor plan editor's map, lets the user
// drag and rotate it into place, and rasterizes SVG floor images (InnerSpace scans) for upload.
// Template geometry arrives in local meters (x east, y up the plan); the conversion to lat/lng
// must match TemplatePlacer.ToLatLng on the server, or the preview and the import disagree.
window.innerspaceImport = {
    _layer: null,
    _handle: null,
    _preview: null,
    _placement: null,
    _drag: null,
    _dotNetRef: null,

    _map: function () { return window.fpEditor && window.fpEditor._map; },

    _toLatLng: function (x, y) {
        var p = this._placement;
        var r = -p.rotationDeg * Math.PI / 180;
        var ex = x * Math.cos(r) - y * Math.sin(r);
        var ny = x * Math.sin(r) + y * Math.cos(r);
        return [p.lat + ny / 111320, p.lng + ex / (111320 * Math.cos(p.lat * Math.PI / 180))];
    },

    // preview: { walls: [{ points: [[x, y]...], materials: [...] }], footprints: [[[x, y]...]] }
    show: function (dotNetRef, preview, placement) {
        var map = this._map();
        if (!map) return false;
        this.hide();
        this._dotNetRef = dotNetRef;
        this._preview = preview;
        this._placement = { lat: placement.lat, lng: placement.lng, rotationDeg: placement.rotationDeg || 0 };
        this._layer = L.layerGroup().addTo(map);
        this._setBuildingHits(false);

        var self = this;
        this._handle = L.marker([placement.lat, placement.lng], {
            draggable: true,
            zIndexOffset: 1000,
            // The editor's Move Building handle: same job, same look.
            icon: L.divIcon({ className: 'fp-move-handle', html: '✥', iconSize: [28, 28], iconAnchor: [14, 14] })
        }).addTo(map);
        this._handle.on('drag', function (e) {
            var ll = e.target.getLatLng();
            self._placement.lat = ll.lat;
            self._placement.lng = ll.lng;
            self._render();
        });
        this._handle.on('dragend', function () { self._notify(); });

        // Escape cancels placing. The map holds focus here, so listen on the document, and take the
        // key before the floor plan editor's own Escape handling sees it.
        this._onKey = function (e) {
            if (e.key !== 'Escape' || !self._dotNetRef) return;
            e.stopPropagation();
            e.preventDefault();
            self._dotNetRef.invokeMethodAsync('OnPlacementEscape');
        };
        document.addEventListener('keydown', this._onKey, true);

        this._render();
        this.fitView();
        return true;
    },

    _render: function () {
        if (!this._layer) return;
        this._layer.clearLayers();
        var self = this;
        var colors = (window.fpEditor && window.fpEditor._materialColors) || {};
        (this._preview.footprints || []).forEach(function (fp) {
            var poly = L.polygon(fp.map(function (q) { return self._toLatLng(q[0], q[1]); }), {
                color: '#f97316', weight: 1, fillColor: '#f97316', fillOpacity: 0.12, interactive: true
            }).addTo(self._layer);
            poly.on('mousedown', function (e) { self._startDrag(e); });
        });
        (this._preview.walls || []).forEach(function (w) {
            for (var i = 0; i < w.points.length - 1; i++) {
                var a = w.points[i], b = w.points[i + 1];
                var mat = (w.materials && w.materials[i]) || 'drywall';
                L.polyline([self._toLatLng(a[0], a[1]), self._toLatLng(b[0], b[1])], {
                    color: colors[mat] || '#a0a0a8', weight: 3, opacity: 0.95, interactive: false
                }).addTo(self._layer);
            }
        });
    },

    // Dragging the footprint itself, not just the handle.
    _startDrag: function (e) {
        var map = this._map();
        if (!map) return;
        L.DomEvent.stop(e);
        var self = this;
        var start = e.latlng;
        var origin = { lat: this._placement.lat, lng: this._placement.lng };
        map.dragging.disable();
        var move = function (ev) {
            self._placement.lat = origin.lat + (ev.latlng.lat - start.lat);
            self._placement.lng = origin.lng + (ev.latlng.lng - start.lng);
            self._handle.setLatLng([self._placement.lat, self._placement.lng]);
            self._render();
        };
        var ended = false;
        var up = function () {
            if (ended) return;
            ended = true;
            map.off('mousemove', move);
            map.off('mouseup', up);
            document.removeEventListener('mouseup', up);
            map.dragging.enable();
            self._notify();
        };
        map.on('mousemove', move);
        map.on('mouseup', up);
        // A release outside the map never reaches Leaflet; without this the map stays unpannable.
        document.addEventListener('mouseup', up);
    },

    _notify: function () {
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnPlacementChanged', this.getPlacement());
    },

    setPlacement: function (placement) {
        if (!this._placement) return;
        this._placement = { lat: placement.lat, lng: placement.lng, rotationDeg: placement.rotationDeg || 0 };
        if (this._handle) this._handle.setLatLng([placement.lat, placement.lng]);
        this._render();
    },

    setRotation: function (deg) {
        if (!this._placement) return;
        this._placement.rotationDeg = ((deg % 360) + 360) % 360;
        this._render();
    },

    getPlacement: function () {
        return this._placement ? { lat: this._placement.lat, lng: this._placement.lng, rotationDeg: this._placement.rotationDeg } : null;
    },

    fitView: function () {
        var map = this._map();
        if (!map || !this._layer) return;
        var layers = this._layer.getLayers();
        if (!layers.length) { map.setView([this._placement.lat, this._placement.lng], 19); return; }
        var b = L.featureGroup(layers).getBounds();
        if (b.isValid()) map.fitBounds(b.pad(0.4));
    },

    // The other buildings' "Click to edit" areas stay inert while placing (their clicks are ignored too).
    _setBuildingHits: function (on) {
        var hits = window.fpEditor && window.fpEditor._bgHitAreaLayer;
        if (!hits) return;
        hits.eachLayer(function (l) {
            var el = l.getElement && l.getElement();
            if (el) el.style.pointerEvents = on ? '' : 'none';
        });
    },

    hide: function () {
        this._setBuildingHits(true);
        if (this._onKey) {
            document.removeEventListener('keydown', this._onKey, true);
            this._onKey = null;
        }
        var map = this._map();
        if (this._layer && map) map.removeLayer(this._layer);
        if (this._handle && map) map.removeLayer(this._handle);
        this._layer = null;
        this._handle = null;
        this._preview = null;
        this._placement = null;
    },

    // pending: [{ floorId, sourcePlanId, swLat, swLng, neLat, neLng, rotationDeg }]
    // Draws each SVG on a canvas and uploads the PNG through the regular floor image endpoint.
    uploadPendingImages: async function (pending) {
        var done = 0;
        for (var i = 0; i < pending.length; i++) {
            var p = pending[i];
            try {
                var resp = await fetch('/api/floor-plan/innerspace/plans/' + encodeURIComponent(p.sourcePlanId) + '/image');
                if (!resp.ok) continue;
                var blob = await resp.blob();
                var png = await this._rasterize(blob);
                if (!png) continue;
                var form = new FormData();
                form.append('image', png, 'innerspace.png');
                form.append('swLat', String(p.swLat));
                form.append('swLng', String(p.swLng));
                form.append('neLat', String(p.neLat));
                form.append('neLng', String(p.neLng));
                form.append('label', 'InnerSpace');
                var up = await fetch('/api/floor-plan/floors/' + p.floorId + '/images', { method: 'POST', body: form });
                if (!up.ok) continue;
                var img = await up.json();
                if (p.rotationDeg) {
                    await fetch('/api/floor-plan/images/' + img.id, {
                        method: 'PUT',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ rotationDeg: p.rotationDeg })
                    });
                }
                done++;
            } catch (e) {
                console.warn('InnerSpace image upload failed', e);
            }
        }
        return done;
    },

    _rasterize: function (blob) {
        return new Promise(function (resolve) {
            var url = URL.createObjectURL(blob);
            var img = new Image();
            img.onload = function () {
                // Scan SVGs are small (about 900 x 1900); 4x keeps lines sharp at close zoom.
                var w = img.naturalWidth || img.width, h = img.naturalHeight || img.height;
                var scale = Math.min(4, 4096 / Math.max(w, h, 1));
                var canvas = document.createElement('canvas');
                canvas.width = Math.max(1, Math.round(w * scale));
                canvas.height = Math.max(1, Math.round(h * scale));
                canvas.getContext('2d').drawImage(img, 0, 0, canvas.width, canvas.height);
                URL.revokeObjectURL(url);
                canvas.toBlob(function (b) { resolve(b); }, 'image/png');
            };
            img.onerror = function () { URL.revokeObjectURL(url); resolve(null); };
            img.src = url;
        });
    }
};
