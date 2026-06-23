// Satellite tile toggle control for Leaflet maps (Speed Test Map, Signal Map).
// Switches between the OSM base layer and Mapbox satellite tiles.
// Requires a Mapbox public token; if none is configured clicking the button
// shows a brief prompt linking to Settings.
(function () {
    if (window.MapSatelliteToggle) return;

    var SAT_TILE = 'https://api.mapbox.com/styles/v1/mapbox/satellite-v9/tiles/256/{z}/{x}/{y}@2x?access_token=';
    var SAT_ATTR = '&copy; <a href="https://www.mapbox.com/about/maps/" target="_blank" rel="noopener">Mapbox</a>'
                 + ' &copy; <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>';

    function layersIcon() {
        return '<svg xmlns="http://www.w3.org/2000/svg" width="15" height="15" viewBox="0 0 24 24"'
            + ' fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
            + '<polygon points="12 2 2 7 12 12 22 7 12 2"/>'
            + '<polyline points="2 17 12 22 22 17"/>'
            + '<polyline points="2 12 12 17 22 12"/>'
            + '</svg>';
    }

    window.MapSatelliteToggle = {
        /**
         * Adds a satellite toggle button to a Leaflet map (bottom-left, above the scale bar).
         * @param {L.Map} map
         * @param {L.TileLayer} osmLayer - the existing OSM base layer to swap in/out
         * @param {string} mapboxToken - Mapbox public token; empty string = unconfigured
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

                    var nag = null;
                    var satLayer = null;
                    var active = false;

                    function clearNag() {
                        if (nag) { nag.remove(); nag = null; }
                    }

                    btn.addEventListener('click', function () {
                        if (!mapboxToken) {
                            if (nag) { clearNag(); return; }
                            nag = document.createElement('div');
                            nag.className = 'map-sat-nag';
                            nag.innerHTML = 'Add a <a href="/settings#map">Mapbox API key</a> in Settings to enable satellite view.';
                            root.appendChild(nag);
                            setTimeout(clearNag, 6000);
                            return;
                        }
                        clearNag();
                        active = !active;
                        if (active) {
                            if (!satLayer) {
                                satLayer = L.tileLayer(SAT_TILE + mapboxToken, {
                                    maxZoom: 24, maxNativeZoom: 22, tileSize: 256,
                                    attribution: SAT_ATTR
                                });
                            }
                            map.removeLayer(osmLayer);
                            satLayer.addTo(map);
                            satLayer.bringToBack();
                            btn.classList.add('is-active');
                        } else {
                            if (satLayer) map.removeLayer(satLayer);
                            osmLayer.addTo(map);
                            osmLayer.bringToBack();
                            btn.classList.remove('is-active');
                        }
                    });

                    return root;
                }
            });

            map.addControl(new SatControl());
        }
    };
})();
