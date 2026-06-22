// Collapsible address search control for Leaflet maps (Speed Test Map, Signal Map).
// Geocodes via the public OpenStreetMap Nominatim service. Commercial use is permitted
// with attribution; per the Nominatim usage policy we only geocode on submit (Enter or
// icon click), never per keystroke, and rely on the browser-sent Referer to identify the app.
(function () {
    if (window.MapAddressSearch) return;

    var GEOCODE_URL = 'https://nominatim.openstreetmap.org/search';
    var RESULT_LIMIT = 10;
    // Only do location-aware searching once the user is zoomed into a region. Below this
    // the default view is continent-wide (e.g. the US-wide zoom 4 start), and biasing would
    // drag a far-away user's search toward the wrong place.
    var BIAS_MIN_ZOOM = 8;
    // Minimum half-size (degrees) of the "near me" box for the local-first pass, so a deeply
    // zoomed-in user still searches a regional area (~110 km) rather than the visible sliver.
    var LOCAL_MIN_HALF_DEG = 1.0;

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
                        '<div class="map-addr-search-bar">'
                        + '<input class="map-addr-search-input" type="text" autocomplete="off" spellcheck="false"'
                        + ' placeholder="' + escapeHtml(placeholder) + '" aria-label="Search address or place" />'
                        + '<button class="map-addr-search-toggle" type="button" aria-label="Search address"'
                        + ' title="Search address">' + searchIconSvg() + '</button>'
                        + '</div>'
                        + '<div class="map-addr-search-results" role="listbox"></div>';

                    var input = root.querySelector('.map-addr-search-input');
                    var toggle = root.querySelector('.map-addr-search-toggle');
                    var results = root.querySelector('.map-addr-search-results');
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
                        root.classList.remove('is-error', 'is-open');
                        input.blur();
                    }
                    function closeResults() {
                        root.classList.remove('is-open');
                        results.innerHTML = '';
                    }

                    // A regional box around the current map center, used to constrain the
                    // local-first pass. Returns null when zoomed out (see BIAS_MIN_ZOOM) so
                    // a far-away user at the default view gets a plain global search.
                    function localBox() {
                        if (map.getZoom() < BIAS_MIN_ZOOM) return null;
                        var c = map.getCenter();
                        var b = map.getBounds();
                        var halfLat = Math.max(LOCAL_MIN_HALF_DEG, (b.getNorth() - b.getSouth()) / 2);
                        var halfLng = Math.max(LOCAL_MIN_HALF_DEG, (b.getEast() - b.getWest()) / 2);
                        return { west: c.lng - halfLng, east: c.lng + halfLng, north: c.lat + halfLat, south: c.lat - halfLat };
                    }
                    function boxParam(box) {
                        return [box.west, box.north, box.east, box.south]
                            .map(function (n) { return n.toFixed(5); }).join(',');
                    }

                    function geocode(url) {
                        return fetch(url, { headers: { 'Accept': 'application/json' } })
                            .then(function (r) { return r.ok ? r.json() : []; })
                            .catch(function () { return []; });
                    }

                    function selectResult(hit) {
                        var lat = parseFloat(hit.lat), lng = parseFloat(hit.lon);
                        if (isNaN(lat) || isNaN(lng)) return;
                        closeResults();
                        var z = Math.min(Math.max(map.getZoom(), targetZoom), map.getMaxZoom() || targetZoom);
                        if (marker) map.removeLayer(marker);
                        marker = L.marker([lat, lng], { icon: pinIcon(L) })
                            .addTo(map)
                            // autoPan:false so opening the popup doesn't shove the result off-center
                            .bindPopup('<div class="map-addr-search-popup">' + escapeHtml(hit.display_name) + '</div>',
                                { autoPan: false });
                        marker.openPopup();
                        // Center last so the popup auto-pan can't pull us off the result.
                        map.setView([lat, lng], z, { animate: true });
                        if (typeof opts.onResult === 'function') opts.onResult(lat, lng, hit.display_name);
                    }

                    function renderResults(list) {
                        results.innerHTML = '';
                        list.forEach(function (hit) {
                            var row = document.createElement('div');
                            row.className = 'map-addr-search-result';
                            row.setAttribute('role', 'option');
                            row.textContent = hit.display_name;
                            row.addEventListener('click', function () { selectResult(hit); });
                            results.appendChild(row);
                        });
                        root.classList.add('is-open');
                    }

                    function showEmpty() {
                        results.innerHTML = '<div class="map-addr-search-empty">No matches found</div>';
                        root.classList.add('is-open', 'is-error');
                    }

                    function present(list) {
                        root.classList.remove('is-loading');
                        if (!list || !list.length) { showEmpty(); return; }
                        if (list.length === 1) { selectResult(list[0]); return; }
                        renderResults(list);
                    }

                    function doSearch() {
                        var q = (input.value || '').trim();
                        if (!q) { collapse(); return; }
                        root.classList.remove('is-error');
                        closeResults();
                        root.classList.add('is-loading');

                        // Soft viewbox bias around the user (a padded regional box) when zoomed
                        // in: it lifts a nearby match above a more "prominent" same-named place
                        // elsewhere, without hard-excluding far results or overriding an explicit
                        // region in the query (e.g. "paris, tx" still resolves to Texas).
                        var url = GEOCODE_URL + '?format=jsonv2&addressdetails=1&limit=' + RESULT_LIMIT;
                        var box = localBox();
                        if (box) url += '&viewbox=' + boxParam(box);
                        url += '&q=' + encodeURIComponent(q);
                        geocode(url).then(present);
                    }

                    toggle.addEventListener('click', function () {
                        if (root.classList.contains('is-collapsed')) { expand(); return; }
                        if ((input.value || '').trim()) doSearch();
                        else collapse();
                    });
                    input.addEventListener('keydown', function (e) {
                        if (e.key === 'Enter') { e.preventDefault(); doSearch(); }
                        else if (e.key === 'Escape') { e.preventDefault(); closeResults(); collapse(); }
                    });
                    input.addEventListener('input', function () {
                        root.classList.remove('is-error');
                        if (root.classList.contains('is-open')) closeResults();
                    });

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
