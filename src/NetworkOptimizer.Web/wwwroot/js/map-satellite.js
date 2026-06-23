// Satellite tile toggle control for Leaflet maps (Speed Test Map, Signal Map).
// Switches between the OSM base layer and a satellite layer.
//
// Provider selection:
//   - No Mapbox token configured: uses Esri World Imagery, which needs no API key
//     and no signup. It is free for NON-COMMERCIAL use only (attribution required).
//   - Mapbox token configured (Settings > Satellite Imagery): uses Mapbox satellite,
//     which is licensed for commercial use under the account's plan. This lets
//     commercial users enable satellite without relying on the non-commercial Esri tiles.
(function () {
    if (window.MapSatelliteToggle) return;

    var ESRI_TILE = 'https://services.arcgisonline.com/arcgis/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}';
    var ESRI_ATTR = 'Imagery &copy; <a href="https://www.esri.com" target="_blank" rel="noopener">Esri</a>,'
                  + ' Maxar, Earthstar Geographics &mdash; non-commercial use only';

    var MAPBOX_TILE = 'https://api.mapbox.com/styles/v1/mapbox/satellite-v9/tiles/256/{z}/{x}/{y}@2x?access_token=';
    var MAPBOX_ATTR = '&copy; <a href="https://www.mapbox.com/about/maps/" target="_blank" rel="noopener">Mapbox</a>'
                    + ' &copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>';

    function layersIcon() {
        return '<svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" viewBox="0 0 24 24"'
            + ' fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
            + '<polygon points="12 2 2 7 12 12 22 7 12 2"/>'
            + '<polyline points="2 17 12 22 22 17"/>'
            + '<polyline points="2 12 12 17 22 12"/>'
            + '</svg>';
    }

    function buildSatLayer(mapboxToken) {
        if (mapboxToken) {
            return L.tileLayer(MAPBOX_TILE + mapboxToken, {
                maxZoom: 24, maxNativeZoom: 22, tileSize: 256, attribution: MAPBOX_ATTR
            });
        }
        return L.tileLayer(ESRI_TILE, {
            maxZoom: 24, maxNativeZoom: 19, attribution: ESRI_ATTR
        });
    }

    // One-time, per-browser acknowledgement that the built-in (Esri) imagery is
    // non-commercial. Only relevant when no Mapbox token is set; once a server-side
    // token is configured the confirm never shows, so per-browser scope is fine.
    var ACK_KEY = 'networkOptimizerSatelliteNonCommercialAck';
    function ackGiven() {
        try { return localStorage.getItem(ACK_KEY) === '1'; } catch (e) { return false; }
    }
    function setAck() {
        try { localStorage.setItem(ACK_KEY, '1'); } catch (e) { }
    }

    // Styled confirm shown the first time a user enables the free non-commercial
    // imagery. Resolves true if they accept, false on cancel/backdrop/Escape.
    function confirmNonCommercial() {
        return new Promise(function (resolve) {
            var backdrop = document.createElement('div');
            backdrop.className = 'map-sat-confirm-backdrop';
            backdrop.innerHTML =
                '<div class="map-sat-confirm" role="dialog" aria-modal="true" aria-labelledby="map-sat-confirm-title">'
                + '<h3 id="map-sat-confirm-title">Enable satellite imagery?</h3>'
                + '<p>The built-in satellite imagery (Esri World Imagery) is free for <strong>personal,'
                + ' non-commercial use only</strong>. If you use Network Optimizer commercially (an MSP,'
                + ' installer, or any paid-service delivery), add a Mapbox token in Settings to use'
                + ' commercially licensed imagery instead.</p>'
                + '<div class="map-sat-confirm-actions">'
                + '<button class="btn btn-secondary" data-act="cancel" type="button">Cancel</button>'
                + '<button class="btn btn-primary" data-act="ok" type="button">I understand, continue</button>'
                + '</div></div>';

            function close(result) {
                document.removeEventListener('keydown', onKey, true);
                backdrop.remove();
                resolve(result);
            }
            function onKey(e) {
                if (e.key === 'Escape') { e.preventDefault(); close(false); }
            }
            backdrop.addEventListener('click', function (e) {
                if (e.target === backdrop) { close(false); return; }
                var act = e.target.getAttribute('data-act');
                if (act === 'ok') close(true);
                else if (act === 'cancel') close(false);
            });
            document.addEventListener('keydown', onKey, true);
            document.body.appendChild(backdrop);
        });
    }

    window.MapSatelliteToggle = {
        /**
         * Adds a satellite toggle button to a Leaflet map (bottom-left, above the scale bar).
         * @param {L.Map} map
         * @param {L.TileLayer} osmLayer - the existing OSM base layer to swap in/out
         * @param {string} mapboxToken - optional Mapbox token; empty = use free Esri imagery
         */
        add: function (map, osmLayer, mapboxToken) {
            if (typeof L === 'undefined' || !map) return;

            var SatControl = L.Control.extend({
                options: { position: 'bottomleft' },
                onAdd: function () {
                    var root = L.DomUtil.create('div', 'map-sat-ctrl');
                    L.DomEvent.disableClickPropagation(root);
                    L.DomEvent.disableScrollPropagation(root);

                    var btn = document.createElement('button');
                    btn.className = 'map-sat-btn';
                    btn.type = 'button';
                    btn.setAttribute('aria-label', 'Toggle satellite view');
                    btn.setAttribute('title', 'Satellite view');
                    btn.innerHTML = layersIcon();
                    root.appendChild(btn);

                    var satLayer = null;
                    var active = false;
                    var token = mapboxToken || '';

                    function enableSat() {
                        if (!satLayer) satLayer = buildSatLayer(token);
                        map.removeLayer(osmLayer);
                        satLayer.addTo(map);
                        satLayer.bringToBack();
                        btn.classList.add('is-active');
                        active = true;
                    }
                    function disableSat() {
                        if (satLayer) map.removeLayer(satLayer);
                        osmLayer.addTo(map);
                        osmLayer.bringToBack();
                        btn.classList.remove('is-active');
                        active = false;
                    }

                    btn.addEventListener('click', function () {
                        if (active) { disableSat(); return; }
                        // Licensed (token) or already acknowledged: switch immediately.
                        if (token || ackGiven()) { enableSat(); return; }
                        // First time on the free non-commercial layer: confirm once.
                        confirmNonCommercial().then(function (ok) {
                            if (ok) { setAck(); enableSat(); }
                        });
                    });

                    return root;
                }
            });

            map.addControl(new SatControl());
        }
    };
})();
