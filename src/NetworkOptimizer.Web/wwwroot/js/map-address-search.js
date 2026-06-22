// Collapsible address search control for Leaflet maps (Speed Test Map, Signal Map).
// Geocodes via the public OpenStreetMap Nominatim service. Commercial use is permitted
// with attribution; per the Nominatim usage policy we only geocode on submit (Enter or
// icon click), never per keystroke, and rely on the browser-sent Referer to identify the app.
(function () {
    if (window.MapAddressSearch) return;

    var GEOCODE_URL = 'https://nominatim.openstreetmap.org/search';

    function searchIconSvg() {
        return '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24"'
            + ' fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
            + '<circle cx="11" cy="11" r="7"></circle><line x1="21" y1="21" x2="16.65" y2="16.65"></line></svg>';
    }

    function pinIcon(L) {
        return L.divIcon({
            className: 'map-addr-search-pin-wrap',
            html: '<div class="map-addr-search-pin"></div>',
            iconSize: [24, 24],
            iconAnchor: [12, 12],
            popupAnchor: [0, -14]
        });
    }

    function escapeHtml(s) {
        if (!s) return '';
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    window.MapAddressSearch = {
        /**
         * Adds a collapsible address-search control to a Leaflet map.
         * @param {L.Map} map - the Leaflet map instance
         * @param {Object} [opts]
         * @param {string} [opts.position='topright'] - Leaflet control corner
         * @param {string} [opts.placeholder='Search address or place...']
         * @param {number} [opts.zoom=18] - minimum zoom to apply when centering on a result
         * @param {function(number, number, string)} [opts.onResult] - callback(lat, lng, displayName)
         * @returns {L.Control|null}
         */
        add: function (map, opts) {
            opts = opts || {};
            if (typeof L === 'undefined' || !map) return null;

            var placeholder = opts.placeholder || 'Search address or place...';
            var targetZoom = opts.zoom || 18;

            var SearchControl = L.Control.extend({
                options: { position: opts.position || 'topright' },
                onAdd: function () {
                    var root = L.DomUtil.create('div', 'map-addr-search is-collapsed');
                    root.innerHTML =
                        '<input class="map-addr-search-input" type="text" autocomplete="off" spellcheck="false"'
                        + ' placeholder="' + escapeHtml(placeholder) + '" aria-label="Search address or place" />'
                        + '<button class="map-addr-search-toggle" type="button" aria-label="Search address"'
                        + ' title="Search address">' + searchIconSvg() + '</button>';

                    var input = root.querySelector('.map-addr-search-input');
                    var toggle = root.querySelector('.map-addr-search-toggle');
                    var marker = null;

                    // Keep clicks/scroll on the control from reaching the map underneath.
                    L.DomEvent.disableClickPropagation(root);
                    L.DomEvent.disableScrollPropagation(root);

                    function expand() {
                        root.classList.remove('is-collapsed');
                        setTimeout(function () { input.focus(); }, 60);
                    }
                    function collapse() {
                        root.classList.add('is-collapsed');
                        root.classList.remove('is-error');
                        input.blur();
                    }

                    function doSearch() {
                        var q = (input.value || '').trim();
                        if (!q) { collapse(); return; }
                        root.classList.remove('is-error');
                        root.classList.add('is-loading');
                        var url = GEOCODE_URL + '?format=jsonv2&limit=1&q=' + encodeURIComponent(q);
                        fetch(url, { headers: { 'Accept': 'application/json' } })
                            .then(function (r) { return r.ok ? r.json() : []; })
                            .then(function (results) {
                                root.classList.remove('is-loading');
                                if (!results || !results.length) { root.classList.add('is-error'); return; }
                                var hit = results[0];
                                var lat = parseFloat(hit.lat), lng = parseFloat(hit.lon);
                                if (isNaN(lat) || isNaN(lng)) { root.classList.add('is-error'); return; }
                                var z = Math.min(Math.max(map.getZoom(), targetZoom), map.getMaxZoom() || targetZoom);
                                map.setView([lat, lng], z, { animate: true });
                                if (marker) map.removeLayer(marker);
                                marker = L.marker([lat, lng], { icon: pinIcon(L) })
                                    .addTo(map)
                                    .bindPopup('<div class="map-addr-search-popup">' + escapeHtml(hit.display_name) + '</div>');
                                marker.openPopup();
                                if (typeof opts.onResult === 'function') opts.onResult(lat, lng, hit.display_name);
                            })
                            .catch(function () {
                                root.classList.remove('is-loading');
                                root.classList.add('is-error');
                            });
                    }

                    toggle.addEventListener('click', function () {
                        if (root.classList.contains('is-collapsed')) { expand(); return; }
                        if ((input.value || '').trim()) doSearch();
                        else collapse();
                    });
                    input.addEventListener('keydown', function (e) {
                        if (e.key === 'Enter') { e.preventDefault(); doSearch(); }
                        else if (e.key === 'Escape') { e.preventDefault(); collapse(); }
                    });
                    input.addEventListener('input', function () { root.classList.remove('is-error'); });

                    // Collapse when the user starts interacting with the map, but only if the
                    // field is empty so we never discard a half-typed query.
                    map.on('mousedown', function () {
                        if (!root.classList.contains('is-collapsed') && !(input.value || '').trim()) collapse();
                    });

                    this._root = root;
                    return root;
                }
            });

            var ctrl = new SearchControl();
            map.addControl(ctrl);
            return ctrl;
        }
    };
})();
