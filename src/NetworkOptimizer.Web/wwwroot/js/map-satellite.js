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

                    btn.addEventListener('click', function () {
                        active = !active;
                        if (active) {
                            if (!satLayer) satLayer = buildSatLayer(mapboxToken || '');
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
