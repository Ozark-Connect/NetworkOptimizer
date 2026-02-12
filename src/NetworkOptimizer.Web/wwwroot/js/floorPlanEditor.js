// Floor Plan Editor - Leaflet map integration
// Provides map, AP markers, wall drawing, heatmap, and floor overlay management
window.fpEditor = {

    // ── State ────────────────────────────────────────────────────────
    _map: null,
    _dotNetRef: null,
    _overlay: null,
    _apLayer: null,
    _apGlowLayer: null,
    _bgWallLayer: null,
    _wallLayer: null,
    _wallHighlightLayer: null,
    _allWalls: [],
    _wallSelection: { wallIdx: null, segIdx: null },
    _materialLabels: {},
    _materialColors: {},
    _placementHandler: null,
    _wallClickHandler: null,
    _wallDblClickHandler: null,
    _wallMoveHandler: null,
    _wallMapClickBound: false,
    _currentWall: null,
    _currentWallLine: null,
    _currentWallVertices: null,
    _currentWallLabels: null,
    _refAngle: null,
    _previewLine: null,
    _snapToClose: false,
    _snapIndicator: null,
    _closedBySnap: false,
    _corners: null,
    _moveMarker: null,
    _heatmapOverlay: null,
    _contourLayer: null,
    _txPowerOverrides: {},
    _heatmapBand: '5',
    _signalClusterGroup: null,
    _signalCurrentSpider: null,
    _signalSwitchingSpider: false,
    _bgWalls: [],
    _snapGuideLine: null,
    _snapAngleMarker: null,
    _previewLengthLabel: null,

    // ── Map Initialization ───────────────────────────────────────────

    initMap: function (containerId, centerLat, centerLng, zoom) {
        var self = this;
        this._txPowerOverrides = {};

        function loadCss(href) {
            if (document.querySelector('link[href="' + href + '"]')) return;
            var l = document.createElement('link');
            l.rel = 'stylesheet';
            l.href = href;
            document.head.appendChild(l);
        }

        function loadScript(src, cb) {
            var existing = document.querySelector('script[src="' + src + '"]');
            if (existing) {
                if (existing.dataset.loaded === 'true') { cb(); return; }
                existing.addEventListener('load', cb);
                return;
            }
            var s = document.createElement('script');
            s.src = src;
            s.onload = function () { s.dataset.loaded = 'true'; cb(); };
            document.head.appendChild(s);
        }

        function init() {
            // Load Leaflet first
            if (typeof L === 'undefined') {
                loadCss('https://unpkg.com/leaflet@1.9.4/dist/leaflet.css');
                loadScript('https://unpkg.com/leaflet@1.9.4/dist/leaflet.js', function () { setTimeout(init, 100); });
                return;
            }

            // Load MarkerCluster after Leaflet
            if (typeof L.markerClusterGroup !== 'function') {
                loadCss('https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.css');
                loadCss('https://unpkg.com/leaflet.markercluster@1.5.3/dist/MarkerCluster.Default.css');
                loadScript('https://unpkg.com/leaflet.markercluster@1.5.3/dist/leaflet.markercluster.js', function () { setTimeout(init, 100); });
                return;
            }

            var container = document.getElementById(containerId);
            if (!container) { setTimeout(init, 100); return; }

            var m = L.map(containerId, { center: [centerLat, centerLng], zoom: zoom, zoomControl: true, maxZoom: 24 });
            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 24, maxNativeZoom: 19, attribution: 'OpenStreetMap'
            }).addTo(m);
            self._map = m;

            // Custom panes for z-ordering: heatmap(350) < floorOverlay(380) < apGlow(390) < walls(400) < apIcons(450)
            m.createPane('heatmapPane');
            var hpEl = m.getPane('heatmapPane');
            hpEl.style.zIndex = 350;
            hpEl.style.pointerEvents = 'none';
            m.createPane('fpOverlayPane');
            m.getPane('fpOverlayPane').style.zIndex = 380;
            m.createPane('apGlowPane');
            m.getPane('apGlowPane').style.zIndex = 390;
            m.createPane('bgWallPane');
            var bgWpEl = m.getPane('bgWallPane');
            bgWpEl.style.zIndex = 395;
            bgWpEl.style.pointerEvents = 'none';
            m.createPane('wallPane');
            m.getPane('wallPane').style.zIndex = 400;
            m.createPane('apIconPane');
            m.getPane('apIconPane').style.zIndex = 450;
            m.createPane('signalDataPane');
            m.getPane('signalDataPane').style.zIndex = 420;

            self._apGlowLayer = L.layerGroup().addTo(m);
            self._apLayer = L.layerGroup().addTo(m);
            self._bgWallLayer = L.layerGroup().addTo(m);
            self._wallLayer = L.layerGroup().addTo(m);
            self._allWalls = [];

            // Signal data cluster group with signal-based coloring
            self._signalClusterGroup = L.markerClusterGroup({
                clusterPane: 'signalDataPane',
                maxClusterRadius: 24,
                spiderfyOnMaxZoom: true,
                showCoverageOnHover: false,
                zoomToBoundsOnClick: true,
                iconCreateFunction: function (cluster) {
                    var markers = cluster.getAllChildMarkers();
                    var totalSignal = 0;
                    markers.forEach(function (mk) { totalSignal += mk.options.signalDbm || -85; });
                    var avgSignal = totalSignal / markers.length;
                    var color = self._signalColor(avgSignal);
                    return L.divIcon({
                        html: "<div class='speed-cluster' style='background:" + color + "'>" + markers.length + "</div>",
                        className: 'speed-cluster-icon',
                        iconSize: L.point(40, 40)
                    });
                }
            });
            m.addLayer(self._signalClusterGroup);

            // Spider fade and z-index management
            self._signalClusterGroup.on('clusterclick', function () {
                if (self._signalCurrentSpider) {
                    self._signalSwitchingSpider = true;
                    var markers = self._signalCurrentSpider.getAllChildMarkers();
                    markers.forEach(function (mk) {
                        if (mk._path) { mk._path.style.transition = 'opacity 0.2s ease-out'; mk._path.style.opacity = '0'; }
                        if (mk._spiderLeg && mk._spiderLeg._path) { mk._spiderLeg._path.style.transition = 'opacity 0.2s ease-out'; mk._spiderLeg._path.style.opacity = '0'; }
                    });
                }
                m.getPane('signalDataPane').style.zIndex = 650;
            });

            self._signalClusterGroup.on('spiderfied', function (e) {
                self._signalCurrentSpider = e.cluster;
                self._signalSwitchingSpider = false;
                if (e.cluster._icon) e.cluster._icon.style.pointerEvents = 'none';
                var markers = e.cluster.getAllChildMarkers();
                markers.forEach(function (mk) {
                    if (mk._spiderLeg && mk._spiderLeg._path) mk._spiderLeg._path.style.pointerEvents = 'none';
                });
            });

            self._signalClusterGroup.on('unspiderfied', function (e) {
                if (e.cluster && e.cluster._icon) e.cluster._icon.style.pointerEvents = '';
                var markers = e.cluster.getAllChildMarkers();
                markers.forEach(function (mk) {
                    if (mk._spiderLeg && mk._spiderLeg._path) mk._spiderLeg._path.style.pointerEvents = '';
                });
                self._signalCurrentSpider = null;
                if (!self._signalSwitchingSpider) m.getPane('signalDataPane').style.zIndex = 420;
            });

            // Fade spider on click outside
            m.getContainer().addEventListener('mousedown', function (e) {
                if (!self._signalCurrentSpider) return;
                var markers = self._signalCurrentSpider.getAllChildMarkers();
                var clickedOnSpider = markers.some(function (mk) {
                    return e.target === mk._path || (mk._spiderLeg && e.target === mk._spiderLeg._path);
                });
                if (clickedOnSpider) return;
                if (e.target.closest && e.target.closest('.leaflet-popup')) return;
                if (e.target.closest && e.target.closest('.speed-cluster')) return;
                if (e.target.closest && e.target.closest('.fp-ap-marker-container')) return;
                if (e.target.classList && e.target.classList.contains('leaflet-interactive')) return;
                markers.forEach(function (mk) {
                    if (mk._path) { mk._path.style.transition = 'opacity 0.2s ease-out'; mk._path.style.opacity = '0'; }
                    if (mk._spiderLeg && mk._spiderLeg._path) { mk._spiderLeg._path.style.transition = 'opacity 0.2s ease-out'; mk._spiderLeg._path.style.opacity = '0'; }
                });
            }, true);

            // Scale AP icons with zoom level
            function updateApScale() {
                var z = m.getZoom();
                var scale = Math.min(1.25, Math.max(1.0, 1.0 + (z - 20) * 0.125));
                container.style.setProperty('--fp-ap-scale', scale.toFixed(3));
            }
            m.on('zoomend', updateApScale);
            updateApScale();

            // Re-evaluate heatmap on zoom/pan (debounced)
            var heatmapTimer = null;
            m.on('moveend', function () {
                if (heatmapTimer) clearTimeout(heatmapTimer);
                heatmapTimer = setTimeout(function () {
                    if (self._dotNetRef) {
                        self._dotNetRef.invokeMethodAsync('OnMapMoveEndForHeatmap');
                    }
                }, 500);
            });

            L.control.scale({ imperial: true, metric: false, position: 'bottomleft' }).addTo(m);
        }

        init();
    },

    setDotNetRef: function (ref) {
        this._dotNetRef = ref;
    },

    // ── View ─────────────────────────────────────────────────────────

    fitBounds: function (swLat, swLng, neLat, neLng) {
        if (this._map) {
            var bounds = [[swLat, swLng], [neLat, neLng]];
            console.log('fitBounds called:', { swLat, swLng, neLat, neLng, mapSize: this._map.getSize(), currentZoom: this._map.getZoom() });
            this._map.fitBounds(bounds, { padding: [10, 10], maxZoom: 24 });
            console.log('fitBounds result:', { newZoom: this._map.getZoom(), newCenter: this._map.getCenter() });
        }
    },

    setView: function (lat, lng, zoom) {
        if (this._map) {
            this._map.setView([lat, lng], zoom);
        }
    },

    invalidateSize: function () {
        if (this._map) {
            this._map.invalidateSize();
        }
    },

    // ── Floor Overlay ────────────────────────────────────────────────

    updateFloorOverlay: function (imageUrl, swLat, swLng, neLat, neLng, opacity) {
        var m = this._map;
        if (!m) return;

        if (this._overlay) {
            m.removeLayer(this._overlay);
            this._overlay = null;
        }
        if (!imageUrl) return;

        var bounds = [[swLat, swLng], [neLat, neLng]];
        this._overlay = L.imageOverlay(imageUrl, bounds, {
            opacity: opacity, interactive: false, pane: 'fpOverlayPane'
        }).addTo(m);
    },

    setFloorOpacity: function (opacity) {
        if (this._overlay) {
            this._overlay.setOpacity(opacity);
        }
    },

    // ── AP Markers ───────────────────────────────────────────────────

    updateApMarkers: function (markersJson, draggable, band) {
        var m = this._map;
        if (!m) return;
        var self = this;
        if (band) this._heatmapBand = band;

        if (!this._apLayer) this._apLayer = L.layerGroup().addTo(m);
        if (!this._apGlowLayer) this._apGlowLayer = L.layerGroup().addTo(m);

        // Track which AP popup is open so we can restore it
        var openPopupMac = null;
        this._apLayer.eachLayer(function (layer) {
            if (layer.getPopup && layer.getPopup() && layer.getPopup().isOpen()) {
                openPopupMac = layer._apMac;
            }
        });

        this._apLayer.clearLayers();
        this._apGlowLayer.clearLayers();

        var aps = JSON.parse(markersJson);
        var reopenMarker = null;

        aps.forEach(function (ap) {
            // Glow layer (behind icons)
            var glowIcon = L.divIcon({
                className: 'fp-ap-glow-container',
                html: '<div class="fp-ap-glow-dot' + (ap.sameFloor ? '' : ' other-floor') + '"></div>',
                iconSize: [48, 48], iconAnchor: [24, 24]
            });
            var glowMarker = L.marker([ap.lat, ap.lng], {
                icon: glowIcon, interactive: false, pane: 'apGlowPane'
            }).addTo(self._apGlowLayer);

            // Icon layer with orientation arrow
            var opacity = ap.online ? (ap.sameFloor ? 1.0 : 0.35) : (ap.sameFloor ? 0.4 : 0.2);
            var arrowHtml = ap.sameFloor
                ? '<div class="fp-ap-direction" style="transform:rotate(' + ap.orientation + 'deg)"><div class="fp-ap-arrow"></div></div>'
                : '';
            var icon = L.divIcon({
                className: 'fp-ap-marker-container',
                html: arrowHtml + '<img src="' + ap.iconUrl + '" class="fp-ap-marker-icon" style="opacity:' + opacity + '" />',
                iconSize: [32, 32], iconAnchor: [16, 16], popupAnchor: [0, -16]
            });
            var marker = L.marker([ap.lat, ap.lng], {
                icon: icon, draggable: draggable && ap.sameFloor, pane: 'apIconPane'
            }).addTo(self._apLayer);
            marker._apMac = ap.mac;

            // Popup with floor selector and orientation input
            var floorOpts = '';
            for (var fi = -2; fi <= 5; fi++) {
                floorOpts += '<option value="' + fi + '"' + (fi === ap.floor ? ' selected' : '') + '>' +
                    (fi <= 0 ? 'B' + Math.abs(fi - 1) : fi === 1 ? '1st' : fi === 2 ? '2nd' : fi === 3 ? '3rd' : fi + 'th') +
                    ' Floor</option>';
            }

            // Mount type dropdown options
            var mountTypes = ['ceiling', 'wall', 'desktop'];
            var mountLabels = { ceiling: 'Ceiling', wall: 'Wall / Pole', desktop: 'Desktop' };
            var mountOpts = '';
            mountTypes.forEach(function (mt) {
                mountOpts += '<option value="' + mt + '"' + (mt === (ap.mountType || 'ceiling') ? ' selected' : '') + '>' + mountLabels[mt] + '</option>';
            });

            // Find the radio matching the active heatmap band
            var bandMap = { '2.4': 'ng', '5': 'na', '6': '6e' };
            var activeRadioCode = bandMap[self._heatmapBand] || 'na';
            var activeRadio = (ap.radios || []).find(function (r) { return r.radioCode === activeRadioCode; });

            // TX power slider section (keyed by mac:band for band independence)
            var txPowerHtml = '';
            if (activeRadio && activeRadio.txPowerDbm != null) {
                var macKey = ap.mac.toLowerCase();
                var overrideKey = macKey + ':' + self._heatmapBand;
                var currentPower = (self._txPowerOverrides[overrideKey] != null) ? self._txPowerOverrides[overrideKey] : activeRadio.txPowerDbm;
                var minPower = activeRadio.minTxPower || 1;
                var maxPower = activeRadio.maxTxPower || activeRadio.txPowerDbm;
                var isOverridden = self._txPowerOverrides[overrideKey] != null;
                txPowerHtml =
                    '<div class="fp-ap-popup-divider"></div>' +
                    '<div class="fp-ap-popup-section-label">Simulate</div>' +
                    '<div class="fp-ap-popup-row"><label>TX Power</label>' +
                    '<input type="range" data-tx-slider min="' + minPower + '" max="' + maxPower + '" value="' + currentPower + '" ' +
                    'oninput="this.nextElementSibling.textContent=this.value+\' dBm\'" ' +
                    'onchange="fpEditor._txPowerOverrides[\'' + overrideKey + '\']=parseInt(this.value);this.nextElementSibling.classList.add(\'overridden\');fpEditor._updateResetSimBtn();fpEditor.computeHeatmap()" />' +
                    '<span class="fp-ap-popup-power' + (isOverridden ? ' overridden' : '') + '">' + currentPower + ' dBm</span></div>';
            }

            marker.bindPopup(
                '<div class="fp-ap-popup">' +
                '<div class="fp-ap-popup-name">' + (ap.name || ap.mac) + '</div>' +
                '<div class="fp-ap-popup-model">' + ap.model + ' \u00b7 ' + ap.clients + ' client' + (ap.clients !== 1 ? 's' : '') + '</div>' +
                '<div class="fp-ap-popup-rows">' +
                '<div class="fp-ap-popup-row"><label>Floor</label>' +
                '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApFloorChangedFromJs\',\'' + ap.mac + '\',parseInt(this.value))">' +
                floorOpts + '</select></div>' +
                '<div class="fp-ap-popup-row"><label>Mount</label>' +
                '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApMountTypeChangedFromJs\',\'' + ap.mac + '\',this.value)">' +
                mountOpts + '</select></div>' +
                '<div class="fp-ap-popup-row"><label>Facing</label>' +
                '<input type="range" min="0" max="359" value="' + ap.orientation + '" ' +
                'oninput="this.nextElementSibling.textContent=this.value+\'\u00B0\'" ' +
                'onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApOrientationChangedFromJs\',\'' + ap.mac + '\',parseInt(this.value))" />' +
                '<span class="fp-ap-popup-deg">' + ap.orientation + '\u00B0</span></div>' +
                txPowerHtml +
                '</div></div>'
            );

            // Sync slider with current override each time popup opens
            (function (macAddr) {
                marker.on('popupopen', function () {
                    var key = macAddr.toLowerCase() + ':' + self._heatmapBand;
                    var override = self._txPowerOverrides[key];
                    if (override == null) return;
                    var el = marker.getPopup() && marker.getPopup().getElement();
                    if (!el) return;
                    var slider = el.querySelector('[data-tx-slider]');
                    if (!slider) return;
                    slider.value = override;
                    var label = slider.nextElementSibling;
                    if (label) {
                        label.textContent = override + ' dBm';
                        label.classList.add('overridden');
                    }
                });
            })(ap.mac);

            if (openPopupMac === ap.mac) {
                reopenMarker = marker;
            }

            if (draggable && ap.sameFloor) {
                (function (gm) {
                    marker.on('drag', function (e) {
                        gm.setLatLng(e.target.getLatLng());
                    });
                    marker.on('dragend', function (e) {
                        var pos = e.target.getLatLng();
                        gm.setLatLng(pos);
                        self._dotNetRef.invokeMethodAsync('OnApDragEndFromJs', ap.mac, pos.lat, pos.lng);
                    });
                })(glowMarker);
            }
        });

        // Reopen popup if one was open before rebuild
        if (reopenMarker) {
            reopenMarker.openPopup();
        }
    },

    _updateResetSimBtn: function () {
        var btn = document.getElementById('fp-reset-sim-btn');
        if (btn) btn.style.display = Object.keys(this._txPowerOverrides).length > 0 ? '' : 'none';
    },

    resetSimulation: function () {
        this._txPowerOverrides = {};
        this._updateResetSimBtn();
        this.computeHeatmap();
    },

    // ── AP Placement Mode ────────────────────────────────────────────

    setPlacementMode: function (enabled) {
        var m = this._map;
        if (!m) return;
        var self = this;

        if (this._placementHandler) {
            m.off('click', this._placementHandler);
            this._placementHandler = null;
        }

        if (enabled) {
            this._placementHandler = function (e) {
                self._dotNetRef.invokeMethodAsync('OnMapClickForPlacement', e.latlng.lat, e.latlng.lng);
            };
            m.on('click', this._placementHandler);
            m.getContainer().style.cursor = 'crosshair';
        } else {
            m.getContainer().style.cursor = '';
        }
    },

    // ── Background Walls (faded, non-interactive) ─────────────────────

    updateBackgroundWalls: function (wallsJson, colorsJson) {
        var m = this._map;
        if (!m) return;
        var self = this;

        if (!this._bgWallLayer) this._bgWallLayer = L.layerGroup().addTo(m);
        this._bgWallLayer.clearLayers();

        var walls = JSON.parse(wallsJson);
        var colors = JSON.parse(colorsJson);
        this._bgWalls = walls;

        walls.forEach(function (wall) {
            for (var i = 0; i < wall.points.length - 1; i++) {
                var mat = (wall.materials && i < wall.materials.length) ? wall.materials[i] : wall.material;
                var color = colors[mat] || '#94a3b8';
                L.polyline(
                    [[wall.points[i].lat, wall.points[i].lng], [wall.points[i + 1].lat, wall.points[i + 1].lng]],
                    { color: color, weight: 2, opacity: 0.3, pane: 'bgWallPane', interactive: false }
                ).addTo(self._bgWallLayer);
            }
        });
    },

    // ── Wall Rendering ───────────────────────────────────────────────

    updateWalls: function (wallsJson, colorsJson, labelsJson) {
        var m = this._map;
        if (!m) return;
        var self = this;

        if (!this._wallLayer) this._wallLayer = L.layerGroup().addTo(m);
        this._wallLayer.clearLayers();
        if (this._wallHighlightLayer) {
            this._wallHighlightLayer.clearLayers();
        } else {
            this._wallHighlightLayer = L.layerGroup().addTo(m);
        }

        var walls = JSON.parse(wallsJson);
        var colors = JSON.parse(colorsJson);
        var labels = JSON.parse(labelsJson);
        this._allWalls = walls;
        this._materialLabels = labels;
        this._materialColors = colors;
        this._wallSelection = { wallIdx: null, segIdx: null };

        // Per-segment rendering
        walls.forEach(function (wall, wi) {
            for (var i = 0; i < wall.points.length - 1; i++) {
                var mat = (wall.materials && i < wall.materials.length) ? wall.materials[i] : wall.material;
                var color = colors[mat] || '#94a3b8';
                var seg = L.polyline(
                    [[wall.points[i].lat, wall.points[i].lng], [wall.points[i + 1].lat, wall.points[i + 1].lng]],
                    { color: color, weight: 4, opacity: 0.9, pane: 'wallPane', interactive: true }
                ).addTo(self._wallLayer);
                seg._fpWallIdx = wi;
                seg._fpSegIdx = i;
                seg.on('click', function (e) {
                    L.DomEvent.stopPropagation(e);
                    self._wallSegClick(e, this._fpWallIdx, this._fpSegIdx);
                });

                // Length labels
                var p1 = wall.points[i];
                var p2 = wall.points[i + 1];
                var d = m.distance(L.latLng(p1.lat, p1.lng), L.latLng(p2.lat, p2.lng));
                var ft = d * 3.28084;
                var label = ft < 100 ? ft.toFixed(1) + "'" : Math.round(ft) + "'";
                var midLat = (p1.lat + p2.lat) / 2;
                var midLng = (p1.lng + p2.lng) / 2;
                L.marker([midLat, midLng], {
                    icon: L.divIcon({ className: 'fp-wall-length', html: label, iconSize: [50, 18], iconAnchor: [25, 9] }),
                    interactive: false
                }).addTo(self._wallLayer);
            }

            // Vertex dots
            var mainColor = colors[wall.material] || '#94a3b8';
            wall.points.forEach(function (p) {
                L.circleMarker([p.lat, p.lng], {
                    radius: 3, color: mainColor, fillColor: '#fff', fillOpacity: 1, weight: 2, interactive: false
                }).addTo(self._wallLayer);
            });
        });

        // Click on map (not on wall) clears selection
        if (!this._wallMapClickBound) {
            this._wallMapClickBound = true;
            m.on('click', function () {
                self._wallHighlightLayer.clearLayers();
                self._wallSelection = { wallIdx: null, segIdx: null };
            });
        }
    },

    // Two-level wall segment click handler
    _wallSegClick: function (e, wi, si) {
        var m = this._map;
        var self = this;
        var sel = this._wallSelection;
        var wall = this._allWalls[wi];
        var labels = this._materialLabels;
        this._wallHighlightLayer.clearLayers();
        m.closePopup();

        // Level 1: click a different wall -> select whole shape
        if (sel.wallIdx !== wi || sel.segIdx !== null) {
            if (sel.wallIdx !== wi) {
                // Select entire wall shape - highlight all segments with dashed blue
                for (var j = 0; j < wall.points.length - 1; j++) {
                    L.polyline(
                        [[wall.points[j].lat, wall.points[j].lng], [wall.points[j + 1].lat, wall.points[j + 1].lng]],
                        { color: '#60a5fa', weight: 6, dashArray: '8,4', opacity: 0.9, interactive: false }
                    ).addTo(this._wallHighlightLayer);
                }
                this._wallSelection = { wallIdx: wi, segIdx: null };

                // Popup with material dropdown for whole shape and delete button
                var wallOpts = '';
                for (var wk in labels) {
                    wallOpts += '<option value="' + wk + '"' + (wk === wall.material ? ' selected' : '') + '>' + labels[wk] + '</option>';
                }
                var wallHtml = '<div style="text-align:center;min-width:180px">' +
                    '<select style="width:100%;padding:3px;margin-bottom:6px;background:#1e293b;color:#e0e0e0;border:1px solid #475569;border-radius:3px" ' +
                    'onchange="fpEditor.changeWallMat(' + wi + ',this.value)">' +
                    wallOpts + '</select><br/>' +
                    '<span style="font-size:11px;color:#94a3b8">' + (wall.points.length - 1) + ' segment' + (wall.points.length > 2 ? 's' : '') + '</span><br/>' +
                    '<button onclick="fpEditor.deleteWall(' + wi + ')" style="margin-top:4px;padding:2px 12px;background:#dc2626;color:#fff;border:none;border-radius:3px;cursor:pointer">Delete Shape</button></div>';
                L.popup({ closeButton: true }).setLatLng(e.latlng).setContent(wallHtml).openOn(m);
                return;
            }
        }

        // Level 2: click segment of already-selected wall -> select that segment
        L.polyline(
            [[wall.points[si].lat, wall.points[si].lng], [wall.points[si + 1].lat, wall.points[si + 1].lng]],
            { color: '#facc15', weight: 8, opacity: 0.9, interactive: false }
        ).addTo(this._wallHighlightLayer);
        this._wallSelection = { wallIdx: wi, segIdx: si };

        // Build material dropdown options
        var segMat = (wall.materials && si < wall.materials.length) ? wall.materials[si] : wall.material;
        var opts = '';
        for (var k in labels) {
            opts += '<option value="' + k + '"' + (k === segMat ? ' selected' : '') + '>' + labels[k] + '</option>';
        }

        // Popup with material dropdown, split, and delete buttons
        var html = '<div style="text-align:center;min-width:180px">' +
            '<select style="width:100%;padding:3px;margin-bottom:6px;background:#1e293b;color:#e0e0e0;border:1px solid #475569;border-radius:3px" ' +
            'onchange="fpEditor.changeSegMat(' + wi + ',' + si + ',this.value)">' +
            opts + '</select><br/>' +
            '<button onclick="fpEditor.splitSeg(' + wi + ',' + si + ')" style="padding:2px 10px;background:#4f46e5;color:#fff;border:none;border-radius:3px;cursor:pointer;margin-right:4px">Split</button>' +
            '<button onclick="fpEditor.deleteSeg(' + wi + ',' + si + ')" style="padding:2px 10px;background:#dc2626;color:#fff;border:none;border-radius:3px;cursor:pointer">Delete Seg</button></div>';
        L.popup({ closeButton: true }).setLatLng(e.latlng).setContent(html).openOn(m);
    },

    // ── Wall Operations (called from popup onclick) ──────────────────

    deleteWall: function (idx) {
        if (this._allWalls && idx >= 0 && idx < this._allWalls.length) {
            this._allWalls.splice(idx, 1);
            this._map.closePopup();
            this._wallSelection = { wallIdx: null, segIdx: null };
            if (this._wallHighlightLayer) this._wallHighlightLayer.clearLayers();
            this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
        }
    },

    changeWallMat: function (wi, mat) {
        var wall = this._allWalls[wi];
        if (!wall) return;
        wall.material = mat;
        if (wall.materials) {
            for (var k = 0; k < wall.materials.length; k++) wall.materials[k] = mat;
        }
        this._map.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        if (this._wallHighlightLayer) this._wallHighlightLayer.clearLayers();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    changeSegMat: function (wi, si, mat) {
        var wall = this._allWalls[wi];
        if (!wall) return;
        if (!wall.materials) {
            wall.materials = [];
            for (var k = 0; k < wall.points.length - 1; k++) wall.materials.push(wall.material);
        }
        wall.materials[si] = mat;
        var allSame = true;
        for (var k2 = 0; k2 < wall.materials.length; k2++) {
            if (wall.materials[k2] !== mat) { allSame = false; break; }
        }
        if (allSame) wall.material = mat;
        this._map.closePopup();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    splitSeg: function (wi, si) {
        var wall = this._allWalls[wi];
        if (!wall) return;
        var p1 = wall.points[si];
        var p2 = wall.points[si + 1];
        var mid = { lat: (p1.lat + p2.lat) / 2, lng: (p1.lng + p2.lng) / 2 };
        wall.points.splice(si + 1, 0, mid);
        if (wall.materials) {
            wall.materials.splice(si, 0, wall.materials[si]);
        }
        this._map.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    deleteSeg: function (wi, si) {
        var wall = this._allWalls[wi];
        if (!wall) return;
        // If only 1 segment (2 points), delete the entire wall
        if (wall.points.length <= 2) { this.deleteWall(wi); return; }
        // First segment: remove first point
        if (si === 0) {
            wall.points.splice(0, 1);
            if (wall.materials) wall.materials.splice(0, 1);
        }
        // Last segment: remove last point
        else if (si === wall.points.length - 2) {
            wall.points.splice(wall.points.length - 1, 1);
            if (wall.materials) wall.materials.splice(si, 1);
        }
        // Middle segment: split into two separate walls
        else {
            var wall1 = { points: wall.points.slice(0, si + 1), material: wall.material };
            var wall2 = { points: wall.points.slice(si + 1), material: wall.material };
            if (wall.materials) {
                wall1.materials = wall.materials.slice(0, si);
                wall2.materials = wall.materials.slice(si + 1);
            }
            this._allWalls.splice(wi, 1, wall1, wall2);
        }
        this._map.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    deleteLastWall: function () {
        if (!this._allWalls || this._allWalls.length === 0) return;
        this._allWalls.pop();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    // Snap to nearby vertices from existing walls and background walls (adjacent floors)
    // Snap to nearby wall vertices (priority) or perpendicular projection onto wall segments.
    // Returns { lat, lng, type: 'vertex'|'segment', segA, segB } or null.
    // segA/segB are the segment endpoints (only for type='segment').
    _snapToVertex: function (lat, lng, snapPixels) {
        var m = this._map;
        if (!m) return null;
        var mousePixel = m.latLngToContainerPoint(L.latLng(lat, lng));
        var bestVertexDist = snapPixels;
        var bestVertexPt = null;
        var bestSegDist = snapPixels;
        var bestSegPt = null;

        function checkWalls(walls) {
            if (!walls) return;
            for (var wi = 0; wi < walls.length; wi++) {
                var pts = walls[wi].points;
                if (!pts) continue;
                // Check vertices
                for (var pi = 0; pi < pts.length; pi++) {
                    var px = m.latLngToContainerPoint(L.latLng(pts[pi].lat, pts[pi].lng));
                    var d = mousePixel.distanceTo(px);
                    if (d < bestVertexDist) {
                        bestVertexDist = d;
                        bestVertexPt = { lat: pts[pi].lat, lng: pts[pi].lng, type: 'vertex' };
                    }
                }
                // Check perpendicular projection onto each segment
                for (var si = 0; si < pts.length - 1; si++) {
                    var aPx = m.latLngToContainerPoint(L.latLng(pts[si].lat, pts[si].lng));
                    var bPx = m.latLngToContainerPoint(L.latLng(pts[si + 1].lat, pts[si + 1].lng));
                    var dx = bPx.x - aPx.x, dy = bPx.y - aPx.y;
                    var len2 = dx * dx + dy * dy;
                    if (len2 < 1) continue;
                    var t = ((mousePixel.x - aPx.x) * dx + (mousePixel.y - aPx.y) * dy) / len2;
                    if (t < 0.01 || t > 0.99) continue;
                    var projPx = L.point(aPx.x + t * dx, aPx.y + t * dy);
                    var dist = mousePixel.distanceTo(projPx);
                    if (dist < bestSegDist) {
                        bestSegDist = dist;
                        bestSegPt = {
                            lat: pts[si].lat + t * (pts[si + 1].lat - pts[si].lat),
                            lng: pts[si].lng + t * (pts[si + 1].lng - pts[si].lng),
                            type: 'segment',
                            segA: { lat: pts[si].lat, lng: pts[si].lng },
                            segB: { lat: pts[si + 1].lat, lng: pts[si + 1].lng }
                        };
                    }
                }
            }
        }

        checkWalls(this._allWalls);
        checkWalls(this._bgWalls);
        return bestVertexPt || bestSegPt;
    },

    // Show live segment length label at midpoint of preview line
    _updatePreviewLength: function (from, to) {
        var m = this._map;
        if (!m) return;
        var d = m.distance(L.latLng(from.lat, from.lng), L.latLng(to.lat, to.lng));
        var ft = d * 3.28084;
        var label = ft < 100 ? ft.toFixed(1) + "'" : Math.round(ft) + "'";
        var midLat = (from.lat + to.lat) / 2;
        var midLng = (from.lng + to.lng) / 2;
        if (!this._previewLengthLabel) {
            this._previewLengthLabel = L.marker([midLat, midLng], {
                icon: L.divIcon({ className: 'fp-wall-length fp-wall-length-live', html: label, iconSize: [50, 18], iconAnchor: [25, 9] }),
                interactive: false
            }).addTo(m);
        } else {
            this._previewLengthLabel.setLatLng([midLat, midLng]);
            this._previewLengthLabel.setIcon(L.divIcon({ className: 'fp-wall-length fp-wall-length-live', html: label, iconSize: [50, 18], iconAnchor: [25, 9] }));
        }
    },

    // ── Wall Drawing Mode ────────────────────────────────────────────

    enterDrawMode: function (wallsJson) {
        var m = this._map;
        if (!m) return;
        var self = this;

        this._allWalls = JSON.parse(wallsJson);
        m.dragging.disable();
        m.getContainer().style.cursor = 'crosshair';

        this._refAngle = null;

        // Snap point to reference angle or perpendicular
        this._snapPoint = function (prev, lat, lng, shiftKey) {
            if (shiftKey) return { lat: lat, lng: lng };
            var cosLat = Math.cos(prev.lat * Math.PI / 180);
            var dx = (lng - prev.lng) * cosLat;
            var dy = lat - prev.lat;
            var dist = Math.sqrt(dx * dx + dy * dy);
            if (dist < 1e-10) return { lat: lat, lng: lng };

            // No reference angle yet: first segment is free-form
            if (self._refAngle === null) return { lat: lat, lng: lng };

            var angle = self._refAngle;
            var perpAngle = angle + Math.PI / 2;
            var ca = Math.cos(angle), sa = Math.sin(angle);
            var cp = Math.cos(perpAngle), sp = Math.sin(perpAngle);
            var projRef = dx * ca + dy * sa;
            var projPerp = dx * cp + dy * sp;

            if (Math.abs(projRef) >= Math.abs(projPerp)) {
                var sLen = Math.abs(projRef);
                var sDir = projRef >= 0 ? 1 : -1;
                var snLen = self._snapLength(sLen, true);
                if (snLen !== null) sLen = snLen;
                return { lat: prev.lat + sDir * sLen * sa, lng: prev.lng + sDir * sLen * ca / cosLat };
            } else {
                var sLen2 = Math.abs(projPerp);
                var sDir2 = projPerp >= 0 ? 1 : -1;
                var snLen2 = self._snapLength(sLen2, false);
                if (snLen2 !== null) sLen2 = snLen2;
                return { lat: prev.lat + sDir2 * sLen2 * sp, lng: prev.lng + sDir2 * sLen2 * cp / cosLat };
            }
        };

        // Length snap: find matching parallel segment lengths in the current wall
        this._snapLength = function (curLen, isRefDir) {
            if (!self._currentWall || self._currentWall.points.length < 2 || self._refAngle === null) return null;
            var pts = self._currentWall.points;
            var refA = self._refAngle;

            for (var j = 0; j < pts.length - 1; j++) {
                var sCosLat = Math.cos(pts[j].lat * Math.PI / 180);
                var sdx = (pts[j + 1].lng - pts[j].lng) * sCosLat;
                var sdy = pts[j + 1].lat - pts[j].lat;
                var pRef = Math.abs(sdx * Math.cos(refA) + sdy * Math.sin(refA));
                var pPerp = Math.abs(sdx * Math.cos(refA + Math.PI / 2) + sdy * Math.sin(refA + Math.PI / 2));
                var segIsRef = pRef >= pPerp;
                if (segIsRef !== isRefDir) continue;
                var segLen = m.distance(L.latLng(pts[j].lat, pts[j].lng), L.latLng(pts[j + 1].lat, pts[j + 1].lng));
                var curMeters = curLen * 111320;
                var diff = Math.abs(curMeters - segLen);
                if (diff < 1.0 && diff > 0.01) return curLen * (segLen / curMeters);
            }
            return null;
        };

        // Click handler
        this._wallClickHandler = function (e) {
            // Close-shape snap
            if (self._snapToClose && self._currentWall && self._currentWall.points.length >= 3) {
                var fp = self._currentWall.points[0];
                self._currentWall.points.push({ lat: fp.lat, lng: fp.lng });
                self._currentWallLine.addLatLng([fp.lat, fp.lng]);
                self._closedBySnap = true;
                self._dotNetRef.invokeMethodAsync('OnMapDblClickFinishWall');
                return;
            }

            var lat = e.latlng.lat, lng = e.latlng.lng;

            // Vertex snap: snap to nearby existing wall vertices (10px threshold)
            if (!e.originalEvent.shiftKey) {
                var vtxSnap = self._snapToVertex(lat, lng, 10);
                if (vtxSnap) {
                    lat = vtxSnap.lat;
                    lng = vtxSnap.lng;
                }
            }

            if (!e.originalEvent.shiftKey && self._currentWall && self._currentWall.points.length > 0) {
                var prev = self._currentWall.points[self._currentWall.points.length - 1];
                // Only apply angle snap if we didn't vertex-snap
                if (!self._snapToVertex(e.latlng.lat, e.latlng.lng, 10)) {
                    var snapped = self._snapPoint(prev, lat, lng, false);
                    lat = snapped.lat;
                    lng = snapped.lng;
                }
                // Set reference angle from first segment
                if (self._refAngle === null && self._currentWall.points.length === 1) {
                    var cosLat2 = Math.cos(prev.lat * Math.PI / 180);
                    var dx2 = (lng - prev.lng) * cosLat2;
                    var dy2 = lat - prev.lat;
                    self._refAngle = Math.atan2(dy2, dx2);
                }
            }
            self._dotNetRef.invokeMethodAsync('OnMapClickForWall', lat, lng);
        };

        // Double-click handler
        this._wallDblClickHandler = function (e) {
            L.DomEvent.stopPropagation(e);
            L.DomEvent.preventDefault(e);
            self._dotNetRef.invokeMethodAsync('OnMapDblClickFinishWall');
        };

        m.on('click', this._wallClickHandler);
        m.on('dblclick', this._wallDblClickHandler);
        m.doubleClickZoom.disable();

        // Preview line (rubber-band from last point to cursor) with snap + close-shape snap
        this._previewLine = null;
        this._snapToClose = false;
        this._snapIndicator = null;

        this._wallMoveHandler = function (e) {
            // Show vertex snap indicator even before first point is placed
            if (!self._currentWall || self._currentWall.points.length === 0) {
                var earlySnap = e.originalEvent.shiftKey ? null : self._snapToVertex(e.latlng.lat, e.latlng.lng, 10);
                if (earlySnap) {
                    if (!self._snapIndicator) {
                        self._snapIndicator = L.marker([earlySnap.lat, earlySnap.lng], {
                            icon: L.divIcon({ className: 'fp-snap-indicator', iconSize: [20, 20], iconAnchor: [10, 10] }),
                            interactive: false
                        }).addTo(m);
                    } else {
                        self._snapIndicator.setLatLng([earlySnap.lat, earlySnap.lng]);
                    }
                } else if (self._snapIndicator) {
                    m.removeLayer(self._snapIndicator);
                    self._snapIndicator = null;
                }
                return;
            }
            var prev = self._currentWall.points[self._currentWall.points.length - 1];

            // Close-shape snap: if 3+ points and cursor is within 15px of first point
            var closeSnap = false;
            if (self._currentWall.points.length >= 3) {
                var fp2 = self._currentWall.points[0];
                var mousePixel = m.latLngToContainerPoint(e.latlng);
                var firstPixel = m.latLngToContainerPoint(L.latLng(fp2.lat, fp2.lng));
                if (mousePixel.distanceTo(firstPixel) < 15) closeSnap = true;
            }
            self._snapToClose = closeSnap;

            if (closeSnap) {
                var fp3 = self._currentWall.points[0];
                if (!self._previewLine) {
                    self._previewLine = L.polyline([[prev.lat, prev.lng], [fp3.lat, fp3.lng]], {
                        color: '#60a5fa', weight: 2, dashArray: '6,4', opacity: 0.8
                    }).addTo(m);
                } else {
                    self._previewLine.setLatLngs([[prev.lat, prev.lng], [fp3.lat, fp3.lng]]);
                }
                if (!self._snapIndicator) {
                    self._snapIndicator = L.marker([fp3.lat, fp3.lng], {
                        icon: L.divIcon({ className: 'fp-snap-indicator', iconSize: [20, 20], iconAnchor: [10, 10] }),
                        interactive: false
                    }).addTo(m);
                }
                if (self._snapGuideLine) { m.removeLayer(self._snapGuideLine); self._snapGuideLine = null; }
                if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }
                return;
            }

            // Vertex/segment snap: check for nearby existing wall vertices or perpendicular projection
            var vtxSnap = e.originalEvent.shiftKey ? null : self._snapToVertex(e.latlng.lat, e.latlng.lng, 10);
            if (vtxSnap) {
                // Preview line: solid when segment snap (locked 90°), dashed for vertex
                var lineStyle = vtxSnap.type === 'segment'
                    ? { color: '#22c55e', weight: 2, dashArray: null, opacity: 0.9 }
                    : { color: '#60a5fa', weight: 2, dashArray: '6,4', opacity: 0.8 };
                if (!self._previewLine) {
                    self._previewLine = L.polyline([[prev.lat, prev.lng], [vtxSnap.lat, vtxSnap.lng]], lineStyle).addTo(m);
                } else {
                    self._previewLine.setLatLngs([[prev.lat, prev.lng], [vtxSnap.lat, vtxSnap.lng]]);
                    self._previewLine.setStyle(lineStyle);
                }
                // Snap indicator dot
                if (!self._snapIndicator) {
                    self._snapIndicator = L.marker([vtxSnap.lat, vtxSnap.lng], {
                        icon: L.divIcon({ className: 'fp-snap-indicator', iconSize: [20, 20], iconAnchor: [10, 10] }),
                        interactive: false
                    }).addTo(m);
                } else {
                    self._snapIndicator.setLatLng([vtxSnap.lat, vtxSnap.lng]);
                }
                // Segment snap: show guide line along the target wall + right angle marker
                if (vtxSnap.type === 'segment' && vtxSnap.segA && vtxSnap.segB) {
                    if (!self._snapGuideLine) {
                        self._snapGuideLine = L.polyline(
                            [[vtxSnap.segA.lat, vtxSnap.segA.lng], [vtxSnap.segB.lat, vtxSnap.segB.lng]],
                            { color: '#22c55e', weight: 2, dashArray: '4,4', opacity: 0.5, interactive: false }
                        ).addTo(m);
                    } else {
                        self._snapGuideLine.setLatLngs([[vtxSnap.segA.lat, vtxSnap.segA.lng], [vtxSnap.segB.lat, vtxSnap.segB.lng]]);
                    }
                    // Right angle marker: small L-shape at the snap point
                    var sp = m.latLngToContainerPoint(L.latLng(vtxSnap.lat, vtxSnap.lng));
                    var ap = m.latLngToContainerPoint(L.latLng(vtxSnap.segA.lat, vtxSnap.segA.lng));
                    var bp = m.latLngToContainerPoint(L.latLng(vtxSnap.segB.lat, vtxSnap.segB.lng));
                    var wdx = bp.x - ap.x, wdy = bp.y - ap.y;
                    var wlen = Math.sqrt(wdx * wdx + wdy * wdy);
                    if (wlen > 0) {
                        var ux = wdx / wlen, uy = wdy / wlen; // unit along wall
                        // Perpendicular direction (toward the drawing point)
                        var pp = m.latLngToContainerPoint(L.latLng(prev.lat, prev.lng));
                        var perpSide = (pp.x - sp.x) * (-uy) + (pp.y - sp.y) * ux;
                        var nx = perpSide >= 0 ? -uy : uy;
                        var ny = perpSide >= 0 ? ux : -ux;
                        var sz = 8; // right angle marker size in pixels
                        var c1 = m.containerPointToLatLng(L.point(sp.x + ux * sz, sp.y + uy * sz));
                        var c2 = m.containerPointToLatLng(L.point(sp.x + ux * sz + nx * sz, sp.y + uy * sz + ny * sz));
                        var c3 = m.containerPointToLatLng(L.point(sp.x + nx * sz, sp.y + ny * sz));
                        if (!self._snapAngleMarker) {
                            self._snapAngleMarker = L.polyline(
                                [[c1.lat, c1.lng], [c2.lat, c2.lng], [c3.lat, c3.lng]],
                                { color: '#22c55e', weight: 1.5, opacity: 0.9, interactive: false }
                            ).addTo(m);
                        } else {
                            self._snapAngleMarker.setLatLngs([[c1.lat, c1.lng], [c2.lat, c2.lng], [c3.lat, c3.lng]]);
                        }
                    }
                } else {
                    if (self._snapGuideLine) { m.removeLayer(self._snapGuideLine); self._snapGuideLine = null; }
                    if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }
                }
                self._updatePreviewLength(prev, vtxSnap);
                return;
            }

            // Remove snap indicators if not snapping
            if (self._snapIndicator) { m.removeLayer(self._snapIndicator); self._snapIndicator = null; }
            if (self._snapGuideLine) { m.removeLayer(self._snapGuideLine); self._snapGuideLine = null; }
            if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }

            var snapped = self._snapPoint(prev, e.latlng.lat, e.latlng.lng, e.originalEvent.shiftKey);
            var lat = snapped.lat, lng = snapped.lng;
            if (!self._previewLine) {
                self._previewLine = L.polyline([[prev.lat, prev.lng], [lat, lng]], {
                    color: '#818cf8', weight: 2, dashArray: '6,4', opacity: 0.8
                }).addTo(m);
            } else {
                self._previewLine.setLatLngs([[prev.lat, prev.lng], [lat, lng]]);
            }
            self._updatePreviewLength(prev, { lat: lat, lng: lng });
        };

        m.on('mousemove', this._wallMoveHandler);
    },

    exitDrawMode: function () {
        var m = this._map;
        if (!m) return;

        // Commit any in-progress wall before exiting draw mode
        if (this._currentWall && this._currentWall.points.length >= 2) {
            if (!this._allWalls) this._allWalls = [];
            this._allWalls.push(this._currentWall);
            this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
        }

        m.dragging.enable();
        m.getContainer().style.cursor = '';
        if (this._wallClickHandler) { m.off('click', this._wallClickHandler); this._wallClickHandler = null; }
        if (this._wallDblClickHandler) { m.off('dblclick', this._wallDblClickHandler); this._wallDblClickHandler = null; }
        if (this._wallMoveHandler) { m.off('mousemove', this._wallMoveHandler); this._wallMoveHandler = null; }
        if (this._previewLine) { m.removeLayer(this._previewLine); this._previewLine = null; }
        if (this._snapIndicator) { m.removeLayer(this._snapIndicator); this._snapIndicator = null; }
        if (this._snapGuideLine) { m.removeLayer(this._snapGuideLine); this._snapGuideLine = null; }
        if (this._snapAngleMarker) { m.removeLayer(this._snapAngleMarker); this._snapAngleMarker = null; }
        if (this._previewLengthLabel) { m.removeLayer(this._previewLengthLabel); this._previewLengthLabel = null; }
        m.doubleClickZoom.enable();
        this._currentWall = null;
        this._refAngle = null;
        if (this._currentWallLine) { this._currentWallLine.remove(); this._currentWallLine = null; }
        if (this._currentWallVertices) { m.removeLayer(this._currentWallVertices); this._currentWallVertices = null; }
        if (this._currentWallLabels) { m.removeLayer(this._currentWallLabels); this._currentWallLabels = null; }
    },

    addWallPoint: function (lat, lng, material, color) {
        var m = this._map;
        if (!m) return;

        if (!this._currentWall) {
            this._currentWall = { points: [], material: material };
            this._currentWallLine = L.polyline([], { color: color, weight: 4, opacity: 0.9 }).addTo(this._wallLayer || m);
            this._currentWallVertices = L.layerGroup().addTo(m);
            this._currentWallLabels = L.layerGroup().addTo(m);
        }

        // Add vertex dot
        L.circleMarker([lat, lng], {
            radius: 5, color: color, fillColor: '#fff', fillOpacity: 1, weight: 2
        }).addTo(this._currentWallVertices);

        this._currentWall.points.push({ lat: lat, lng: lng });
        this._currentWallLine.addLatLng([lat, lng]);

        // Show segment length label (imperial - feet)
        var pts = this._currentWall.points;
        if (pts.length >= 2) {
            var p1 = pts[pts.length - 2];
            var p2 = pts[pts.length - 1];
            var d = m.distance(L.latLng(p1.lat, p1.lng), L.latLng(p2.lat, p2.lng));
            var ft = d * 3.28084;
            var label = ft < 100 ? ft.toFixed(1) + "'" : Math.round(ft) + "'";
            var midLat = (p1.lat + p2.lat) / 2;
            var midLng = (p1.lng + p2.lng) / 2;
            L.marker([midLat, midLng], {
                icon: L.divIcon({ className: 'fp-wall-length', html: label, iconSize: [50, 18], iconAnchor: [25, 9] }),
                interactive: false
            }).addTo(this._currentWallLabels);
        }

        // Reset preview line and length label
        if (this._previewLine) { m.removeLayer(this._previewLine); this._previewLine = null; }
        if (this._previewLengthLabel) { m.removeLayer(this._previewLengthLabel); this._previewLengthLabel = null; }
        if (this._snapGuideLine) { m.removeLayer(this._snapGuideLine); this._snapGuideLine = null; }
        if (this._snapAngleMarker) { m.removeLayer(this._snapAngleMarker); this._snapAngleMarker = null; }
    },

    finishWall: function () {
        var m = this._map;
        if (!m) return;

        // Clean up drawing aids
        if (this._currentWallVertices) { m.removeLayer(this._currentWallVertices); this._currentWallVertices = null; }
        if (this._currentWallLabels) { m.removeLayer(this._currentWallLabels); this._currentWallLabels = null; }
        if (this._previewLine) { m.removeLayer(this._previewLine); this._previewLine = null; }
        if (this._snapIndicator) { m.removeLayer(this._snapIndicator); this._snapIndicator = null; }

        // Remove the extra point added by the second click of the double-click
        // (but NOT when the wall was closed via snap - that point is intentional)
        if (!this._closedBySnap && this._currentWall && this._currentWall.points.length > 2) {
            this._currentWall.points.pop();
            if (this._currentWall.materials) this._currentWall.materials.pop();
        }
        this._closedBySnap = false;

        // Validate wall (need at least 2 points)
        if (!this._currentWall || this._currentWall.points.length < 2) {
            this._currentWall = null;
            if (this._currentWallLine) { this._currentWallLine.remove(); this._currentWallLine = null; }
            return;
        }

        // Remove the drawn polyline (updateWalls will re-render)
        if (this._currentWallLine) { this._currentWallLine.remove(); this._currentWallLine = null; }

        if (!this._allWalls) this._allWalls = [];

        // Auto-split: if drawing a 2-point segment near an existing wall,
        // split the existing wall and replace that section with the new material.
        // Works for any material - allows replacing wall sections with doors, windows, or different wall types.
        var cw = this._currentWall;
        var didSplit = false;

        if (cw && cw.points.length === 2) {
            var snapDist = 2;
            var bestWi = -1, bestSi = -1, bestT1 = -1, bestT2 = -1, bestD = Infinity;
            var p1 = L.latLng(cw.points[0].lat, cw.points[0].lng);
            var p2 = L.latLng(cw.points[1].lat, cw.points[1].lng);

            for (var wi = 0; wi < this._allWalls.length; wi++) {
                var wall = this._allWalls[wi];
                for (var si = 0; si < wall.points.length - 1; si++) {
                    var a = L.latLng(wall.points[si].lat, wall.points[si].lng);
                    var b = L.latLng(wall.points[si + 1].lat, wall.points[si + 1].lng);
                    var dx = b.lng - a.lng, dy = b.lat - a.lat;
                    var len2 = dx * dx + dy * dy;
                    if (len2 < 1e-20) continue;
                    var t1 = ((p1.lng - a.lng) * dx + (p1.lat - a.lat) * dy) / len2;
                    var t2 = ((p2.lng - a.lng) * dx + (p2.lat - a.lat) * dy) / len2;
                    if (t1 < -0.05 || t1 > 1.05 || t2 < -0.05 || t2 > 1.05) continue;
                    t1 = Math.max(0, Math.min(1, t1));
                    t2 = Math.max(0, Math.min(1, t2));
                    var proj1 = L.latLng(a.lat + t1 * dy, a.lng + t1 * dx);
                    var proj2 = L.latLng(a.lat + t2 * dy, a.lng + t2 * dx);
                    var d1 = m.distance(p1, proj1);
                    var d2 = m.distance(p2, proj2);
                    var maxD = Math.max(d1, d2);
                    if (maxD < snapDist && maxD < bestD) {
                        bestD = maxD; bestWi = wi; bestSi = si; bestT1 = t1; bestT2 = t2;
                    }
                }
            }

            if (bestWi >= 0) {
                var tMin = Math.min(bestT1, bestT2);
                var tMax = Math.max(bestT1, bestT2);
                var targetWall = this._allWalls[bestWi];
                var targetSi = bestSi;
                var aP = targetWall.points[targetSi];
                var bP = targetWall.points[targetSi + 1];
                var splitPt1 = { lat: aP.lat + tMin * (bP.lat - aP.lat), lng: aP.lng + tMin * (bP.lng - aP.lng) };
                var splitPt2 = { lat: aP.lat + tMax * (bP.lat - aP.lat), lng: aP.lng + tMax * (bP.lng - aP.lng) };
                targetWall.points.splice(targetSi + 1, 0, splitPt1, splitPt2);
                if (!targetWall.materials) {
                    targetWall.materials = [];
                    for (var j = 0; j < targetWall.points.length - 3; j++) targetWall.materials.push(targetWall.material);
                } else {
                    targetWall.materials.splice(targetSi, 0, targetWall.materials[targetSi] || targetWall.material);
                }
                targetWall.materials[targetSi + 1] = cw.material;
                didSplit = true;
            }
        }

        if (!didSplit) {
            this._allWalls.push(cw);
        }

        this._currentWall = null;
        this._refAngle = null;
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    // ── Position Mode ────────────────────────────────────────────────

    enterPositionMode: function (swLat, swLng, neLat, neLng) {
        var m = this._map;
        if (!m) return;
        var self = this;

        if (this._corners) {
            this._corners.forEach(function (c) { m.removeLayer(c); });
        }

        // Use provided bounds (from C#) so position mode works even without a floor plan image
        var sw, ne;
        if (swLat !== undefined && swLat !== null) {
            sw = L.latLng(swLat, swLng);
            ne = L.latLng(neLat, neLng);
        } else if (this._overlay) {
            var bounds = this._overlay.getBounds();
            sw = bounds.getSouthWest();
            ne = bounds.getNorthEast();
        } else {
            return;
        }
        var ci = L.divIcon({ className: 'fp-corner-handle', iconSize: [14, 14], iconAnchor: [7, 7] });
        var swM = L.marker(sw, { icon: ci, draggable: true }).addTo(m);
        var neM = L.marker(ne, { icon: ci, draggable: true }).addTo(m);
        this._corners = [swM, neM];

        function upd() {
            self._overlay.setBounds([swM.getLatLng(), neM.getLatLng()]);
        }
        swM.on('drag', upd);
        neM.on('drag', upd);

        function save() {
            var s = swM.getLatLng(), n = neM.getLatLng();
            self._dotNetRef.invokeMethodAsync('OnBoundsChangedFromJs', s.lat, s.lng, n.lat, n.lng);
        }
        swM.on('dragend', save);
        neM.on('dragend', save);
    },

    exitPositionMode: function () {
        var m = this._map;
        if (!m) return;
        if (this._corners) {
            this._corners.forEach(function (c) { m.removeLayer(c); });
            this._corners = null;
        }
    },

    // ── Building Move Mode ──────────────────────────────────────────

    enterMoveMode: function (centerLat, centerLng) {
        var m = this._map;
        if (!m) return;
        var self = this;

        this.exitMoveMode();
        m.dragging.disable();

        var ci = L.divIcon({ className: 'fp-move-handle', html: '\u2725', iconSize: [28, 28], iconAnchor: [14, 14] });
        this._moveMarker = L.marker([centerLat, centerLng], { icon: ci, draggable: true }).addTo(m);
        this._moveStartCenter = L.latLng(centerLat, centerLng);

        // Live preview: move overlay and walls during drag
        this._moveMarker.on('drag', function (e) {
            if (!self._overlay || !self._moveStartCenter) return;
            var pos = e.target.getLatLng();
            var dLat = pos.lat - self._moveStartCenter.lat;
            var dLng = pos.lng - self._moveStartCenter.lng;
            var b = self._overlay.getBounds();
            var sw = b.getSouthWest();
            var ne = b.getNorthEast();
            self._overlay.setBounds([[sw.lat + dLat, sw.lng + dLng], [ne.lat + dLat, ne.lng + dLng]]);
            self._moveStartCenter = pos;
        });

        this._moveMarker.on('dragend', function (e) {
            var pos = e.target.getLatLng();
            self._dotNetRef.invokeMethodAsync('OnBuildingMovedFromJs', pos.lat, pos.lng);
        });
    },

    exitMoveMode: function () {
        if (this._moveMarker && this._map) {
            this._map.removeLayer(this._moveMarker);
            this._moveMarker = null;
        }
        if (this._map) this._map.dragging.enable();
    },

    // ── Heatmap ──────────────────────────────────────────────────────

    computeHeatmap: function (baseUrl, activeFloor, band) {
        var m = this._map;
        if (!m) return;
        var self = this;

        // Store params so the slider can re-invoke without arguments
        if (baseUrl) this._heatmapBaseUrl = baseUrl;
        if (activeFloor != null) this._heatmapFloor = activeFloor;
        if (band) this._heatmapBand = band;
        baseUrl = this._heatmapBaseUrl;
        activeFloor = this._heatmapFloor;
        band = this._heatmapBand;
        if (!baseUrl) return;

        var vb = m.getBounds();
        var sw = vb.getSouthWest();
        var ne = vb.getNorthEast();
        var vWidth = m.distance(sw, L.latLng(sw.lat, ne.lng));
        var vHeight = m.distance(sw, L.latLng(ne.lat, sw.lng));
        var maxDim = Math.max(vWidth, vHeight);
        var res = maxDim > 400 ? Math.ceil(maxDim / 400) : 1.0;

        var body = {
            activeFloor: activeFloor, band: band,
            gridResolutionMeters: res,
            swLat: sw.lat, swLng: sw.lng, neLat: ne.lat, neLng: ne.lng
        };
        // Filter overrides to current band and strip band suffix for API
        var bandSuffix = ':' + band;
        var filteredOverrides = {};
        Object.keys(self._txPowerOverrides).forEach(function (key) {
            if (key.endsWith(bandSuffix)) {
                filteredOverrides[key.slice(0, -bandSuffix.length)] = self._txPowerOverrides[key];
            }
        });
        if (Object.keys(filteredOverrides).length > 0) {
            body.txPowerOverrides = filteredOverrides;
        }

        fetch(baseUrl + '/api/heatmap/compute', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        })
        .then(function (r) { return r.json(); })
        .then(function (data) {
            if (!data || !data.data) return;

            if (self._heatmapOverlay) m.removeLayer(self._heatmapOverlay);

            var canvas = document.createElement('canvas');
            canvas.width = data.width;
            canvas.height = data.height;
            var ctx = canvas.getContext('2d');
            var imgData = ctx.createImageData(data.width, data.height);

            // Smooth color gradient function
            function lerpColor(sig) {
                var stops = [
                    { s: -30, r: 0, g: 220, b: 0 }, { s: -45, r: 34, g: 197, b: 94 },
                    { s: -55, r: 180, g: 220, b: 40 }, { s: -65, r: 250, g: 204, b: 21 },
                    { s: -72, r: 251, g: 146, b: 60 }, { s: -80, r: 239, g: 68, b: 68 },
                    { s: -90, r: 107, g: 114, b: 128 }
                ];
                if (sig >= stops[0].s) return stops[0];
                if (sig <= stops[stops.length - 1].s) return stops[stops.length - 1];
                for (var j = 0; j < stops.length - 1; j++) {
                    if (sig <= stops[j].s && sig >= stops[j + 1].s) {
                        var t = (sig - stops[j + 1].s) / (stops[j].s - stops[j + 1].s);
                        return {
                            r: Math.round(stops[j].r * t + stops[j + 1].r * (1 - t)),
                            g: Math.round(stops[j].g * t + stops[j + 1].g * (1 - t)),
                            b: Math.round(stops[j].b * t + stops[j + 1].b * (1 - t))
                        };
                    }
                }
                return stops[stops.length - 1];
            }

            for (var i = 0; i < data.data.length; i++) {
                var sig = data.data[i];
                var c = lerpColor(sig);
                var row = data.height - 1 - Math.floor(i / data.width);
                var col = i % data.width;
                var idx = (row * data.width + col) * 4;
                var alpha = sig >= -90 ? 140 : sig <= -95 ? 0 : Math.round(140 * (-95 - sig) / (-95 + 90));
                imgData.data[idx] = c.r;
                imgData.data[idx + 1] = c.g;
                imgData.data[idx + 2] = c.b;
                imgData.data[idx + 3] = alpha;
            }

            ctx.putImageData(imgData, 0, 0);
            var dataUrl = canvas.toDataURL();
            var bounds = [[data.swLat, data.swLng], [data.neLat, data.neLng]];
            self._heatmapOverlay = L.imageOverlay(dataUrl, bounds, {
                opacity: 0.6, pane: 'heatmapPane', interactive: false
            }).addTo(m);

            // Contour lines using marching squares
            if (self._contourLayer) m.removeLayer(self._contourLayer);
            self._contourLayer = L.layerGroup().addTo(m);

            var thresholds = [
                { db: -50, color: '#22c55e', label: '-50' },
                { db: -60, color: '#eab308', label: '-60' },
                { db: -70, color: '#f97316', label: '-70' },
                { db: -80, color: '#ef4444', label: '-80' }
            ];
            var latStep = (data.neLat - data.swLat) / data.height;
            var lngStep = (data.neLng - data.swLng) / data.width;

            function gv(x, y) {
                if (x < 0 || x >= data.width || y < 0 || y >= data.height) return -100;
                return data.data[y * data.width + x];
            }

            thresholds.forEach(function (th) {
                var segs = [];
                for (var cy = 0; cy < data.height - 1; cy++) {
                    for (var cx = 0; cx < data.width - 1; cx++) {
                        var tl = gv(cx, cy + 1) >= th.db ? 1 : 0;
                        var tr = gv(cx + 1, cy + 1) >= th.db ? 1 : 0;
                        var br = gv(cx + 1, cy) >= th.db ? 1 : 0;
                        var bl = gv(cx, cy) >= th.db ? 1 : 0;
                        var ci2 = tl * 8 + tr * 4 + br * 2 + bl;
                        if (ci2 === 0 || ci2 === 15) continue;

                        function lerp2(v1, v2) { var d2 = v2 - v1; return d2 === 0 ? 0.5 : (th.db - v1) / d2; }
                        var t = lerp2(gv(cx, cy + 1), gv(cx + 1, cy + 1));
                        var r2 = lerp2(gv(cx + 1, cy + 1), gv(cx + 1, cy));
                        var b = lerp2(gv(cx, cy), gv(cx + 1, cy));
                        var l = lerp2(gv(cx, cy + 1), gv(cx, cy));

                        var eT = [cx + t, cy + 1], eR = [cx + 1, cy + 1 - r2], eB = [cx + b, cy], eL = [cx, cy + 1 - l];
                        var cases = {
                            1: [eL, eB], 2: [eB, eR], 3: [eL, eR], 4: [eT, eR],
                            5: [eL, eT, eB, eR], 6: [eT, eB], 7: [eL, eT], 8: [eL, eT],
                            9: [eT, eB], 10: [eT, eR, eL, eB], 11: [eT, eR],
                            12: [eL, eR], 13: [eB, eR], 14: [eL, eB]
                        };
                        var p = cases[ci2];
                        if (!p) continue;
                        for (var si2 = 0; si2 < p.length; si2 += 2) {
                            segs.push([
                                [data.swLat + p[si2][1] * latStep, data.swLng + p[si2][0] * lngStep],
                                [data.swLat + p[si2 + 1][1] * latStep, data.swLng + p[si2 + 1][0] * lngStep]
                            ]);
                        }
                    }
                }

                segs.forEach(function (s) {
                    L.polyline(s, { color: th.color, weight: 1.5, opacity: 0.7, interactive: false, pane: 'wallPane' })
                        .addTo(self._contourLayer);
                });

                if (segs.length > 0) {
                    var best = segs[0][0];
                    segs.forEach(function (s) {
                        if (s[0][1] > best[1]) best = s[0];
                        if (s[1][1] > best[1]) best = s[1];
                    });
                    L.marker(best, {
                        icon: L.divIcon({ className: 'fp-contour-label', html: th.label, iconSize: [30, 14], iconAnchor: [15, 7] }),
                        interactive: false
                    }).addTo(self._contourLayer);
                }
            });
        })
        .catch(function (err) { console.error('Heatmap error:', err); });
    },

    clearHeatmap: function () {
        if (this._heatmapOverlay) {
            this._map.removeLayer(this._heatmapOverlay);
            this._heatmapOverlay = null;
        }
        if (this._contourLayer) {
            this._map.removeLayer(this._contourLayer);
            this._contourLayer = null;
        }
    },

    // ── Signal Data Overlay ────────────────────────────────────────

    _signalColor: function (dbm) {
        var stops = [
            { s: -30, r: 0, g: 220, b: 0 }, { s: -45, r: 34, g: 197, b: 94 },
            { s: -55, r: 180, g: 220, b: 40 }, { s: -65, r: 250, g: 204, b: 21 },
            { s: -72, r: 251, g: 146, b: 60 }, { s: -80, r: 239, g: 68, b: 68 },
            { s: -90, r: 107, g: 114, b: 128 }
        ];
        if (dbm >= stops[0].s) return 'rgb(' + stops[0].r + ',' + stops[0].g + ',' + stops[0].b + ')';
        if (dbm <= stops[stops.length - 1].s) return 'rgb(' + stops[stops.length - 1].r + ',' + stops[stops.length - 1].g + ',' + stops[stops.length - 1].b + ')';
        for (var j = 0; j < stops.length - 1; j++) {
            if (dbm <= stops[j].s && dbm >= stops[j + 1].s) {
                var t = (dbm - stops[j + 1].s) / (stops[j].s - stops[j + 1].s);
                return 'rgb(' + Math.round(stops[j].r * t + stops[j + 1].r * (1 - t)) + ',' +
                    Math.round(stops[j].g * t + stops[j + 1].g * (1 - t)) + ',' +
                    Math.round(stops[j].b * t + stops[j + 1].b * (1 - t)) + ')';
            }
        }
        return 'rgb(' + stops[stops.length - 1].r + ',' + stops[stops.length - 1].g + ',' + stops[stops.length - 1].b + ')';
    },

    updateSignalData: function (markersJson) {
        if (!this._map || !this._signalClusterGroup) return;
        var self = this;

        this._signalClusterGroup.clearLayers();
        this._signalCurrentSpider = null;

        var markers = JSON.parse(markersJson);

        markers.forEach(function (m) {
            var marker = L.circleMarker([m.lat, m.lng], {
                radius: 8,
                fillColor: m.color,
                color: '#fff',
                weight: 2,
                opacity: 1,
                fillOpacity: 0.8,
                signalDbm: m.signalDbm
            });
            if (m.popup) marker.bindPopup(m.popup);
            self._signalClusterGroup.addLayer(marker);
        });
    },

    clearSignalData: function () {
        if (this._signalClusterGroup) {
            this._signalClusterGroup.clearLayers();
            this._signalCurrentSpider = null;
        }
    },

    // ── Cleanup ──────────────────────────────────────────────────────

    clearFloorLayers: function () {
        if (this._overlay && this._map) { this._map.removeLayer(this._overlay); this._overlay = null; }
        if (this._heatmapOverlay && this._map) { this._map.removeLayer(this._heatmapOverlay); this._heatmapOverlay = null; }
        if (this._contourLayer && this._map) { this._map.removeLayer(this._contourLayer); this._contourLayer = null; }
        if (this._signalClusterGroup) this._signalClusterGroup.clearLayers();
        if (this._bgWallLayer) this._bgWallLayer.clearLayers();
        if (this._wallLayer) this._wallLayer.clearLayers();
    }
};
