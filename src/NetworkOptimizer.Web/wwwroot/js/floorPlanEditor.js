// Floor Plan Editor - Leaflet map integration
// Provides map, AP markers, wall drawing, heatmap, and floor overlay management
function esc(s) { if (!s) return ''; var d = document.createElement('div'); d.textContent = s; return d.innerHTML.replace(/"/g, '&quot;'); }

window.fpEditor = {

    // ── State ────────────────────────────────────────────────────────
    _map: null,
    _dotNetRef: null,
    _overlay: null, // legacy single overlay (kept for backward compat)
    _overlays: [],  // array of { id, overlay } for multi-image
    _selectedOverlayId: null,
    _apLayer: null,
    _apGlowLayer: null,
    _bgWallLayer: null,
    _wallLayer: null,
    _wallHighlightLayer: null,
    _allWalls: [],
    _wallSelection: { wallIdx: null, segIdx: null },
    _multiSel: null,          // [{ wi, si }] segments picked by Shift+drag or Shift+click
    _wallsEditable: false,
    _suppressClickUntil: 0,   // swallows the click the browser fires after a marquee drag
    _materialLabels: {},
    _materialColors: {},
    _placementHandler: null,
    _wallClickHandler: null,
    _wallDblClickHandler: null,
    _wallMoveHandler: null,
    _wallMapClickBound: false,
    _currentWall: null,
    _currentWallSegLines: null,
    _currentWallVertices: null,
    _currentWallLabels: null,
    _refAngle: null,
    _previewLine: null,
    _snapToClose: false,
    _snapIndicator: null,
    _corners: null,
    _moveMarker: null,
    _heatmapOverlay: null,
    _heatmapRequestId: 0,
    _contourLayer: null,
    _txPowerOverrides: {},
    _antennaModeOverrides: {},
    _disabledAps: {},
    _disabledForPlanAps: {},
    _heatmapBand: '5',
    _excludePlannedAps: true,
    _signalClusterGroup: null,
    _signalCurrentSpider: null,
    _signalSwitchingSpider: false,
    _signalMeasurements: null,
    _bgWalls: [],
    _sameBuildingWalls: [],
    _snapGuideLine: null,
    _snapAngleMarker: null,
    _previewLengthLabel: null,
    _edgePanHandler: null,
    _edgePanTarget: null,
    _edgePanTimer: null,
    _edgePanDelayTimer: null,
    _activeMoveHandlers: null, // { moveHandler, finishHandler } for cancellable moves
    _escHandler: null,
    _distanceWarnShown: false,
    _scaleBar: null, // SteppedScaleBar state

    // ── Edge Pan ──────────────────────────────────────────────────────

    _startEdgePan: function () {
        var self = this;
        var m = this._map;
        if (!m || this._edgePanHandler) return;
        var edgeZone = 40; // pixels from edge to trigger pan
        var panSpeed = 4;  // pixels per frame at the very edge
        var panDelay = 150; // ms delay before panning starts
        self._edgePanDx = 0;
        self._edgePanDy = 0;

        // Listen on the editor wrapper so we get mousemove events over the toolbar too.
        // Edge zones are still calculated relative to the map container.
        this._edgePanHandler = function (e) {
            // Pause panning when hovering over any form input (select, button, etc.)
            if ((e.target && e.target.closest && (e.target.closest('select') || e.target.closest('input') || e.target.closest('button')))
                || self._inToolboxArea(e.clientX, e.clientY)) {
                self._edgePanDx = 0;
                self._edgePanDy = 0;
                self._stopEdgePanTimer();
                return;
            }

            var mapRect = m.getContainer().getBoundingClientRect();
            var x = e.clientX - mapRect.left;
            var y = e.clientY - mapRect.top;
            var w = mapRect.width;
            var h = mapRect.height;

            var dx = 0, dy = 0;
            if (x < edgeZone) dx = -panSpeed * (1 - x / edgeZone);
            else if (x > w - edgeZone) dx = panSpeed * (1 - (w - x) / edgeZone);
            if (y < edgeZone) dy = -panSpeed * (1 - y / edgeZone);
            else if (y > h - edgeZone) dy = panSpeed * (1 - (h - y) / edgeZone);

            self._edgePanDx = dx;
            self._edgePanDy = dy;

            if (dx !== 0 || dy !== 0) {
                // Start panning after a delay so users can move through edge zones to reach toolbar
                if (!self._edgePanTimer && !self._edgePanDelayTimer) {
                    self._edgePanDelayTimer = setTimeout(function () {
                        self._edgePanDelayTimer = null;
                        if (self._edgePanDx !== 0 || self._edgePanDy !== 0) {
                            self._edgePanTimer = setInterval(function () {
                                m.panBy([self._edgePanDx, self._edgePanDy], { animate: false });
                            }, 16);
                        }
                    }, panDelay);
                }
            } else {
                self._stopEdgePanTimer();
            }
        };

        // Attach to the editor wrapper (parent of both toolbar and map)
        this._edgePanTarget = m.getContainer().closest('.floor-plan-editor') || m.getContainer();
        this._edgePanTarget.addEventListener('mousemove', this._edgePanHandler);
    },

    // The top-left control column (zoom + toolbox) as one box, gaps included, plus a margin, so
    // moving between its buttons never edge-pans the map.
    _inToolboxArea: function (clientX, clientY) {
        var m = this._map;
        if (!m) return false;
        var corner = m.getContainer().querySelector('.leaflet-top.leaflet-left');
        if (!corner) return false;
        var l = Infinity, t = Infinity, r = -Infinity, b = -Infinity;
        corner.querySelectorAll('.leaflet-control').forEach(function (c) {
            var rc = c.getBoundingClientRect();
            if (rc.width === 0 && rc.height === 0) return;
            l = Math.min(l, rc.left); t = Math.min(t, rc.top); r = Math.max(r, rc.right); b = Math.max(b, rc.bottom);
        });
        if (l === Infinity) return false;
        var pad = 12;
        return clientX >= l - pad && clientX <= r + pad && clientY >= t - pad && clientY <= b + pad;
    },

    _stopEdgePanTimer: function () {
        if (this._edgePanDelayTimer) {
            clearTimeout(this._edgePanDelayTimer);
            this._edgePanDelayTimer = null;
        }
        if (this._edgePanTimer) {
            clearInterval(this._edgePanTimer);
            this._edgePanTimer = null;
        }
    },

    _stopEdgePan: function () {
        if (this._edgePanHandler && this._edgePanTarget) {
            this._edgePanTarget.removeEventListener('mousemove', this._edgePanHandler);
            this._edgePanHandler = null;
            this._edgePanTarget = null;
        }
        this._stopEdgePanTimer();
    },

    // ── Escape Key ────────────────────────────────────────────────────

    _initEscapeHandler: function () {
        var self = this;
        if (this._escHandler) return;
        this._escHandler = function (e) {
            // Delete/Backspace: delete the multi-selection, or the selected wall or segment
            var inField = e.target && (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA');
            // Delete/Backspace on an open AP popup: a planned AP is deleted, a placed AP (in
            // Place/Edit APs) leaves the map. C# re-checks read-only and the mode.
            var apTarget = self._apPopupTarget;
            var apDeletable = apTarget && (apTarget.planned || self._toolboxState.mode === 'aps');
            if ((e.key === 'Delete' || e.key === 'Backspace') && !inField && apDeletable && self._dotNetRef && self._toolboxState.editable) {
                e.preventDefault();
                self._apPopupTarget = null;
                if (self._map) self._map.closePopup();
                if (apTarget.planned) self._dotNetRef.invokeMethodAsync('OnPlannedApDeleteFromJs', apTarget.plannedId);
                else if (self._toolboxState.mode === 'aps') self._dotNetRef.invokeMethodAsync('OnApRemoveFromJs', apTarget.mac);
                return;
            }
            if ((e.key === 'Delete' || e.key === 'Backspace') && !inField && self._multiSel && self._multiSel.length > 0) {
                e.preventDefault();
                self.deleteMultiSel();
                return;
            }
            if ((e.key === 'Delete' || e.key === 'Backspace') && self._wallSelection && self._wallSelection.wallIdx !== null) {
                e.preventDefault();
                if (self._wallSelection.segIdx !== null) {
                    self.deleteSeg(self._wallSelection.wallIdx, self._wallSelection.segIdx);
                } else {
                    self.deleteWall(self._wallSelection.wallIdx);
                }
                return;
            }

            if (e.key !== 'Escape') return;
            var m = self._map;
            if (!m) return;

            // Priority 1: Cancel active move (shape move, building move)
            if (self._activeMoveHandlers) {
                m.off('mousemove', self._activeMoveHandlers.moveHandler);
                m.off('click', self._activeMoveHandlers.finishHandler);
                if (self._activeMoveHandlers.cancel) self._activeMoveHandlers.cancel();
                self._stopEdgePan();
                m.dragging.enable();
                m.getContainer().style.cursor = '';
                if (self._wallHighlightLayer) self._wallHighlightLayer.clearLayers();
                self._wallSelection = { wallIdx: null, segIdx: null };
                self._activeMoveHandlers = null;
                return;
            }

            // Priority 2: Cancel overlay image move
            if (self._moveMarker) {
                self.exitMoveMode();
                if (self._dotNetRef) self._dotNetRef.invokeMethodAsync('OnEscapeMoveMode');
                return;
            }

            // Priority 3: Close open popup
            if (m._popup && m._popup.isOpen()) {
                m.closePopup();
                return;
            }

            // Priority 4: Finish/cancel current shape being drawn (stay in draw mode)
            if (self._isDrawing && self._currentWall) {
                // >= 2 points: commit the wall; < 2 points: just cancel and clean up
                self.commitCurrentWall();
                return;
            }

            // Priority 5: Deselect wall segment/shape or the multi-selection
            if (self._multiSel && self._multiSel.length > 0) {
                self._clearMultiSel();
                return;
            }
            if (self._wallSelection && (self._wallSelection.wallIdx !== null || self._wallSelection.segIdx !== null)) {
                self._wallSelection = { wallIdx: null, segIdx: null };
                if (self._wallHighlightLayer) self._wallHighlightLayer.clearLayers();
                return;
            }

            // Priority 6: Exit draw mode or AP mode (back to view)
            if (self._isDrawing || self._dotNetRef) {
                if (self._dotNetRef) self._dotNetRef.invokeMethodAsync('OnEscapeToView');
            }
        };
        document.addEventListener('keydown', this._escHandler);
    },

    _removeEscapeHandler: function () {
        if (this._escHandler) {
            document.removeEventListener('keydown', this._escHandler);
            this._escHandler = null;
        }
    },

    // ── Map Initialization ───────────────────────────────────────────

    initMap: function (containerId, centerLat, centerLng, zoom, mapboxToken) {
        var self = this;
        this._txPowerOverrides = {};
        this._antennaModeOverrides = {};
        this._disabledAps = {};
        this._disabledForPlanAps = {};
        var resolveReady;
        var readyPromise = new Promise(function (resolve) { resolveReady = resolve; });

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

            var m = L.map(containerId, { center: [centerLat, centerLng], zoom: zoom, zoomControl: true, maxZoom: 24, zoomSnap: 0.5, zoomDelta: zoom >= 21 ? 0.5 : 1 });
            var osmLayer = L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 24, maxNativeZoom: 19, attribution: 'OpenStreetMap', className: 'map-tiles-dark'
            }).addTo(m);
            self._map = m;
            // The wheel zooms after the pointer rests on the map or a click (wheel-gate.js); always in fullscreen.
            if (window.WheelGate) {
                window.WheelGate.leaflet(m, { exempt: function () { return !!container.closest('.fp-fullscreen'); } });
            }

            // Dynamic zoom delta: 0.5 at building level (zoom >= 21), 1 otherwise
            // Override zoomIn/zoomOut to always use dynamic delta (Leaflet's zoom control
            // passes options.zoomDelta explicitly, so we must override the value)
            var origZoomIn = m.zoomIn.bind(m);
            var origZoomOut = m.zoomOut.bind(m);
            m.zoomIn = function (delta, options) {
                var d = m.getZoom() >= 21 ? 0.5 : 1;
                m.options.zoomDelta = d;
                return origZoomIn(d, options);
            };
            m.zoomOut = function (delta, options) {
                var d = m.getZoom() > 21 ? 0.5 : 1;
                m.options.zoomDelta = d;
                return origZoomOut(d, options);
            };

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
                        iconSize: L.point(24, 24)
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

            // Immediately invalidate + abort on zoom/pan START so stale responses
            // can't render with wrong-viewport bounds
            m.on('zoomstart movestart', function () {
                self._heatmapRequestId = (self._heatmapRequestId || 0) + 1;
                if (self._heatmapAbort) self._heatmapAbort.abort();
            });

            // Recompute heatmap when zoom/pan settles
            m.on('moveend', function () {
                if (self._heatmapBaseUrl) {
                    self.computeHeatmap();
                }
            });

            // Stepped distance scale bar (3 steps normal, 5 fullscreen, hidden on mobile non-fullscreen)
            var initSteps = (window.innerWidth <= 768) ? 0 : 3;
            self._scaleBar = SteppedScaleBar.create(m, initSteps);

            // Satellite toggle (bottom-left) — added after the scale bar so it stacks above it
            if (window.MapSatelliteToggle) {
                window.MapSatelliteToggle.add(m, osmLayer, mapboxToken || '');
            }

            // Collapsible address search (top-right) - jump the floor plan to a street address
            if (window.MapAddressSearch) {
                self._addrSearch = window.MapAddressSearch.add(m, { position: 'topright' });
            }

            resolveReady();
        }

        init();
        return readyPromise;
    },

    setDotNetRef: function (ref) {
        this._dotNetRef = ref;
        this._initEscapeHandler();
    },

    setScaleSteps: function (steps) {
        // On mobile, only show scale bar in fullscreen (steps > 3)
        var isMobile = window.innerWidth <= 768;
        SteppedScaleBar.setSteps(this._scaleBar, (isMobile && steps <= 3) ? 0 : steps);
    },

    // ── View ─────────────────────────────────────────────────────────

    fitBounds: function (swLat, swLng, neLat, neLng) {
        if (this._map) {
            var bounds = L.latLngBounds([[swLat, swLng], [neLat, neLng]]);
            this._map.fitBounds(bounds, { padding: [40, 40], animate: false, maxZoom: 24 });
        }
    },

    setView: function (lat, lng, zoom) {
        if (this._map) {
            this._map.setView([lat, lng], zoom);
        }
    },

    getCenter: function () {
        if (this._map) {
            var c = this._map.getCenter();
            return [c.lat, c.lng];
        }
        return null;
    },

    saveMapView: function (buildingLat, buildingLng) {
        var self = this;
        if (this._map) {
            var c = this._map.getCenter();
            this._savedView = {
                lat: c.lat, lng: c.lng, zoom: this._map.getZoom(),
                buildingLat: buildingLat, buildingLng: buildingLng
            };
            // After the next fitBounds settles, record the building zoom level.
            // Clear saved view if user zooms out more than 1 step from that.
            if (this._savedViewZoomHandler) this._map.off('zoomend', this._savedViewZoomHandler);
            var armed = false;
            this._savedViewZoomHandler = function () {
                if (!self._savedView) {
                    self._map.off('zoomend', self._savedViewZoomHandler);
                    self._savedViewZoomHandler = null;
                    return;
                }
                if (!armed) {
                    // First zoomend after save = fitBounds completed; record zoom and
                    // actual map center (may differ from DB center for asymmetric buildings)
                    self._savedView.buildingZoom = self._map.getZoom();
                    var fc = self._map.getCenter();
                    self._savedView.fitCenterLat = fc.lat;
                    self._savedView.fitCenterLng = fc.lng;
                    armed = true;
                    return;
                }
                if (self._map.getZoom() < self._savedView.buildingZoom - 1) {
                    self._savedView = null;
                    self._map.off('zoomend', self._savedViewZoomHandler);
                    self._savedViewZoomHandler = null;
                }
            };
            this._map.on('zoomend', this._savedViewZoomHandler);
        }
    },

    restoreMapView: function () {
        // Clean up zoom listener
        if (this._savedViewZoomHandler && this._map) {
            this._map.off('zoomend', this._savedViewZoomHandler);
            this._savedViewZoomHandler = null;
        }
        if (!this._map || !this._savedView) return;
        // Only restore if the building is still visible in the viewport;
        // if the user has panned away, they navigated intentionally.
        var sv = this._savedView;
        this._savedView = null;
        // Use the post-fitBounds center (actual viewport center when editing started)
        // rather than the DB building center, which may not match for asymmetric buildings.
        var checkLat = sv.fitCenterLat != null ? sv.fitCenterLat : sv.buildingLat;
        var checkLng = sv.fitCenterLng != null ? sv.fitCenterLng : sv.buildingLng;
        if (checkLat != null && checkLng != null) {
            // Check in pixel space so the map's aspect ratio doesn't matter.
            // Building must be within the center 66% of the container in both axes.
            var px = this._map.latLngToContainerPoint([checkLat, checkLng]);
            var sz = this._map.getSize();
            var mx = sz.x * 0.17, my = sz.y * 0.17;
            if (px.x < mx || px.x > sz.x - mx || px.y < my || px.y > sz.y - my) return;
        }
        // Don't restore if it would zoom in more than current view
        if (sv.zoom > this._map.getZoom()) return;
        this._map.setView([sv.lat, sv.lng], sv.zoom);
    },

    invalidateSize: function () {
        if (this._map) {
            this._map.invalidateSize();
        }
    },

    // Recalculate container size and adjust zoom proportionally to the viewport
    // width change so the same geographic area stays visible.
    invalidateSizeProportional: function () {
        if (!this._map) return;
        var oldSize = this._map.getSize();
        var center = this._map.getCenter();
        var oldZoom = this._map.getZoom();
        this._map.invalidateSize();
        var newSize = this._map.getSize();
        if (oldSize.x > 0 && newSize.x > 0) {
            var zoomDelta = Math.log2(newSize.x / oldSize.x);
            this._map.setView(center, oldZoom + zoomDelta, { animate: false });
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

    // ── Rotation Geometry Helpers ─────────────────────────────────────

    // Rotate a pixel point around a center point by angleDeg (CW in screen coords, matching CSS rotate())
    _rotatePointPx: function (pt, center, angleDeg) {
        var rad = angleDeg * Math.PI / 180;
        var dx = pt.x - center.x;
        var dy = pt.y - center.y;
        return L.point(
            center.x + dx * Math.cos(rad) - dy * Math.sin(rad),
            center.y + dx * Math.sin(rad) + dy * Math.cos(rad)
        );
    },

    // Get rotated corner LatLngs for a given axis-aligned bounds and rotation
    _getRotatedCorners: function (bounds, rotationDeg, map) {
        var sw = bounds.getSouthWest();
        var ne = bounds.getNorthEast();
        var center = bounds.getCenter();
        var cPx = map.latLngToContainerPoint(center);
        var swPx = map.latLngToContainerPoint(sw);
        var nePx = map.latLngToContainerPoint(ne);
        var nwPx = map.latLngToContainerPoint(L.latLng(ne.lat, sw.lng));
        var sePx = map.latLngToContainerPoint(L.latLng(sw.lat, ne.lng));
        return {
            sw: map.containerPointToLatLng(this._rotatePointPx(swPx, cPx, rotationDeg)),
            ne: map.containerPointToLatLng(this._rotatePointPx(nePx, cPx, rotationDeg)),
            nw: map.containerPointToLatLng(this._rotatePointPx(nwPx, cPx, rotationDeg)),
            se: map.containerPointToLatLng(this._rotatePointPx(sePx, cPx, rotationDeg))
        };
    },

    // Given two diagonally-opposite rotated corner LatLngs, compute axis-aligned bounds
    _boundsFromRotatedDiagonal: function (rotA, rotB, rotationDeg, map) {
        var aPx = map.latLngToContainerPoint(rotA);
        var bPx = map.latLngToContainerPoint(rotB);
        var cPx = L.point((aPx.x + bPx.x) / 2, (aPx.y + bPx.y) / 2);
        var aAl = map.containerPointToLatLng(this._rotatePointPx(aPx, cPx, -rotationDeg));
        var bAl = map.containerPointToLatLng(this._rotatePointPx(bPx, cPx, -rotationDeg));
        return L.latLngBounds(
            L.latLng(Math.min(aAl.lat, bAl.lat), Math.min(aAl.lng, bAl.lng)),
            L.latLng(Math.max(aAl.lat, bAl.lat), Math.max(aAl.lng, bAl.lng))
        );
    },

    // Pick the closest CSS resize cursor for a diagonal at baseAngle + rotationDeg
    _getResizeCursor: function (baseAngle, rotationDeg) {
        var a = ((baseAngle + rotationDeg) % 360 + 360) % 360;
        if (a >= 180) a -= 180;
        // 0=ew, 45=nesw, 90=ns, 135=nwse (each covers ±22.5°)
        if (a < 22.5 || a >= 157.5) return 'ew-resize';
        if (a < 67.5) return 'nesw-resize';
        if (a < 112.5) return 'ns-resize';
        return 'nwse-resize';
    },

    // Apply rotation + crop transforms to an overlay element
    _applyOverlayTransforms: function (overlay) {
        var el = overlay.getElement ? overlay.getElement() : overlay._image;
        if (!el) return;
        if (overlay._rotationDeg) {
            el.style.transform += ' translate(50%, 50%) rotate(' + overlay._rotationDeg + 'deg) translate(-50%, -50%)';
        }
        if (overlay._crop) {
            el.style.clipPath = 'inset(' + (overlay._crop.top || 0) + '% ' + (overlay._crop.right || 0) + '% ' +
                (overlay._crop.bottom || 0) + '% ' + (overlay._crop.left || 0) + '%)';
        } else {
            el.style.clipPath = '';
        }
    },

    // ── Multi-Image Overlays ──────────────────────────────────────────

    updateFloorOverlays: function (imagesJson) {
        var m = this._map;
        if (!m) return;
        var self = this;

        // Remove existing overlays
        this._overlays.forEach(function (o) { m.removeLayer(o.overlay); });
        this._overlays = [];
        this._selectedOverlayId = null;

        if (!imagesJson || imagesJson.length === 0) return;

        imagesJson.forEach(function (img) {
            var bounds = [[img.swLatitude, img.swLongitude], [img.neLatitude, img.neLongitude]];
            var overlay = L.imageOverlay(img.imageUrl, bounds, {
                opacity: img.opacity || 0.7,
                interactive: true,
                pane: 'fpOverlayPane',
                className: 'fp-image-overlay'
            });

            // Store rotation/crop state on the overlay instance
            overlay._rotationDeg = img.rotationDeg || 0;
            overlay._crop = null;
            if (img.cropJson) {
                try { overlay._crop = typeof img.cropJson === 'string' ? JSON.parse(img.cropJson) : img.cropJson; }
                catch (e) { /* invalid crop JSON */ }
            }

            // Monkey-patch _reset BEFORE addTo so Leaflet's initial _reset uses our version
            var origReset = overlay._reset.bind(overlay);
            overlay._reset = function () {
                origReset();
                self._applyOverlayTransforms(this);
            };

            // Monkey-patch _animateZoom to maintain rotation/crop during zoom animation
            // Without this, Leaflet's zoom animation overwrites transform and rotation flickers
            var origAnimateZoom = overlay._animateZoom.bind(overlay);
            overlay._animateZoom = function (e) {
                origAnimateZoom(e);
                self._applyOverlayTransforms(this);
            };

            // Now add to map - Leaflet's _reset will use our patched version
            overlay.addTo(m);
            // Also re-apply when image finishes loading (triggers another _reset)
            overlay.once('load', function () { overlay._reset(); });

            overlay.on('click', function () {
                // Don't fire selection when in position mode (drag-to-move triggers click)
                if (self._corners) return;
                if (self._dotNetRef) {
                    self._dotNetRef.invokeMethodAsync('OnImageSelectedFromJs', img.id);
                }
            });

            self._overlays.push({ id: img.id, overlay: overlay });
        });
    },

    setImageRotation: function (imageId, deg) {
        var entry = this._overlays.find(function (o) { return o.id === imageId; });
        if (!entry) return;
        entry.overlay._rotationDeg = deg;
        entry.overlay._reset();
        // Update position mode handles if active for this image
        if (this._positionUpdateFn) this._positionUpdateFn(deg);
    },

    setImageCrop: function (imageId, top, right, bottom, left) {
        var entry = this._overlays.find(function (o) { return o.id === imageId; });
        if (!entry) return;
        entry.overlay._crop = { top: top, right: right, bottom: bottom, left: left };
        entry.overlay._reset();
    },

    selectOverlay: function (imageId) {
        var self = this;
        this._selectedOverlayId = imageId;
        this._overlays.forEach(function (o) {
            var el = o.overlay.getElement();
            if (!el) return;
            if (o.id === imageId) {
                el.style.outline = '3px solid #3b82f6';
                el.style.outlineOffset = '-3px';
            } else {
                el.style.outline = '';
                el.style.outlineOffset = '';
            }
        });
    },

    deselectOverlay: function () {
        this._selectedOverlayId = null;
        this._overlays.forEach(function (o) {
            var el = o.overlay.getElement();
            if (!el) return;
            el.style.outline = '';
            el.style.outlineOffset = '';
        });
    },

    setImageOpacity: function (imageId, opacity) {
        var entry = this._overlays.find(function (o) { return o.id === imageId; });
        if (entry) entry.overlay.setOpacity(opacity);
    },

    setOverlaysInteractive: function (interactive) {
        this._overlays.forEach(function (o) {
            var el = o.overlay.getElement();
            if (el) el.style.pointerEvents = interactive ? 'auto' : 'none';
        });
    },

    _getOverlay: function (imageId) {
        var entry = this._overlays.find(function (o) { return o.id === imageId; });
        return entry ? entry.overlay : null;
    },

    // ── Underlay Upload (HEIC/PDF conversion) ────────────────────────

    // Opens file picker immediately (must be called from native onclick to work on iOS Safari)
    pickUnderlayFile: function () {
        var self = this;
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = 'image/*,.pdf,.heic,.heif';
        input.style.display = 'none';
        document.body.appendChild(input);

        input.addEventListener('change', async function () {
            var file = input.files && input.files[0];
            document.body.removeChild(input);
            if (!file) return;

            try {
                // Check file size (50 MB limit)
                if (file.size > 50 * 1024 * 1024) {
                    alert('File is too large. Maximum size is 50 MB.');
                    return;
                }

                // Get bounds from C# (after file is picked, so user gesture isn't needed)
                var info = await self._dotNetRef.invokeMethodAsync('GetUnderlayUploadInfo');
                if (!info) return;

                var blob = await self._convertToImage(file);
                if (!blob) return; // user cancelled (e.g. PDF page picker)

                // Get image natural dimensions for aspect-ratio matching
                var imgW = 0, imgH = 0;
                try {
                    var dims = await self._getImageDimensions(blob);
                    imgW = dims.width;
                    imgH = dims.height;
                } catch (e) { /* couldn't get dims, C# will use fallback */ }

                var formData = new FormData();
                formData.append('image', blob, 'underlay.png');
                formData.append('swLat', info.swLat.toString());
                formData.append('swLng', info.swLng.toString());
                formData.append('neLat', info.neLat.toString());
                formData.append('neLng', info.neLng.toString());

                var resp = await fetch('/api/floor-plan/floors/' + info.floorId + '/images', {
                    method: 'POST',
                    body: formData
                });
                if (!resp.ok) throw new Error('Upload failed: ' + resp.status);
                var result = await resp.json();
                if (self._dotNetRef) {
                    self._dotNetRef.invokeMethodAsync('OnUnderlayUploadedFromJs', result.id, imgW, imgH);
                }
            } catch (err) {
                console.error('Underlay upload error:', err);
                alert(err.message || 'Upload failed');
            }
        });

        input.click();
    },

    _convertToImage: async function (file) {
        var name = file.name.toLowerCase();
        var type = file.type.toLowerCase();
        var isHeic = type === 'image/heic' || type === 'image/heif' ||
            name.endsWith('.heic') || name.endsWith('.heif');

        // HEIC/HEIF: try native browser decoding first (works if OS has HEIC codec),
        // then fall back to heic2any JS decoder
        if (isHeic) {
            // Attempt 1: native decode via createImageBitmap / img element
            try {
                var nativeBlob = await this._tryNativeDecode(file);
                if (nativeBlob) return nativeBlob;
            } catch (e) { /* native decode failed, try heic2any */ }

            // Attempt 2: heic2any JS decoder
            if (typeof heic2any !== 'undefined') {
                try {
                    var result = await heic2any({ blob: file, toType: 'image/png', quality: 0.92 });
                    return Array.isArray(result) ? result[0] : result;
                } catch (e) {
                    throw new Error('HEIC conversion failed. On Windows, install "HEIF Image Extensions" from the Microsoft Store, then try again.');
                }
            }
            throw new Error('Cannot convert HEIC. Install "HEIF Image Extensions" from the Microsoft Store.');
        }

        // PDF: let user pick a page, then render at high resolution
        if (type === 'application/pdf' || name.endsWith('.pdf')) {
            var pdfjsLib = await import('/lib/pdf.min.mjs');
            pdfjsLib.GlobalWorkerOptions.workerSrc = '/lib/pdf.worker.min.mjs';
            var arrayBuf = await file.arrayBuffer();
            var pdf = await pdfjsLib.getDocument({ data: arrayBuf }).promise;

            var pageNum = 1;
            if (pdf.numPages > 1) {
                pageNum = await this._showPdfPagePicker(pdf);
                if (!pageNum) return null;
            }

            var page = await pdf.getPage(pageNum);
            var scale = 2;
            var viewport = page.getViewport({ scale: scale });
            var canvas = document.createElement('canvas');
            canvas.width = viewport.width;
            canvas.height = viewport.height;
            var ctx = canvas.getContext('2d');
            await page.render({ canvasContext: ctx, viewport: viewport }).promise;
            return new Promise(function (resolve) {
                canvas.toBlob(function (blob) { resolve(blob); }, 'image/png');
            });
        }

        // Other images (JPEG, PNG, WebP, etc.): pass through
        return file;
    },

    // Try decoding an image natively via the browser (uses OS codecs for HEIC etc.)
    _tryNativeDecode: function (file) {
        return new Promise(function (resolve, reject) {
            var url = URL.createObjectURL(file);
            var img = new Image();
            img.onload = function () {
                var canvas = document.createElement('canvas');
                canvas.width = img.naturalWidth;
                canvas.height = img.naturalHeight;
                canvas.getContext('2d').drawImage(img, 0, 0);
                canvas.toBlob(function (blob) {
                    URL.revokeObjectURL(url);
                    resolve(blob);
                }, 'image/png');
            };
            img.onerror = function () {
                URL.revokeObjectURL(url);
                reject(new Error('Native decode failed'));
            };
            img.src = url;
        });
    },

    _getImageDimensions: function (blob) {
        return new Promise(function (resolve, reject) {
            var url = URL.createObjectURL(blob);
            var img = new Image();
            img.onload = function () {
                URL.revokeObjectURL(url);
                resolve({ width: img.naturalWidth, height: img.naturalHeight });
            };
            img.onerror = function () {
                URL.revokeObjectURL(url);
                reject(new Error('Could not read image dimensions'));
            };
            img.src = url;
        });
    },

    _showPdfPagePicker: function (pdf) {
        return new Promise(function (resolve) {
            // Build modal DOM
            var backdrop = document.createElement('div');
            backdrop.className = 'fp-dialog-backdrop';

            var dialog = document.createElement('div');
            dialog.className = 'fp-dialog';
            dialog.style.maxWidth = '680px';
            dialog.style.maxHeight = '80vh';
            dialog.style.display = 'flex';
            dialog.style.flexDirection = 'column';

            var title = document.createElement('h3');
            title.textContent = 'Select PDF Page (' + pdf.numPages + ' pages)';
            dialog.appendChild(title);

            var grid = document.createElement('div');
            grid.style.display = 'grid';
            grid.style.gridTemplateColumns = 'repeat(auto-fill, minmax(140px, 1fr))';
            grid.style.gap = '12px';
            grid.style.overflowY = 'auto';
            grid.style.flex = '1';
            grid.style.padding = '4px';
            dialog.appendChild(grid);

            var actions = document.createElement('div');
            actions.className = 'fp-dialog-actions';
            actions.style.marginTop = '12px';
            var cancelBtn = document.createElement('button');
            cancelBtn.className = 'fp-btn';
            cancelBtn.textContent = 'Cancel';
            actions.appendChild(cancelBtn);
            dialog.appendChild(actions);

            var dismiss = function () {
                document.removeEventListener('keydown', escHandler);
                document.body.removeChild(backdrop);
                resolve(0);
            };
            var escHandler = function (e) { if (e.key === 'Escape') dismiss(); };
            cancelBtn.onclick = dismiss;
            backdrop.onclick = dismiss;
            dialog.onclick = function (e) { e.stopPropagation(); };
            document.addEventListener('keydown', escHandler);

            backdrop.appendChild(dialog);
            document.body.appendChild(backdrop);

            // Render thumbnails
            var thumbScale = 0.5;
            for (var i = 1; i <= pdf.numPages; i++) {
                (function (pageNum) {
                    var cell = document.createElement('div');
                    cell.style.cursor = 'pointer';
                    cell.style.border = '2px solid transparent';
                    cell.style.borderRadius = '4px';
                    cell.style.padding = '4px';
                    cell.style.textAlign = 'center';
                    cell.style.transition = 'border-color 0.15s';
                    cell.onmouseenter = function () { cell.style.borderColor = '#3b82f6'; };
                    cell.onmouseleave = function () { cell.style.borderColor = 'transparent'; };
                    cell.onclick = function () {
                        document.removeEventListener('keydown', escHandler);
                        document.body.removeChild(backdrop);
                        resolve(pageNum);
                    };

                    var label = document.createElement('div');
                    label.textContent = 'Page ' + pageNum;
                    label.style.fontSize = '12px';
                    label.style.color = '#cbd5e1';
                    label.style.marginTop = '4px';

                    pdf.getPage(pageNum).then(function (page) {
                        var vp = page.getViewport({ scale: thumbScale });
                        var canvas = document.createElement('canvas');
                        canvas.width = vp.width;
                        canvas.height = vp.height;
                        canvas.style.width = '100%';
                        canvas.style.height = 'auto';
                        canvas.style.borderRadius = '2px';
                        canvas.style.background = '#fff';
                        var ctx = canvas.getContext('2d');
                        page.render({ canvasContext: ctx, viewport: vp }).promise.then(function () {
                            cell.insertBefore(canvas, label);
                        });
                    });

                    cell.appendChild(label);
                    grid.appendChild(cell);
                })(i);
            }
        });
    },

    // ── AP Markers ───────────────────────────────────────────────────

    _apMarkers: {},

    _apKey: function (ap) { return ap.isPlanned ? 'p' + ap.plannedId : 'm' + String(ap.mac).toLowerCase(); },

    // Move above Remove, for the AP popup and the right-click menu. Move needs the AP's own floor.
    _apActionButtons: function (ap) {
        var key = this._apKey(ap).replace(/[^a-z0-9:p]/gi, '');
        var move = '<button class="fp-ap-popup-move" onclick="fpEditor.moveAp(\'' + key + '\')"' +
            (ap.sameFloor ? '' : ' disabled data-tooltip="Switch to this AP\'s floor to move it"') + '>Move</button>';
        var remove = '<button class="fp-ap-popup-delete" onclick="fpEditor.removeAp(\'' + key + '\')">' +
            (ap.isPlanned ? 'Remove Planned AP' : 'Remove from Map') + '</button>';
        return move + remove;
    },

    // Move: the AP follows the pointer and lands on the next click. Esc puts it back.
    moveAp: function (key) {
        var self = this, m = this._map, entry = this._apMarkers[key];
        if (!m || !entry || !entry.ap.sameFloor) return;
        m.closePopup();
        var mk = entry.marker, gm = entry.glow, ap = entry.ap;
        var orig = mk.getLatLng();
        var el = mk.getElement();
        if (el) el.style.pointerEvents = 'none'; // the click that lands it must reach the map
        m.getContainer().style.cursor = 'move';
        // While it moves, nothing else on the map takes hover or clicks (building hit areas, walls, APs)
        m.getContainer().classList.add('fp-ap-moving');
        var done = function () {
            m.off('mousemove', moveHandler);
            m.off('click', finishHandler);
            if (el) el.style.pointerEvents = '';
            m.getContainer().style.cursor = '';
            m.getContainer().classList.remove('fp-ap-moving');
            self._activeMoveHandlers = null;
        };
        var moveHandler = function (e) { mk.setLatLng(e.latlng); gm.setLatLng(e.latlng); };
        var finishHandler = function (e) {
            done();
            mk.setLatLng(e.latlng); gm.setLatLng(e.latlng);
            if (ap.isPlanned) self._dotNetRef.invokeMethodAsync('OnPlannedApDragEndFromJs', ap.plannedId, e.latlng.lat, e.latlng.lng);
            else self._dotNetRef.invokeMethodAsync('OnApDragEndFromJs', ap.mac, e.latlng.lat, e.latlng.lng);
        };
        // Deferred so the click on the Move button does not land the AP where it already is
        setTimeout(function () {
            m.on('mousemove', moveHandler);
            m.on('click', finishHandler);
            self._activeMoveHandlers = {
                moveHandler: moveHandler, finishHandler: finishHandler,
                cancel: function () {
                    mk.setLatLng(orig); gm.setLatLng(orig);
                    if (el) el.style.pointerEvents = '';
                    m.getContainer().classList.remove('fp-ap-moving');
                }
            };
        }, 0);
    },

    removeAp: function (key) {
        var entry = this._apMarkers[key];
        if (!entry || !this._dotNetRef) return;
        this._map.closePopup();
        this._apPopupTarget = null;
        if (entry.ap.isPlanned) this._dotNetRef.invokeMethodAsync('OnPlannedApDeleteFromJs', entry.ap.plannedId);
        else this._dotNetRef.invokeMethodAsync('OnApRemoveFromJs', entry.ap.mac);
    },

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
        this._apMarkers = {};
        this._apGlowLayer.clearLayers();

        var aps = JSON.parse(markersJson);
        var reopenMarker = null;

        aps.forEach(function (ap) {
            var isPlanned = ap.isPlanned;

            // Glow layer (behind icons)
            var glowClass = 'fp-ap-glow-dot' + (ap.sameFloor ? '' : ' other-floor') + (isPlanned ? ' planned' : '');
            var glowIcon = L.divIcon({
                className: 'fp-ap-glow-container',
                html: '<div class="' + glowClass + '"></div>',
                iconSize: [48, 48], iconAnchor: [24, 24]
            });
            var isDisabled = self._isApEffectivelyDisabled(ap.mac.toLowerCase());
            var glowMarker = L.marker([ap.lat, ap.lng], {
                icon: glowIcon, interactive: false, pane: 'apGlowPane',
                opacity: isDisabled ? 0 : 1
            }).addTo(self._apGlowLayer);

            // Icon layer with orientation arrow
            var opacity = isDisabled ? 0.2
                : isPlanned ? (ap.sameFloor ? 1.0 : 0.35)
                : (ap.online ? (ap.sameFloor ? 1.0 : 0.35) : (ap.sameFloor ? 0.4 : 0.2));
            var arrowHtml = ap.sameFloor
                ? '<div class="fp-ap-direction" style="transform:rotate(' + ap.orientation + 'deg)"><div class="fp-ap-arrow"></div></div>'
                : '';
            var badgeHtml = isPlanned ? '<div class="fp-ap-planned-badge">P</div>' : '';
            var containerClass = 'fp-ap-marker-container' + (isPlanned ? ' planned' : '');
            var icon = L.divIcon({
                className: containerClass,
                html: arrowHtml + '<img src="' + ap.iconUrl + '" class="fp-ap-marker-icon" style="opacity:' + opacity + '" />' + badgeHtml,
                iconSize: [32, 32], iconAnchor: [16, 16], popupAnchor: [0, -16]
            });
            var marker = L.marker([ap.lat, ap.lng], {
                icon: icon, draggable: draggable, pane: 'apIconPane'
            }).addTo(self._apLayer);
            marker._apMac = ap.mac;

            // Popup with floor selector and orientation input
            var floorOpts = '';
            for (var fi = -2; fi <= 5; fi++) {
                floorOpts += '<option value="' + fi + '"' + (fi === ap.floor ? ' selected' : '') + '>' +
                    (fi <= 0 ? 'B' + Math.abs(fi - 1) : fi === 1 ? '1st' : fi === 2 ? '2nd' : fi === 3 ? '3rd' : fi + 'th') +
                    ' Floor</option>';
            }

            // Mount type dropdown options - constrain by model
            var allMounts = ['ceiling', 'wall', 'desktop'];
            var mountLabels = { ceiling: 'Ceiling', wall: 'Wall / Pole', desktop: 'Desktop' };
            var model = (ap.model || '').toUpperCase();
            var mountTypes;
            if (/^UDR/.test(model)) {
                mountTypes = ['desktop']; // UDR*: desktop only
            } else if (/^UX/.test(model)) {
                mountTypes = ['wall', 'desktop']; // UX*: wall or desktop
            } else {
                mountTypes = allMounts;
            }
            var mountOpts = '';
            mountTypes.forEach(function (mt) {
                mountOpts += '<option value="' + mt + '"' + (mt === (ap.mountType || 'ceiling') ? ' selected' : '') + '>' + mountLabels[mt] + '</option>';
            });

            // Find the radio matching the active heatmap band
            var bandMap = { '2.4': 'ng', '5': 'na', '6': '6e' };
            var activeRadioCode = bandMap[self._heatmapBand] || 'na';
            var activeRadio = (ap.radios || []).find(function (r) { return r.radioCode === activeRadioCode; });

            // TX power slider section - different for planned vs real APs
            var txPowerHtml = '';
            if (isPlanned) {
                // Planned APs: TX power changes persist directly to DB
                if (activeRadio && activeRadio.txPowerDbm != null) {
                    var minPower = activeRadio.minTxPowerDbm || 1;
                    var maxPower = activeRadio.maxTxPowerDbm || activeRadio.txPowerDbm;
                    var currentPower = activeRadio.txPowerDbm;
                    var antennaGain = (activeRadio.eirp != null) ? activeRadio.eirp - activeRadio.txPowerDbm : null;
                    var currentEirp = (antennaGain != null) ? currentPower + antennaGain : null;
                    var eirpText = currentEirp != null ? ' / ' + currentEirp + ' dBm EIRP' : '';
                    var bandStr = self._heatmapBand || '5';
                    txPowerHtml =
                        '<div class="fp-ap-popup-divider"></div>' +
                        '<div class="fp-ap-popup-row"><label>TX Power</label>' +
                        '<input type="range" data-tx-slider min="' + minPower + '" max="' + maxPower + '" value="' + currentPower + '" ' +
                        (antennaGain != null ? 'data-antenna-gain="' + antennaGain + '" ' : '') +
                        'oninput="fpEditor._updateTxPowerLabel(this)" ' +
                        'onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnPlannedApTxPowerChangedFromJs\',' + ap.plannedId + ',\'' + bandStr + '\',parseInt(this.value))" />' +
                        '<span class="fp-ap-popup-deg-wrap"></span></div>' +
                        '<div class="fp-ap-popup-tx-info">' + currentPower + ' dBm TX' + eirpText + '</div>';
                }
            } else {
                // Real APs: TX power uses simulation overrides (ephemeral)
                if (activeRadio && activeRadio.txPowerDbm != null) {
                    var macKey = ap.mac.toLowerCase();
                    var overrideKey = macKey + ':' + self._heatmapBand;
                    var currentPower = (self._txPowerOverrides[overrideKey] != null) ? self._txPowerOverrides[overrideKey] : activeRadio.txPowerDbm;
                    var minPower = activeRadio.minTxPowerDbm || 1;
                    var maxPower = activeRadio.maxTxPowerDbm || activeRadio.txPowerDbm;
                    var isOverridden = self._txPowerOverrides[overrideKey] != null;
                    var safeKey = esc(overrideKey);
                    var antennaGain = (activeRadio.eirp != null) ? activeRadio.eirp - activeRadio.txPowerDbm : null;
                    // When antenna mode is overridden, use catalog limits for the simulated mode
                    var simMode = self._antennaModeOverrides[macKey];
                    if (simMode && activeRadio.catalogModes) {
                        var modeKey = simMode.toLowerCase();
                        var modeCat = activeRadio.catalogModes[modeKey];
                        if (modeCat) {
                            maxPower = modeCat.maxTxPowerDbm;
                            antennaGain = modeCat.antennaGainDbi;
                            if (currentPower > maxPower) {
                                currentPower = maxPower;
                                if (self._txPowerOverrides[overrideKey] != null)
                                    self._txPowerOverrides[overrideKey] = maxPower;
                            }
                        }
                    }
                    var currentEirp = (antennaGain != null) ? currentPower + antennaGain : null;
                    var eirpText = currentEirp != null ? ' / ' + currentEirp + ' dBm EIRP' : '';
                    txPowerHtml =
                        '<div class="fp-ap-popup-divider"></div>' +
                        '<div class="fp-ap-popup-section-label">Simulate</div>' +
                        '<div class="fp-ap-popup-row"><label>TX Power</label>' +
                        '<input type="range" data-tx-slider min="' + minPower + '" max="' + maxPower + '" value="' + currentPower + '" ' +
                        (antennaGain != null ? 'data-antenna-gain="' + antennaGain + '" ' : '') +
                        'oninput="fpEditor._updateTxPowerLabel(this)" ' +
                        'onchange="fpEditor._txPowerOverrides[\'' + safeKey + '\']=parseInt(this.value);fpEditor._updateTxPowerLabel(this);fpEditor._updateResetSimBtn();fpEditor.computeHeatmap();fpEditor._dotNetRef.invokeMethodAsync(\'OnSimulationChanged\')" />' +
                        '<span class="fp-ap-popup-deg-wrap"></span></div>' +
                        '<div class="fp-ap-popup-tx-info' + (isOverridden ? ' overridden' : '') + '">' + currentPower + ' dBm TX' + eirpText + '</div>';
                }
            }

            // Antenna mode toggle for APs with switchable modes
            // Supports two types: Internal/OMNI (U7-Outdoor) and Narrow/Wide (E7-Audience)
            var antennaModeHtml = '';
            function getModePair(mode) {
                var m = (mode || '').toUpperCase();
                if (m === 'NARROW' || m === 'WIDE') return { modes: ['Narrow', 'Wide'], labels: ['Narrow', 'Wide'] };
                return { modes: ['Internal', 'OMNI'], labels: ['Directional', 'Omni'] };
            }
            if (isPlanned) {
                // Planned APs: antenna mode persists directly
                if (activeRadio && activeRadio.antennaMode) {
                    var currentMode = activeRadio.antennaMode;
                    var pair = getModePair(currentMode);
                    var activeIdx = currentMode.toUpperCase() === pair.modes[1].toUpperCase() ? 1 : 0;
                    antennaModeHtml =
                        '<div class="fp-ap-popup-row"><label>Antenna</label>' +
                        '<div class="fp-mode-toggle" ' +
                        'onclick="fpEditor._togglePlannedAntennaMode(' + ap.plannedId + ',\'' + esc(currentMode) + '\')">' +
                        '<span class="fp-mode-opt' + (activeIdx === 0 ? ' active' : '') + '">' + pair.labels[0] + '</span>' +
                        '<span class="fp-mode-opt' + (activeIdx === 1 ? ' active' : '') + '">' + pair.labels[1] + '</span>' +
                        '</div></div>';
                }
            } else {
                // Real APs: antenna mode uses simulation overrides
                if (activeRadio && activeRadio.antennaMode) {
                    var modeOverrideKey = ap.mac.toLowerCase();
                    var currentMode = self._antennaModeOverrides[modeOverrideKey] || activeRadio.antennaMode;
                    var pair = getModePair(currentMode);
                    var activeIdx = currentMode.toUpperCase() === pair.modes[1].toUpperCase() ? 1 : 0;
                    var safeModeKey = esc(modeOverrideKey);
                    var modeIsOverridden = self._antennaModeOverrides[modeOverrideKey] != null;
                    var modeHeader = txPowerHtml ? '' :
                        '<div class="fp-ap-popup-divider"></div><div class="fp-ap-popup-section-label">Simulate</div>';
                    antennaModeHtml = modeHeader +
                        '<div class="fp-ap-popup-row"><label>Antenna</label>' +
                        '<div class="fp-mode-toggle' + (modeIsOverridden ? ' overridden' : '') + '" ' +
                        'onclick="fpEditor._toggleAntennaMode(\'' + safeModeKey + '\',\'' + esc(activeRadio.antennaMode) + '\')">' +
                        '<span class="fp-mode-opt' + (activeIdx === 0 ? ' active' : '') + '">' + pair.labels[0] + '</span>' +
                        '<span class="fp-mode-opt' + (activeIdx === 1 ? ' active' : '') + '">' + pair.labels[1] + '</span>' +
                        '</div></div>';
                }
            }

            // Disable AP toggle buttons (two modes)
            var disableApHtml = '';
            var macLower = ap.mac.toLowerCase();
            var isSimDisabled = !!self._disabledAps[macLower];
            var isPlanDisabled = !!self._disabledForPlanAps[macLower];
            var disableHeader = (txPowerHtml || antennaModeHtml) ? '' :
                '<div class="fp-ap-popup-divider"></div><div class="fp-ap-popup-section-label">Simulate</div>';
            disableApHtml = disableHeader +
                '<div class="fp-ap-popup-row" style="margin-top:4px">' +
                '<button class="fp-disable-ap-btn' + (isSimDisabled ? ' active' : '') + '" ' +
                'data-tooltip="Simulate disabling this AP to see how coverage is affected" data-tooltip-hover-only ' +
                'onclick="fpEditor._toggleDisableAp(\'' + esc(macLower) + '\')">' +
                (isSimDisabled ? 'Enable AP' : 'Disable AP') +
                '</button></div>' +
                '<div class="fp-ap-popup-row" style="margin-top:2px">' +
                '<button class="fp-disable-ap-btn fp-disable-plan' + (isPlanDisabled ? ' active' : '') + '" ' +
                'data-tooltip="Simulate removing this AP to test coverage with a replacement" data-tooltip-hover-only ' +
                'onclick="fpEditor._toggleDisableForPlanAp(\'' + esc(macLower) + '\')">' +
                (isPlanDisabled ? 'Enable AP (Plan)' : 'Disable AP (Plan)') +
                '</button></div>';

            var safeMac = esc(ap.mac);

            if (isPlanned) {
                // Planned AP popup: direct editing + delete button
                var plannedTag = '<span class="fp-ap-popup-planned-tag">Planned</span>';
                var nameInput = '<input type="text" class="fp-ap-popup-name-input" value="' + esc(ap.name || ap.model) + '" ' +
                    'oninput="fpEditor._debouncedNameSave(' + ap.plannedId + ',this.value)" />';
                var deleteBtn = '<div class="fp-ap-popup-divider"></div>' + self._apActionButtons(ap);

                marker.bindPopup(
                    '<div class="fp-ap-popup">' +
                    '<div class="fp-ap-popup-header">' + nameInput + plannedTag + '</div>' +
                    '<div class="fp-ap-popup-model">' + esc(ap.model) + '</div>' +
                    '<div class="fp-ap-popup-rows">' +
                    '<div class="fp-ap-popup-row"><label>Floor</label>' +
                    '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnPlannedApFloorChangedFromJs\',' + ap.plannedId + ',parseInt(this.value))">' +
                    floorOpts + '</select></div>' +
                    '<div class="fp-ap-popup-row"><label>Mount</label>' +
                    '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnPlannedApMountTypeChangedFromJs\',' + ap.plannedId + ',this.value)">' +
                    mountOpts + '</select></div>' +
                    '<div class="fp-ap-popup-row"><label>Facing</label>' +
                    '<input type="range" min="0" max="359" value="' + ap.orientation + '" ' +
                    'oninput="fpEditor._syncFacingFromSlider(this,\'' + safeMac + '\')" ' +
                    'onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnPlannedApOrientationChangedFromJs\',' + ap.plannedId + ',parseInt(this.value))" />' +
                    '<span class="fp-ap-popup-deg-wrap"><input type="number" class="fp-ap-popup-deg-input" min="0" max="359" value="' + ap.orientation + '" ' +
                    'data-save-method="OnPlannedApOrientationChangedFromJs" data-save-id="' + ap.plannedId + '" data-save-type="planned" ' +
                    'onfocus="this.select()" oninput="fpEditor._syncFacingFromInput(this,\'' + safeMac + '\')" />' +
                    '<span class="fp-ap-popup-deg-suffix">\u00B0</span></span></div>' +
                    txPowerHtml +
                    antennaModeHtml +
                    deleteBtn +
                    '</div></div>',
                    { maxWidth: 280 }
                );
            } else {
                // Real AP popup (existing behavior)
                marker.bindPopup(
                    '<div class="fp-ap-popup">' +
                    '<div class="fp-ap-popup-name">' + esc(ap.name || ap.mac) + '</div>' +
                    '<div class="fp-ap-popup-model">' + esc(ap.model) + ' \u00b7 ' + ap.clients + ' client' + (ap.clients !== 1 ? 's' : '') + '</div>' +
                    '<div class="fp-ap-popup-rows">' +
                    '<div class="fp-ap-popup-row"><label>Floor</label>' +
                    '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApFloorChangedFromJs\',\'' + safeMac + '\',parseInt(this.value))">' +
                    floorOpts + '</select></div>' +
                    '<div class="fp-ap-popup-row"><label>Mount</label>' +
                    '<select onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApMountTypeChangedFromJs\',\'' + safeMac + '\',this.value)">' +
                    mountOpts + '</select></div>' +
                    '<div class="fp-ap-popup-row"><label>Facing</label>' +
                    '<input type="range" min="0" max="359" value="' + ap.orientation + '" ' +
                    'oninput="fpEditor._syncFacingFromSlider(this,\'' + safeMac + '\')" ' +
                    'onchange="fpEditor._dotNetRef.invokeMethodAsync(\'OnApOrientationChangedFromJs\',\'' + safeMac + '\',parseInt(this.value))" />' +
                    '<span class="fp-ap-popup-deg-wrap"><input type="number" class="fp-ap-popup-deg-input" min="0" max="359" value="' + ap.orientation + '" ' +
                    'data-save-method="OnApOrientationChangedFromJs" data-save-id="\'' + safeMac + '\'" data-save-type="real" ' +
                    'onfocus="this.select()" oninput="fpEditor._syncFacingFromInput(this,\'' + safeMac + '\')" />' +
                    '<span class="fp-ap-popup-deg-suffix">\u00B0</span></span></div>' +
                    txPowerHtml +
                    antennaModeHtml +
                    disableApHtml +
                    (self._toolboxState.editable ? '<div class="fp-ap-popup-divider"></div>' + self._apActionButtons(ap) : '') +
                    '</div></div>'
                );
            }

            // The AP whose popup is open is the Delete key's target
            (function (target) {
                marker.on('popupopen', function () { self._apPopupTarget = target; });
                marker.on('popupclose', function () { if (self._apPopupTarget === target) self._apPopupTarget = null; });
            })({ planned: !!isPlanned, plannedId: ap.plannedId, mac: ap.mac });

            // Move / Remove from the popup buttons and the right-click menu
            self._apMarkers[self._apKey(ap)] = { marker: marker, glow: glowMarker, ap: ap };
            (function (apData) {
                marker.on('contextmenu', function (e) {
                    L.DomEvent.stopPropagation(e);
                    if (e.originalEvent) e.originalEvent.preventDefault();
                    if (!self._toolboxState.editable || self._isDrawing) return;
                    m.closePopup();
                    L.popup({ closeButton: false, autoPan: false, offset: [0, -14] })
                        .setLatLng(e.target.getLatLng())
                        .setContent('<div class="fp-ap-popup fp-ap-menu">' + self._apActionButtons(apData) + '</div>')
                        .openOn(m);
                });
            })(ap);

            // Sync slider with current override each time popup opens (real APs only)
            if (!isPlanned) {
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
            }

            if (openPopupMac === ap.mac) {
                reopenMarker = marker;
            }

            if (draggable && ap.sameFloor) {
                (function (gm, apData) {
                    marker.on('drag', function (e) {
                        var pos = e.target.getLatLng();
                        gm.setLatLng(pos);
                        // Edge pan while dragging AP
                        var container = m.getContainer();
                        var rect = container.getBoundingClientRect();
                        var px = m.latLngToContainerPoint(pos);
                        var ez = 40, ps = 4, dx = 0, dy = 0;
                        if (px.x < ez) dx = -ps * (1 - px.x / ez);
                        else if (px.x > rect.width - ez) dx = ps * (1 - (rect.width - px.x) / ez);
                        if (px.y < ez) dy = -ps * (1 - px.y / ez);
                        else if (px.y > rect.height - ez) dy = ps * (1 - (rect.height - px.y) / ez);
                        if (dx !== 0 || dy !== 0) m.panBy([dx, dy], { animate: false });
                    });
                    marker.on('dragend', function (e) {
                        var pos = e.target.getLatLng();
                        gm.setLatLng(pos);
                        if (apData.isPlanned) {
                            self._dotNetRef.invokeMethodAsync('OnPlannedApDragEndFromJs', apData.plannedId, pos.lat, pos.lng);
                        } else {
                            self._dotNetRef.invokeMethodAsync('OnApDragEndFromJs', apData.mac, pos.lat, pos.lng);
                        }
                    });
                })(glowMarker, ap);
            } else if (draggable && !ap.sameFloor) {
                (function (origLatLng) {
                    marker.on('dragstart', function () {
                        self._showDrawWarning('Switch to this AP\'s floor to move it');
                    });
                    marker.on('dragend', function (e) {
                        e.target.setLatLng(origLatLng);
                    });
                })(L.latLng(ap.lat, ap.lng));
            }
        });

        // Reopen popup if one was open before rebuild
        if (reopenMarker) {
            reopenMarker.openPopup();
        }
    },

    _debouncedNameSave: function (plannedId, value) {
        clearTimeout(this._nameDebounceTimer);
        this._nameDebounceTimer = setTimeout(function () {
            fpEditor._dotNetRef.invokeMethodAsync('OnPlannedApNameChangedFromJs', plannedId, value);
        }, 500);
    },

    _updateTxPowerLabel: function (slider) {
        var info = slider.closest('.fp-ap-popup-rows').querySelector('.fp-ap-popup-tx-info');
        if (!info) return;
        var tx = parseInt(slider.value);
        var gain = slider.dataset.antennaGain;
        var eirpText = gain != null ? ' / ' + (tx + parseInt(gain)) + ' dBm EIRP' : '';
        info.textContent = tx + ' dBm TX' + eirpText;
        info.classList.add('overridden');
    },

    // Sync number input and arrow from slider drag
    _syncFacingFromSlider: function (slider, mac) {
        var row = slider.closest('.fp-ap-popup-row');
        var numInput = row && row.querySelector('.fp-ap-popup-deg-input');
        if (numInput) numInput.value = slider.value;
        this._rotateApArrow(mac, slider.value);
    },

    // Sync slider and arrow from number input, debounce save
    _facingDebounceTimer: null,
    _syncFacingFromInput: function (input, mac) {
        var v = parseInt(input.value);
        if (isNaN(v)) return;
        v = Math.max(0, Math.min(359, v));
        input.value = v;
        var row = input.closest('.fp-ap-popup-row');
        var slider = row && row.querySelector('input[type="range"]');
        if (slider) slider.value = v;
        this._rotateApArrow(mac, v);

        // Debounce save - fires 1100ms after last keystroke, no blur needed
        var self = this;
        var method = input.dataset.saveMethod;
        var id = input.dataset.saveId;
        var isPlanned = input.dataset.saveType === 'planned';
        if (this._facingDebounceTimer) clearTimeout(this._facingDebounceTimer);
        this._facingDebounceTimer = setTimeout(function () {
            self._facingDebounceTimer = null;
            if (isPlanned) {
                self._dotNetRef.invokeMethodAsync(method, parseInt(id), v);
            } else {
                self._dotNetRef.invokeMethodAsync(method, id.replace(/'/g, ''), v);
            }
        }, 1100);
    },

    // Rotate AP direction arrow in realtime (called from facing slider oninput)
    _rotateApArrow: function (mac, deg) {
        if (!this._apLayer) return;
        this._apLayer.eachLayer(function (layer) {
            if (layer._apMac === mac) {
                var el = layer.getElement && layer.getElement();
                if (!el) return;
                var dir = el.querySelector('.fp-ap-direction');
                if (dir) dir.style.transform = 'rotate(' + deg + 'deg)';
            }
        });
    },

    _getOtherMode: function (currentMode) {
        var m = currentMode.toUpperCase();
        if (m === 'NARROW') return 'Wide';
        if (m === 'WIDE') return 'Narrow';
        if (m === 'OMNI') return 'Internal';
        return 'OMNI';
    },

    _togglePlannedAntennaMode: function (plannedId, currentMode) {
        var next = this._getOtherMode(currentMode);
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnPlannedApAntennaModeChangedFromJs', plannedId, next);
    },

    _toggleAntennaMode: function (key, originalMode) {
        var current = this._antennaModeOverrides[key] || originalMode;
        var next = this._getOtherMode(current);
        if (next.toUpperCase() === originalMode.toUpperCase()) {
            delete this._antennaModeOverrides[key];
        } else {
            this._antennaModeOverrides[key] = next;
        }
        this._updateResetSimBtn();
        this.computeHeatmap();
        // Rebuild popups to reflect the new mode label
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnSimulationChanged');
    },

    _isApEffectivelyDisabled: function (macLower) {
        if (this._disabledAps[macLower]) return true;
        if (this._disabledForPlanAps[macLower] && !this._excludePlannedAps) return true;
        return false;
    },

    _toggleDisableAp: function (macLower) {
        if (this._disabledAps[macLower]) {
            delete this._disabledAps[macLower];
        } else {
            this._disabledAps[macLower] = true;
        }
        this._updateResetSimBtn();
        this.computeHeatmap();
        // Rebuild markers to update opacity and popup button label
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnSimulationChanged');
    },

    _toggleDisableForPlanAp: function (macLower) {
        if (this._disabledForPlanAps[macLower]) {
            delete this._disabledForPlanAps[macLower];
        } else {
            this._disabledForPlanAps[macLower] = true;
        }
        this._updateResetSimBtn();
        this.computeHeatmap();
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnSimulationChanged');
    },

    setExcludePlannedAps: function (exclude) {
        this._excludePlannedAps = exclude;
    },

    _updateResetSimBtn: function () {
        var btn = document.getElementById('fp-reset-sim-btn');
        var hasOverrides = Object.keys(this._txPowerOverrides).length > 0 ||
                           Object.keys(this._antennaModeOverrides).length > 0 ||
                           Object.keys(this._disabledAps).length > 0 ||
                           Object.keys(this._disabledForPlanAps).length > 0;
        if (btn) btn.style.display = hasOverrides ? '' : 'none';
    },

    resetSimulation: function () {
        this._txPowerOverrides = {};
        this._antennaModeOverrides = {};
        this._disabledAps = {};
        this._disabledForPlanAps = {};
        this._updateResetSimBtn();
        this.computeHeatmap();
        // Rebuild markers to restore opacity
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnSimulationChanged');
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

        this._placementActive = enabled;
        this._updateWallToolbox();

        if (enabled) {
            this._placementHandler = function (e) {
                self._dotNetRef.invokeMethodAsync('OnMapClickForPlacement', e.latlng.lat, e.latlng.lng);
            };
            m.on('click', this._placementHandler);
            m.getContainer().style.cursor = 'crosshair';
            // Remove building hit areas so they don't intercept clicks or show hover
            if (this._bgHitAreaLayer) this._bgHitAreaLayer.clearLayers();
        } else {
            m.getContainer().style.cursor = '';
        }
    },

    // ── Background Walls (faded, non-interactive) ─────────────────────

    updateSameBuildingWalls: function (wallsJson) {
        this._sameBuildingWalls = wallsJson ? JSON.parse(wallsJson) : [];
    },

    updateBackgroundWalls: function (wallsJson, colorsJson, clickable) {
        var m = this._map;
        if (!m) return;
        var self = this;

        if (!this._bgWallLayer) this._bgWallLayer = L.layerGroup().addTo(m);
        this._bgWallLayer.clearLayers();

        var walls = JSON.parse(wallsJson);
        var colors = JSON.parse(colorsJson);
        this._bgWalls = walls;

        walls.forEach(function (wall) {
            var opacity = wall._opacity || 0.5;
            for (var i = 0; i < wall.points.length - 1; i++) {
                var mat = (wall.materials && i < wall.materials.length && wall.materials[i]) ? wall.materials[i] : wall.material;
                var color = colors[mat] || '#94a3b8';
                L.polyline(
                    [[wall.points[i].lat, wall.points[i].lng], [wall.points[i + 1].lat, wall.points[i + 1].lng]],
                    { color: color, weight: 3, opacity: opacity, pane: 'bgWallPane', interactive: false }
                ).addTo(self._bgWallLayer);
            }
        });

        // Clickable building hit areas in global view (convex hull of all wall points)
        // Remove previous hit areas
        if (this._bgHitAreaLayer) this._bgHitAreaLayer.clearLayers();
        else this._bgHitAreaLayer = L.layerGroup().addTo(m);

        // Never enable building click-to-select while AP placement is active
        var effectiveClickable = clickable && !this._placementActive;
        if (effectiveClickable) {
            var allPts = {};
            var names = {};
            walls.forEach(function (wall) {
                var id = wall._buildingId;
                if (!id) return;
                if (wall._buildingName) names[id] = wall._buildingName;
                if (!allPts[id]) allPts[id] = [];
                wall.points.forEach(function (p) { allPts[id].push(p); });
            });
            Object.keys(allPts).forEach(function (id) {
                var latlngs = self._convexHull(allPts[id]);
                if (latlngs.length < 3) return;
                var poly = L.polygon(latlngs, {
                    color: '#64b5f6', weight: 0, fillOpacity: 0, interactive: true, pane: 'bgWallPane'
                }).addTo(self._bgHitAreaLayer);
                var bldgId = parseInt(id);
                poly.on('click', function () {
                    // An InnerSpace import being placed owns the map until it is imported or cancelled.
                    if (window.innerspaceImport && window.innerspaceImport._placement) return;
                    if (self._dotNetRef) self._dotNetRef.invokeMethodAsync('OnBgBuildingClicked', bldgId);
                });
                // "Tap" on touch-first devices, "Click" with a mouse
                var verb = (window.matchMedia && window.matchMedia('(pointer: coarse)').matches) ? 'Tap' : 'Click';
                poly.bindTooltip('<div class="trace-tip">' + verb + ' to edit ' + esc(names[id] || 'this building') + '</div>',
                    { sticky: true, className: 'trace-map-tooltip', opacity: 1 });
                poly.on('mouseover', function () { poly.setStyle({ fillOpacity: 0.15 }); });
                poly.on('mouseout', function () { poly.setStyle({ fillOpacity: 0 }); });
            });
        }
    },

    // ── Wall Rendering ───────────────────────────────────────────────

    updateWalls: function (wallsJson, colorsJson, labelsJson, editable) {
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
        this._multiSel = null;
        this._wallsEditable = editable === true; // the deselect path passes no flag: not editable
        this._initWallMarquee();

        // Per-segment rendering
        walls.forEach(function (wall, wi) {
            for (var i = 0; i < wall.points.length - 1; i++) {
                var mat = (wall.materials && i < wall.materials.length && wall.materials[i]) ? wall.materials[i] : wall.material;
                var color = colors[mat] || '#94a3b8';
                var seg = L.polyline(
                    [[wall.points[i].lat, wall.points[i].lng], [wall.points[i + 1].lat, wall.points[i + 1].lng]],
                    { color: color, weight: 4, opacity: 0.9, pane: 'wallPane', interactive: true }
                ).addTo(self._wallLayer);
                self._bindSegEvents(seg, wi, i);

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

        this._updateWallToolbox();

        // Click on map (not on wall) clears selection
        if (!this._wallMapClickBound) {
            this._wallMapClickBound = true;
            m.on('click', function () {
                if (Date.now() < self._suppressClickUntil) return;
                self._wallHighlightLayer.clearLayers();
                self._wallSelection = { wallIdx: null, segIdx: null };
                self._multiSel = null;
            });
        }
    },

    // Wall popups never auto-pan: moving the map under the pointer put the popup's close button
    // where the wall was, so "click again to select whole shape" closed the popup instead.
    _wallPopupOpts: { closeButton: true, autoPan: false },

    // Click and right-click for a wall segment; also bound to the wider hit lines over selected
    // segments, so both behave the same.
    _bindSegEvents: function (layer, wi, si) {
        var self = this;
        layer.on('click', function (e) {
            if (self._isDrawing) return; // Don't intercept clicks during wall drawing
            L.DomEvent.stopPropagation(e);
            if (Date.now() < self._suppressClickUntil) return;
            self._handleSegClick(e, wi, si);
        });
        // Right-click: group popup on a selected segment, else that segment's own popup
        layer.on('contextmenu', function (e) {
            if (self._isDrawing) return;
            L.DomEvent.stopPropagation(e);
            if (e.originalEvent) e.originalEvent.preventDefault();
            if (self._inMultiSel(wi, si)) {
                self._map.closePopup();
                self._openMultiSelPopup(e.latlng);
                return;
            }
            self._clearMultiSel();
            self._wallSelection = { wallIdx: null, segIdx: null };
            self._wallSegClick(e, wi, si);
        });
    },

    // A click on segment si of wall wi. e needs latlng and originalEvent (shiftKey).
    _handleSegClick: function (e, wi, si) {
        if (e.originalEvent && e.originalEvent.shiftKey && this._wallsEditable) {
            this._toggleMultiSel(wi, si, e.latlng);
            return;
        }
        // First click on a selected segment opens the group popup; a click while it is
        // open, or on any other segment, drops the group for a normal single selection.
        // Leaflet closes the popup on preclick, before a native click runs, hence the closedAt window.
        var popupWasOpen = this._multiSelPopup || (Date.now() - (this._multiSelPopupClosedAt || 0) < 300);
        if (this._inMultiSel(wi, si) && !popupWasOpen) {
            this._openMultiSelPopup(e.latlng);
            return;
        }
        this._clearMultiSel();
        this._wallSegClick(e, wi, si);
    },

    // Nearest wall segment within tolPx of a container point, or null.
    _segAtPoint: function (pt, tolPx) {
        var m = this._map;
        var best = null, bestD = tolPx;
        (this._allWalls || []).forEach(function (wall, wi) {
            for (var si = 0; si < wall.points.length - 1; si++) {
                var a = m.latLngToContainerPoint(L.latLng(wall.points[si].lat, wall.points[si].lng));
                var b = m.latLngToContainerPoint(L.latLng(wall.points[si + 1].lat, wall.points[si + 1].lng));
                var d = L.LineUtil.pointToSegmentDistance(pt, a, b);
                if (d <= bestD) { bestD = d; best = { wi: wi, si: si }; }
            }
        });
        return best;
    },

    // Highlight for a selected segment plus an invisible 18px hit line over it, so a selected wall
    // is easy to click or right-click. The hit line sits in wallPane, above the 4px segment.
    _addSelHighlight: function (wi, si, style) {
        var wall = this._allWalls[wi];
        var pts = [[wall.points[si].lat, wall.points[si].lng], [wall.points[si + 1].lat, wall.points[si + 1].lng]];
        L.polyline(pts, L.extend({ interactive: false }, style)).addTo(this._wallHighlightLayer);
        var hit = L.polyline(pts, { weight: 18, opacity: 0, pane: 'wallPane', interactive: true })
            .addTo(this._wallHighlightLayer);
        this._bindSegEvents(hit, wi, si);
    },

    // Two-level wall click handler: 1st click = segment, 2nd click (same wall) = whole shape
    _wallSegClick: function (e, wi, si) {
        var m = this._map;
        var self = this;
        var sel = this._wallSelection;
        var wall = this._allWalls[wi];
        var labels = this._materialLabels;
        this._wallHighlightLayer.clearLayers();
        m.closePopup();

        // Level 2: clicking same wall again (already have a segment selected) -> select whole shape
        if (sel.wallIdx === wi && sel.segIdx !== null) {
            // Select entire wall shape - highlight all segments as a selection
            for (var j = 0; j < wall.points.length - 1; j++) {
                this._addSelHighlight(wi, j, { color: '#facc15', weight: 8, opacity: 0.9 });
            }
            this._wallSelection = { wallIdx: wi, segIdx: null };

            // Popup with material dropdown for whole shape and delete button
            var wallOpts = '';
            for (var wk in labels) {
                wallOpts += '<option value="' + wk + '"' + (wk === wall.material ? ' selected' : '') + '>' + labels[wk] + '</option>';
            }
            var wallHtml = '<div style="text-align:center;min-width:180px;padding-top:14px">' +
                '<select style="width:100%;padding:3px;margin-bottom:6px;background:#1e293b;color:#e0e0e0;border:1px solid #475569;border-radius:3px" ' +
                'onchange="fpEditor.changeWallMat(' + wi + ',this.value)">' +
                wallOpts + '</select><br/>' +
                '<span style="font-size:11px;color:#94a3b8">' + (wall.points.length - 1) + ' segment' + (wall.points.length > 2 ? 's' : '') + '</span><br/>' +
                '<button onclick="fpEditor.moveWall(' + wi + ')" style="margin-top:4px;padding:2px 10px;background:#4f46e5;color:#fff;border:none;border-radius:3px;cursor:pointer;margin-right:4px">Move</button>' +
                '<button onclick="fpEditor.deleteWall(' + wi + ')" style="padding:2px 10px;background:#dc2626;color:#fff;border:none;border-radius:3px;cursor:pointer">Delete</button></div>';
            L.popup(this._wallPopupOpts).setLatLng(e.latlng).setContent(wallHtml).openOn(m);
            return;
        }

        // Level 1: first click on any wall -> select individual segment
        this._addSelHighlight(wi, si, { color: '#facc15', weight: 8, opacity: 0.9 });
        this._wallSelection = { wallIdx: wi, segIdx: si };

        // Build material dropdown options
        var segMat = (wall.materials && si < wall.materials.length && wall.materials[si]) ? wall.materials[si] : wall.material;
        var opts = '';
        for (var k in labels) {
            opts += '<option value="' + k + '"' + (k === segMat ? ' selected' : '') + '>' + labels[k] + '</option>';
        }

        // Hint for multi-segment shapes
        var hintHtml = (wall.points.length > 2)
            ? '<div style="font-size:11px;color:#94a3b8;margin-bottom:4px">Click again to select whole shape</div>'
            : '';
        if (this._wallsEditable)
            hintHtml += '<div style="font-size:11px;color:#94a3b8;margin-bottom:4px">' +
                (this._wallTool === 'select' ? 'Drag a box or Shift+click to select several' : 'Shift+click or Shift+drag to select several') +
                '</div>';

        // Popup with material dropdown, split, and delete buttons
        var html = '<div style="text-align:center;min-width:180px;padding-top:14px">' +
            hintHtml +
            '<select style="width:100%;padding:3px;margin-bottom:6px;background:#1e293b;color:#e0e0e0;border:1px solid #475569;border-radius:3px" ' +
            'onchange="fpEditor.changeSegMat(' + wi + ',' + si + ',this.value)">' +
            opts + '</select><br/>' +
            '<button onclick="fpEditor.splitSeg(' + wi + ',' + si + ')" style="padding:2px 10px;background:#4f46e5;color:#fff;border:none;border-radius:3px;cursor:pointer;margin-right:4px">Split</button>' +
            '<button onclick="fpEditor.deleteSeg(' + wi + ',' + si + ')" style="padding:2px 10px;background:#dc2626;color:#fff;border:none;border-radius:3px;cursor:pointer">Delete Seg</button></div>';
        L.popup(this._wallPopupOpts).setLatLng(e.latlng).setContent(html).openOn(m);
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
        // _keepSpanIfSameKind drops wall.spans once every band is gone, so re-check it each pass.
        for (var s = 0; wall.spans && s < wall.spans.length; s++) this._keepSpanIfSameKind(wall, s, this._segMaterial(wall, s), mat);
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
        this._keepSpanIfSameKind(wall, si, wall.materials[si], mat);
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
        if (wall.spans) wall.spans.splice(si, 0, wall.spans[si] || null);
        this._map.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    deleteSeg: function (wi, si) {
        var wall = this._allWalls[wi];
        if (!wall) return;
        // If only 1 segment (2 points), delete the entire wall
        if (wall.points.length <= 2) { this.deleteWall(wi); return; }
        var n = wall.points.length - 1;
        var f0 = wall.points[0], fN = wall.points[n];
        // A closed outline missing one segment is one open path: start it after the gap, so
        // redrawing the gap can rejoin it (see _joinAtEnds) instead of leaving two walls.
        if (n >= 3 && f0.lat === fN.lat && f0.lng === fN.lng) {
            var ring = wall.points.slice(0, n);
            var mats = [], spans = [];
            for (var k = 0; k < n; k++) { mats.push(this._segMaterial(wall, k)); spans.push((wall.spans && wall.spans[k]) || null); }
            wall.points = ring.slice(si + 1).concat(ring.slice(0, si + 1));
            wall.materials = mats.slice(si + 1).concat(mats.slice(0, si));
            wall.spans = spans.slice(si + 1).concat(spans.slice(0, si));
            wall.material = wall.materials[0];
            this._collapseMaterials(wall);
        }
        // First segment: remove first point
        else if (si === 0) {
            wall.points.splice(0, 1);
            if (wall.materials) wall.materials.splice(0, 1);
            if (wall.spans) wall.spans.splice(0, 1);
        }
        // Last segment: remove last point
        else if (si === wall.points.length - 2) {
            wall.points.splice(wall.points.length - 1, 1);
            if (wall.materials) wall.materials.splice(si, 1);
            if (wall.spans) wall.spans.splice(si, 1);
        }
        // Middle segment: split into two separate walls
        else {
            var wall1 = { points: wall.points.slice(0, si + 1), material: wall.material };
            var wall2 = { points: wall.points.slice(si + 1), material: wall.material };
            if (wall.materials) {
                wall1.materials = wall.materials.slice(0, si);
                wall2.materials = wall.materials.slice(si + 1);
            }
            if (wall.spans) {
                wall1.spans = wall.spans.slice(0, si);
                wall2.spans = wall.spans.slice(si + 1);
            }
            this._allWalls.splice(wi, 1, wall1, wall2);
        }
        this._map.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    // ── Multi-segment selection (Shift+drag marquee, Shift+click) ────

    // Shift+drag (or a drag with the Select tool) selects every wall segment the box touches. Runs in
    // the capture phase and stops propagation so Leaflet's pan and box zoom never see the gesture.
    // Leaflet 1.9 starts its drag on pointerdown wherever pointer events exist, so blocking mousedown
    // alone lets the map pan: the gesture starts on pointerdown, and mousedown is the fallback.
    _initWallMarquee: function () {
        var m = this._map;
        if (!m || this._marqueeBound) return;
        this._marqueeBound = true;
        var self = this;
        var container = m.getContainer();
        m.boxZoom.disable();
        var usePointer = !!window.PointerEvent;
        var DOWN = usePointer ? 'pointerdown' : 'mousedown';
        var MOVE = usePointer ? 'pointermove' : 'mousemove';
        var UP = usePointer ? 'pointerup' : 'mouseup';

        // Holding Shift shows the Select tool's crosshair, so the box-select gesture is visible. The
        // document listeners outlive a map, so they bind once and read the current map.
        if (!this._shiftCursorBound) {
            this._shiftCursorBound = true;
            var setShiftCursor = function (on) {
                if (self._map) self._map.getContainer().classList.toggle('fp-shift-select', on && !!self._selectUsable);
            };
            document.addEventListener('keydown', function (e) { if (e.key === 'Shift') setShiftCursor(true); });
            document.addEventListener('keyup', function (e) { if (e.key === 'Shift') setShiftCursor(false); });
            window.addEventListener('blur', function () { setShiftCursor(false); });
        }

        // The compatibility mousedown after a handled pointerdown must not reach Leaflet either
        container.addEventListener('mousedown', function (e) {
            if (self._marqueeActive) e.stopPropagation();
        }, true);
        // After a gesture the marquee handled itself, the browser's click must not reach Leaflet:
        // its preclick would close the popup the gesture just opened.
        container.addEventListener('click', function (e) {
            if (Date.now() < self._swallowClickUntil) {
                self._swallowClickUntil = 0;
                e.stopPropagation();
            }
        }, true);

        container.addEventListener(DOWN, function (e) {
            if (usePointer && e.pointerType === 'touch') return;
            if (e.button !== 0 || !(e.shiftKey || self._wallTool === 'select')) return;
            if (!self._wallsEditable || self._isDrawing || self._placementActive || self._activeMoveHandlers || self._moveMarker) return;
            if (!self._allWalls || self._allWalls.length === 0 || !self._wallLayer || self._wallLayer.getLayers().length === 0) return;
            if (e.target.closest && e.target.closest('.leaflet-popup, .leaflet-control, .leaflet-marker-icon')) return;
            e.stopPropagation();
            e.preventDefault();

            var additive = e.shiftKey;
            var start = m.mouseEventToContainerPoint(e);
            var rect = null;
            self._marqueeActive = true;
            var onMove = function (ev) {
                var cur = m.mouseEventToContainerPoint(ev);
                if (!rect && cur.distanceTo(start) < 4) return; // a click's jitter is not a drag
                var bounds = L.latLngBounds(m.containerPointToLatLng(start), m.containerPointToLatLng(cur));
                if (!rect) {
                    rect = L.rectangle(bounds, { color: '#60a5fa', weight: 1, dashArray: '4,3', fillOpacity: 0.1, interactive: false }).addTo(m);
                } else {
                    rect.setBounds(bounds);
                }
            };
            var onUp = function (ev) {
                document.removeEventListener(MOVE, onMove);
                document.removeEventListener(UP, onUp);
                self._marqueeActive = false;
                var end = m.mouseEventToContainerPoint(ev);
                self._swallowClickUntil = Date.now() + 400;
                if (!rect) {
                    // No drag: this press is the click. Pick the segment with a 9px tolerance; an
                    // empty spot clears the selection, as a map click does.
                    var hit = self._segAtPoint(end, 9);
                    if (hit) {
                        self._handleSegClick({ latlng: m.containerPointToLatLng(end), originalEvent: ev }, hit.wi, hit.si);
                    } else {
                        self._map.closePopup();
                        self._wallHighlightLayer.clearLayers();
                        self._wallSelection = { wallIdx: null, segIdx: null };
                        self._multiSel = null;
                    }
                    return;
                }
                m.removeLayer(rect);
                self._suppressClickUntil = Date.now() + 300;
                var box = {
                    minX: Math.min(start.x, end.x), maxX: Math.max(start.x, end.x),
                    minY: Math.min(start.y, end.y), maxY: Math.max(start.y, end.y)
                };
                var hits = [];
                self._allWalls.forEach(function (wall, wi) {
                    for (var si = 0; si < wall.points.length - 1; si++) {
                        var a = m.latLngToContainerPoint(L.latLng(wall.points[si].lat, wall.points[si].lng));
                        var b = m.latLngToContainerPoint(L.latLng(wall.points[si + 1].lat, wall.points[si + 1].lng));
                        if (self._segHitsBox(a, b, box)) hits.push({ wi: wi, si: si });
                    }
                });
                // Shift+drag adds to an existing multi-selection; a plain drag (Select tool) replaces it
                var merged = additive ? self._currentSelList() : [];
                hits.forEach(function (h) {
                    if (!merged.some(function (s) { return s.wi === h.wi && s.si === h.si; })) merged.push(h);
                });
                self._showMultiSel(merged);
            };
            document.addEventListener(MOVE, onMove);
            document.addEventListener(UP, onUp);
        }, true);
    },

    // Map toolbox under the zoom control: Pan / Select, Draw Layout / Undo, APs / Planned APs.
    // C# pushes its state (setToolboxState); the mode buttons call the toolbar's own toggles.
    // Select makes a plain drag draw the selection box: the marquee's capture-phase handler stops
    // the drag before Leaflet can pan, so map dragging itself stays on.
    _toolboxState: { editable: false, floor: false, mode: 'view', apLabel: 'Place APs', planLabel: 'Add Planned APs' },

    _toolIcons: {
        pan: '<path d="M18 11V6a2 2 0 0 0-4 0v5"/><path d="M14 10V4a2 2 0 0 0-4 0v6"/><path d="M10 10.5V6a2 2 0 0 0-4 0v8"/><path d="M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15"/>',
        select: '<path d="M5 3a2 2 0 0 0-2 2"/><path d="M19 3a2 2 0 0 1 2 2"/><path d="M5 21a2 2 0 0 1-2-2"/><path d="M9 3h1"/><path d="M9 21h2"/><path d="M14 3h1"/><path d="M3 9v1"/><path d="M21 9v2"/><path d="M3 14v1"/><path d="m12 12 4 10 1.7-4.3L22 16Z"/>',
        // A wall corner with a pen nib on its open end
        draw: '<path d="M3 21V9h8"/><circle cx="3" cy="9" r="0.6" fill="currentColor"/><path d="M14.5 6.5 18 3l3 3-3.5 3.5"/><path d="M14.5 6.5 9 12l-1 4 4-1 5.5-5.5"/>',
        undo: '<path d="M9 14 4 9l5-5"/><path d="M4 9h10.5a5.5 5.5 0 0 1 0 11H11"/>',
        // AppIcon.razor's "check"
        done: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
        // AppIcon.razor's "access-point" puck (same paths and 1.6 stroke), so the toolbox AP matches
        // the device cards and path-trace hops
        aps: '<g stroke-width="1.6"><path d="M2.5 11v2.5c0 2.2 4.25 4 9.5 4s9.5-1.8 9.5-4V11" fill="currentColor" fill-opacity="0.18"/>' +
            '<ellipse cx="12" cy="11" rx="9.5" ry="4" fill="currentColor" fill-opacity="0.35"/>' +
            '<ellipse cx="12" cy="11" rx="2.4" ry="1"/></g>',
        // Planned AP: the same puck, lighter, with the planned marker's orange (#f97316) plus badge,
        // badged the way ApLockIcon.razor badges its AP (dashes turn to noise at 20px)
        plan: '<g stroke-width="1.6" stroke-opacity="0.75"><path d="M2.5 11v2.5c0 2.2 4.25 4 9.5 4s9.5-1.8 9.5-4V11" fill="currentColor" fill-opacity="0.1"/>' +
            '<ellipse cx="12" cy="11" rx="9.5" ry="4" fill="currentColor" fill-opacity="0.2"/>' +
            '<ellipse cx="12" cy="11" rx="2.4" ry="1"/></g>' +
            '<circle cx="19" cy="5" r="4.4" fill="#f97316" stroke="none"/>' +
            '<path d="M19 3v4M17 5h4" stroke="#fff" stroke-width="1.6"/>'
    },

    _initWallToolbox: function () {
        var m = this._map;
        if (!m || this._wallToolbox) return;
        var self = this;
        var Toolbox = L.Control.extend({
            options: { position: 'topleft' },
            onAdd: function () {
                var div = L.DomUtil.create('div', 'leaflet-bar fp-tool-control');
                div.dataset.tour = 'signal-map-toolbox';
                var btn = function (key, icon) {
                    var sz = (icon === 'aps' || icon === 'plan') ? 20 : 16; // the flat puck needs the extra size
                    return '<a href="#" role="button" data-tool="' + key + '" data-tooltip-hover-only>' +
                        '<svg viewBox="0 0 24 24" width="' + sz + '" height="' + sz + '" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' +
                        self._toolIcons[icon] + '</svg></a>';
                };
                div.innerHTML =
                    '<div class="fp-tool-group" data-group="walls">' + btn('pan', 'pan') + btn('select', 'select') + '</div>' +
                    '<div class="fp-tool-group" data-group="draw">' + btn('draw', 'draw') + btn('undo', 'undo') + '</div>' +
                    '<div class="fp-tool-group" data-group="aps">' + btn('aps', 'aps') + btn('plan-aps', 'plan') + '</div>' +
                    '<div class="fp-tool-group" data-group="done">' + btn('done', 'done') + '</div>';
                L.DomEvent.disableClickPropagation(div);
                L.DomEvent.on(div, 'click', function (e) {
                    L.DomEvent.preventDefault(e);
                    var a = e.target.closest('a[data-tool]');
                    if (!a || a.classList.contains('disabled')) return;
                    self._onToolboxClick(a.getAttribute('data-tool'));
                });
                return div;
            }
        });
        this._wallToolbox = new Toolbox();
        this._wallTool = 'pan';
    },

    setToolboxState: function (json) {
        this._toolboxState = JSON.parse(json);
        this._updateWallToolbox();
    },

    _onToolboxClick: function (tool) {
        if (tool === 'pan' && this._isDrawing) { this._setDrawPan(!this._drawPan); return; }
        if (tool === 'pan' || tool === 'select') { this._setWallTool(tool); return; }
        if (tool === 'undo' && this._isDrawing) { this._undoLastPoint(); return; }
        // Done ends the innermost thing in progress: the shape, then the mode, then the building's editing.
        if (tool === 'done' && this._isDrawing && this._currentWall && this._currentWall.points.length > 0) {
            this.commitCurrentWall();
            return;
        }
        if (this._dotNetRef) this._dotNetRef.invokeMethodAsync('OnToolboxAction', tool);
    },

    // Undo while drawing: drops the last point of the shape in progress, or the last saved wall
    // when no shape is in progress.
    _undoLastPoint: function () {
        var cw = this._currentWall;
        if (!cw || cw.points.length === 0) { this.deleteLastWall(); return; }
        cw.points.pop();
        if (cw.materials && cw.materials.length > cw.points.length - 1) cw.materials.pop();
        var dropLast = function (group) {
            if (!group) return;
            var layers = group.getLayers();
            if (layers.length > 0) group.removeLayer(layers[layers.length - 1]);
        };
        dropLast(this._currentWallVertices);
        if (cw.points.length >= 1) { dropLast(this._currentWallSegLines); dropLast(this._currentWallLabels); }
        if (this._previewLine) { this._map.removeLayer(this._previewLine); this._previewLine = null; }
        if (this._previewLengthLabel) { this._map.removeLayer(this._previewLengthLabel); this._previewLengthLabel = null; }
        if (cw.points.length < 2) this._refAngle = null;
    },

    // Pan during Draw Layout: drag moves the map, a click still adds a point. Leaflet drops the
    // click that ends a drag, so panning never places a point.
    _setDrawPan: function (on) {
        var m = this._map;
        this._drawPan = !!on;
        if (m && this._isDrawing) {
            if (this._drawPan) m.dragging.enable(); else m.dragging.disable();
            m.getContainer().style.cursor = this._drawPan ? '' : 'crosshair';
        }
        this._renderToolbox();
    },

    _setWallTool: function (tool) {
        this._wallTool = tool === 'select' ? 'select' : 'pan';
        this._renderToolbox();
    },

    // Shows the toolbox whenever the map can be edited. Pan / Select work only on a selected floor's
    // walls outside Draw Layout and AP placement; leaving that state falls back to Pan, so no unseen
    // Select stays active.
    _updateWallToolbox: function () {
        var m = this._map;
        if (!m) return;
        this._initWallToolbox();
        var st = this._toolboxState;
        var added = !!this._wallToolbox._map;
        if (st.editable && !added) this._wallToolbox.addTo(m);
        if (!st.editable && added) this._wallToolbox.remove();
        var hasWalls = this._wallLayer && this._wallLayer.getLayers().length > 0;
        this._selectUsable = st.editable && st.floor && this._wallsEditable && hasWalls && !this._isDrawing && !this._placementActive;
        if (!this._selectUsable && !this._isDrawing) this._wallTool = 'pan';
        this._renderToolbox();
    },

    _renderToolbox: function () {
        var m = this._map;
        if (!m) return;
        var st = this._toolboxState;
        var self = this;
        var drawing = st.mode === 'walls';
        var labels = {
            pan: drawing ? 'Pan: drag to move the map while drawing' : 'Pan',
            select: 'Select walls: drag a box (Shift adds), then click or right-click one to change material',
            draw: drawing ? 'Done Drawing' : 'Draw Layout',
            undo: drawing ? 'Undo Point' : 'Undo Wall',
            aps: st.mode === 'aps' ? 'Done' : st.apLabel,
            'plan-aps': st.mode === 'plan-aps' ? 'Done' : st.planLabel,
            done: drawing ? (st.shape ? 'Finish Shape' : 'Done Drawing')
                : (st.mode === 'aps' || st.mode === 'plan-aps') ? 'Done' : 'Done Editing'
        };
        var active = {
            pan: drawing ? !!this._drawPan : (this._wallTool === 'pan' && st.mode !== 'aps' && st.mode !== 'plan-aps'),
            select: this._wallTool === 'select' && this._selectUsable,
            draw: drawing, undo: false, aps: st.mode === 'aps', 'plan-aps': st.mode === 'plan-aps'
        };
        var disabled = {
            pan: false, select: !this._selectUsable,
            draw: !st.floor, undo: !st.floor, aps: false, 'plan-aps': false
        };
        var el = this._wallToolbox && this._wallToolbox.getContainer();
        if (el) {
            el.querySelectorAll('a[data-tool]').forEach(function (a) {
                var t = a.getAttribute('data-tool');
                a.classList.toggle('active', !!active[t]);
                a.classList.toggle('disabled', !!disabled[t]);
                a.setAttribute('aria-label', labels[t]);
                a.setAttribute('data-tooltip', labels[t]);
                if (a._tippy) a._tippy.setContent(labels[t]);
            });
            el.querySelector('[data-group="draw"]').style.display = st.floor ? '' : 'none';
            var editing = drawing || st.mode === 'aps' || st.mode === 'plan-aps' || !!st.building;
            el.querySelector('[data-group="done"]').style.display = editing ? '' : 'none';
            el.querySelector('[data-group="walls"] [data-tool="select"]').style.display = st.floor ? '' : 'none';
        }
        m.getContainer().classList.toggle('fp-tool-select', this._wallTool === 'select' && self._selectUsable);
    },

    // Liang-Barsky clip: true when segment a-b touches the box (container pixels).
    _segHitsBox: function (a, b, box) {
        var t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
        var p = [-dx, dx, -dy, dy];
        var q = [a.x - box.minX, box.maxX - a.x, a.y - box.minY, box.maxY - a.y];
        for (var i = 0; i < 4; i++) {
            if (p[i] === 0) { if (q[i] < 0) return false; continue; }
            var r = q[i] / p[i];
            if (p[i] < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
        }
        return true;
    },

    // The current selection as a segment list: the multi-selection, else the single segment or
    // whole shape picked by a plain click, so Shift+click and Shift+drag add to it.
    _currentSelList: function () {
        var sel = (this._multiSel || []).slice();
        var single = this._wallSelection;
        if (sel.length === 0 && single && single.wallIdx !== null && this._allWalls[single.wallIdx]) {
            if (single.segIdx !== null) {
                sel.push({ wi: single.wallIdx, si: single.segIdx });
            } else {
                for (var k = 0; k < this._allWalls[single.wallIdx].points.length - 1; k++) sel.push({ wi: single.wallIdx, si: k });
            }
        }
        return sel;
    },

    _toggleMultiSel: function (wi, si, latlng) {
        var sel = this._currentSelList();
        var idx = -1;
        for (var i = 0; i < sel.length; i++) if (sel[i].wi === wi && sel[i].si === si) { idx = i; break; }
        if (idx >= 0) sel.splice(idx, 1); else sel.push({ wi: wi, si: si });
        this._showMultiSel(sel);
    },

    _clearMultiSel: function () {
        if (!this._multiSel) return;
        this._multiSel = null;
        if (this._wallHighlightLayer) this._wallHighlightLayer.clearLayers();
        if (this._map) this._map.closePopup();
    },

    _segMaterial: function (wall, si) {
        return (wall.materials && si < wall.materials.length && wall.materials[si]) ? wall.materials[si] : wall.material;
    },

    // Highlights the selection only. The popup opens on a click or right-click on a selected segment:
    // opening it at the end of a drag lost a race with Leaflet's close-popup-on-map-click.
    _showMultiSel: function (sel) {
        var m = this._map;
        var self = this;
        this._wallHighlightLayer.clearLayers();
        m.closePopup();
        this._wallSelection = { wallIdx: null, segIdx: null };
        if (!sel || sel.length === 0) { this._multiSel = null; return; }
        this._multiSel = sel;
        sel.forEach(function (s) {
            self._addSelHighlight(s.wi, s.si, { color: '#facc15', weight: 8, opacity: 0.9 });
        });
    },

    _inMultiSel: function (wi, si) {
        if (!this._multiSel) return false;
        for (var i = 0; i < this._multiSel.length; i++) {
            if (this._multiSel[i].wi === wi && this._multiSel[i].si === si) return true;
        }
        return false;
    },

    _openMultiSelPopup: function (latlng) {
        var m = this._map;
        var self = this;
        var sel = this._multiSel;
        if (!sel || sel.length === 0) return;
        var mats = {};
        sel.forEach(function (s) { mats[self._segMaterial(self._allWalls[s.wi], s.si)] = true; });
        var matKeys = Object.keys(mats);
        var common = matKeys.length === 1 ? matKeys[0] : null;

        var labels = this._materialLabels;
        var opts = common ? '' : '<option value="" selected disabled>Mixed materials</option>';
        for (var k in labels) {
            opts += '<option value="' + k + '"' + (k === common ? ' selected' : '') + '>' + labels[k] + '</option>';
        }
        var html = '<div style="text-align:center;min-width:180px;padding-top:14px">' +
            '<select style="width:100%;padding:3px;margin-bottom:6px;background:#1e293b;color:#e0e0e0;border:1px solid #475569;border-radius:3px" ' +
            'onchange="fpEditor.changeMultiMat(this.value)">' + opts + '</select><br/>' +
            '<span style="font-size:11px;color:#94a3b8">' + sel.length + ' segment' + (sel.length !== 1 ? 's' : '') + '</span><br/>' +
            (this._straightRun(sel) ? '<button onclick="fpEditor.mergeMultiSel()" style="margin-top:4px;padding:2px 10px;background:#4f46e5;color:#fff;border:none;border-radius:3px;cursor:pointer;margin-right:4px">Merge</button>' : '') +
            '<button onclick="fpEditor.deleteMultiSel()" style="margin-top:4px;padding:2px 10px;background:#dc2626;color:#fff;border:none;border-radius:3px;cursor:pointer">Delete</button></div>';
        var popup = L.popup(this._wallPopupOpts).setLatLng(latlng).setContent(html);
        popup.on('remove', function () {
            if (self._multiSelPopup !== popup) return;
            self._multiSelPopup = null;
            self._multiSelPopupClosedAt = Date.now();
        });
        this._multiSelPopup = popup;
        popup.openOn(m);
    },

    changeMultiMat: function (mat) {
        var self = this;
        var sel = this._multiSel;
        if (!sel || !mat) return;
        sel.forEach(function (s) {
            var wall = self._allWalls[s.wi];
            if (!wall) return;
            if (!wall.materials) {
                wall.materials = [];
                for (var k = 0; k < wall.points.length - 1; k++) wall.materials.push(wall.material);
            }
            self._keepSpanIfSameKind(wall, s.si, wall.materials[s.si], mat);
            wall.materials[s.si] = mat;
        });
        this._allWalls.forEach(function (w) { self._collapseMaterials(w); });
        this._clearMultiSel();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    // Drops the per-segment list when every segment shares one material. Spans (an imported
    // window's sill and head) are per segment too, so the list stays while any span is set.
    _collapseMaterials: function (wall) {
        if (wall.spans && !wall.spans.some(function (s) { return s; })) delete wall.spans;
        if (wall.spans) return;
        if (!wall.materials || wall.materials.length === 0) return;
        var first = wall.materials[0];
        for (var i = 1; i < wall.materials.length; i++) if (wall.materials[i] !== first) return;
        wall.material = first;
        delete wall.materials;
    },

    // A wall as a run of points with one material and span per segment, optionally reversed.
    _asRun: function (wall, reverse) {
        var pts = wall.points.slice(), mats = [], spans = [];
        for (var k = 0; k < pts.length - 1; k++) {
            mats.push(this._segMaterial(wall, k));
            spans.push((wall.spans && wall.spans[k]) || null);
        }
        if (reverse) { pts.reverse(); mats.reverse(); spans.reverse(); }
        return { points: pts, mats: mats, spans: spans };
    },

    // A drawn wall whose end lands on the open end of another wall joins it, so an outline opened
    // by deleting a segment closes again when the gap is redrawn. The longer wall keeps its
    // direction and the joined one turns to run with it: a wall's exterior face follows its
    // direction, so a piece drawn the other way round would show inside out in 3D.
    _joinAtEnds: function (wall) {
        var self = this;
        var near = function (a, b) {
            return self._map.distance(L.latLng(a.lat, a.lng), L.latLng(b.lat, b.lng)) <= 0.05;
        };
        var closed = function (w) {
            var p = w.points;
            return p.length >= 4 && near(p[0], p[p.length - 1]);
        };
        var cur = wall;
        for (var guard = 0; guard < 16 && !closed(cur); guard++) {
            var joined = null;
            for (var i = 0; i < this._allWalls.length && !joined; i++) {
                var o = this._allWalls[i];
                if (o === cur || o.points.length < 2 || closed(o)) continue;
                var cs = cur.points[0], ce = cur.points[cur.points.length - 1];
                var os = o.points[0], oe = o.points[o.points.length - 1];
                var curLonger = cur.points.length >= o.points.length;
                var a = null, b = null;
                if (near(ce, os)) { a = this._asRun(cur, false); b = this._asRun(o, false); }
                else if (near(cs, oe)) { a = this._asRun(o, false); b = this._asRun(cur, false); }
                else if (near(ce, oe)) {
                    if (curLonger) { a = this._asRun(cur, false); b = this._asRun(o, true); }
                    else { a = this._asRun(o, false); b = this._asRun(cur, true); }
                }
                else if (near(cs, os)) {
                    if (curLonger) { a = this._asRun(o, true); b = this._asRun(cur, false); }
                    else { a = this._asRun(cur, true); b = this._asRun(o, false); }
                }
                if (!a) continue;
                joined = {
                    points: a.points.concat(b.points.slice(1)),
                    material: a.mats[0],
                    materials: a.mats.concat(b.mats),
                    spans: a.spans.concat(b.spans)
                };
                this._allWalls = this._allWalls.filter(function (w) { return w !== cur && w !== o; });
                this._allWalls.push(joined);
                cur = joined;
            }
            if (!joined) break;
        }
        if (closed(cur)) {
            var p0 = cur.points[0];
            cur.points[cur.points.length - 1] = { lat: p0.lat, lng: p0.lng };
        }
        this._collapseMaterials(cur);
        if (!cur.spans || !cur.spans.some(function (s) { return s; })) delete cur.spans;
    },

    // 'door', 'window', or null for a full-height wall material.
    _openingKind: function (mat) {
        if (typeof mat !== 'string') return null;
        if (mat.indexOf('door_') === 0) return 'door';
        if (mat.indexOf('window_') === 0) return 'window';
        return null;
    },

    // A segment keeps its height band only while it stays the same kind of opening: a window
    // re-paned keeps its measured sill and head, but a window turned door or wall loses it.
    _keepSpanIfSameKind: function (wall, si, oldMat, newMat) {
        if (!wall.spans) return;
        var kind = this._openingKind(oldMat);
        if (kind === null || kind !== this._openingKind(newMat)) wall.spans[si] = null;
        if (!wall.spans.some(function (s) { return s; })) delete wall.spans;
    },

    // Removes the selected segments. Each wall splits into its runs of kept segments; on a closed
    // shape the run that wraps past the start joins the first run.
    deleteMultiSel: function () {
        var sel = this._multiSel;
        if (!sel || sel.length === 0) return;
        this._removeSegments(sel);
        this._clearMultiSel();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    _removeSegments: function (sel) {
        var self = this;
        var drop = {};
        sel.forEach(function (s) { drop[s.wi + ':' + s.si] = true; });

        var result = [];
        this._allWalls.forEach(function (wall, wi) {
            var n = wall.points.length - 1;
            var runs = [], run = null;
            for (var si = 0; si < n; si++) {
                if (drop[wi + ':' + si]) { run = null; continue; }
                if (!run) { run = { points: [wall.points[si]], mats: [], spans: [] }; runs.push(run); }
                run.points.push(wall.points[si + 1]);
                run.mats.push(self._segMaterial(wall, si));
                run.spans.push((wall.spans && wall.spans[si]) || null);
            }
            if (runs.length === 0) return;
            var first = wall.points[0], last = wall.points[n];
            var closed = n >= 3 && first.lat === last.lat && first.lng === last.lng;
            if (runs.length === 1 && runs[0].mats.length === n) { result.push(wall); return; }
            if (closed && runs.length > 1 && !drop[wi + ':0'] && !drop[wi + ':' + (n - 1)]) {
                var head = runs.shift(), tail = runs.pop();
                runs.push({ points: tail.points.concat(head.points.slice(1)), mats: tail.mats.concat(head.mats), spans: tail.spans.concat(head.spans) });
            }
            runs.forEach(function (r) {
                var w = { points: r.points, material: r.mats[0], materials: r.mats, spans: r.spans };
                self._collapseMaterials(w);
                result.push(w);
            });
        });
        this._allWalls = result;
    },

    // Selected segments of one material that lie on one straight line with no gap: the line's two
    // ends, the material, and the shared height band (null unless every segment has the same one).
    _straightRun: function (sel) {
        var self = this, m = this._map;
        if (!sel || sel.length < 2) return null;
        var segs = sel.map(function (s) {
            var w = self._allWalls[s.wi];
            return { a: w.points[s.si], b: w.points[s.si + 1], mat: self._segMaterial(w, s.si), span: (w.spans && w.spans[s.si]) || null };
        });
        var mat = segs[0].mat, span = JSON.stringify(segs[0].span);
        if (segs.some(function (g) { return g.mat !== mat; })) return null;
        var sameSpan = segs.every(function (g) { return JSON.stringify(g.span) === span; });
        // Local meters about the first point.
        var o = segs[0].a, ky = m.distance(L.latLng(o.lat, o.lng), L.latLng(o.lat + 1e-4, o.lng)) / 1e-4;
        var kx = m.distance(L.latLng(o.lat, o.lng), L.latLng(o.lat, o.lng + 1e-4)) / 1e-4;
        var xy = function (p) { return { x: (p.lng - o.lng) * kx, y: (p.lat - o.lat) * ky, p: p }; };
        var pts = [];
        segs.forEach(function (g) { pts.push(xy(g.a), xy(g.b)); });
        var P = null, Q = null, best = -1;
        for (var i = 0; i < pts.length; i++) for (var j = i + 1; j < pts.length; j++) {
            var d = Math.hypot(pts[i].x - pts[j].x, pts[i].y - pts[j].y);
            if (d > best) { best = d; P = pts[i]; Q = pts[j]; }
        }
        if (best < 0.1) return null;
        var ux = (Q.x - P.x) / best, uy = (Q.y - P.y) / best;
        var spans = [];
        for (var k = 0; k < pts.length; k++) {
            var dx = pts[k].x - P.x, dy = pts[k].y - P.y;
            if (Math.abs(dx * uy - dy * ux) > 0.15) return null;
        }
        segs.forEach(function (g) {
            var ta = (xy(g.a).x - P.x) * ux + (xy(g.a).y - P.y) * uy;
            var tb = (xy(g.b).x - P.x) * ux + (xy(g.b).y - P.y) * uy;
            spans.push([Math.min(ta, tb), Math.max(ta, tb)]);
        });
        spans.sort(function (a, b) { return a[0] - b[0]; });
        var reach = spans[0][0];
        if (reach > 0.05) return null;
        for (var n = 0; n < spans.length; n++) {
            if (spans[n][0] > reach + 0.05) return null;
            reach = Math.max(reach, spans[n][1]);
        }
        if (reach < best - 0.05) return null;
        return { p: { lat: P.p.lat, lng: P.p.lng }, q: { lat: Q.p.lat, lng: Q.p.lng }, mat: mat, span: sameSpan ? segs[0].span : null };
    },

    // Merges a straight run of selected segments into one segment, rejoining the walls on either side.
    mergeMultiSel: function () {
        var sel = this._multiSel;
        var run = this._straightRun(sel);
        if (!run) return;
        this._removeSegments(sel);
        var w = { points: [run.p, run.q], material: run.mat };
        if (run.span) { w.materials = [run.mat]; w.spans = [run.span]; }
        this._allWalls.push(w);
        this._joinAtEnds(w);
        this._clearMultiSel();
        if (this._map) this._map.closePopup();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    _wallArea: function (wall) {
        // Shoelace formula for polygon area (implicitly closes the shape)
        var pts = wall.points;
        if (!pts || pts.length < 3) return 0;
        var area = 0;
        for (var i = 0; i < pts.length; i++) {
            var j = (i + 1) % pts.length;
            area += pts[i].lng * pts[j].lat;
            area -= pts[j].lng * pts[i].lat;
        }
        return Math.abs(area) / 2;
    },

    moveWall: function (wi) {
        var wall = this._allWalls[wi];
        if (!wall) return;

        // If this shape covers the majority of the building's surface area,
        // promote to building move (all floors, APs, bounds)
        var wallArea = this._wallArea(wall);
        var totalArea = 0;
        for (var i = 0; i < this._allWalls.length; i++) {
            totalArea += this._wallArea(this._allWalls[i]);
        }
        if (totalArea > 0 && wallArea / totalArea > 0.5) {
            this._moveBuildingByWall(wi);
            return;
        }

        var m = this._map;
        var self = this;
        m.closePopup();
        if (this._wallHighlightLayer) this._wallHighlightLayer.clearLayers();
        m.dragging.disable();
        this._startEdgePan();
        m.getContainer().style.cursor = 'move';

        // Compute centroid as drag anchor
        var cLat = 0, cLng = 0;
        for (var i = 0; i < wall.points.length; i++) {
            cLat += wall.points[i].lat;
            cLng += wall.points[i].lng;
        }
        cLat /= wall.points.length;
        cLng /= wall.points.length;

        // Draw shape preview in highlight layer
        var drawPreview = function (dLat, dLng) {
            self._wallHighlightLayer.clearLayers();
            for (var j = 0; j < wall.points.length - 1; j++) {
                L.polyline(
                    [[wall.points[j].lat + dLat, wall.points[j].lng + dLng],
                     [wall.points[j + 1].lat + dLat, wall.points[j + 1].lng + dLng]],
                    { color: '#60a5fa', weight: 4, opacity: 0.8, interactive: false }
                ).addTo(self._wallHighlightLayer);
            }
        };
        drawPreview(0, 0);

        var moveHandler = function (e) {
            var dLat = e.latlng.lat - cLat;
            var dLng = e.latlng.lng - cLng;
            drawPreview(dLat, dLng);
        };
        var finishMove = function (e) {
            m.off('mousemove', moveHandler);
            m.off('click', finishMove);
            self._activeMoveHandlers = null;
            self._stopEdgePan();
            m.dragging.enable();
            m.getContainer().style.cursor = '';
            // Apply delta to all points
            var dLat = e.latlng.lat - cLat;
            var dLng = e.latlng.lng - cLng;
            for (var k = 0; k < wall.points.length; k++) {
                wall.points[k].lat += dLat;
                wall.points[k].lng += dLng;
            }
            self._wallHighlightLayer.clearLayers();
            self._wallSelection = { wallIdx: null, segIdx: null };
            self._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(self._allWalls));
        };
        self._activeMoveHandlers = { moveHandler: moveHandler, finishHandler: finishMove };
        m.on('mousemove', moveHandler);
        m.on('click', finishMove);
    },

    moveBuilding: function () {
        if (!this._allWalls || this._allWalls.length === 0) return;
        this._moveBuildingByWall(-1);
    },

    _moveBuildingByWall: function (wi) {
        var m = this._map;
        var self = this;
        m.closePopup();
        if (this._wallHighlightLayer) this._wallHighlightLayer.clearLayers();
        m.dragging.disable();
        this._startEdgePan();
        m.getContainer().style.cursor = 'move';

        // Compute centroid from all walls as drag anchor
        var cLat = 0, cLng = 0, count = 0;
        for (var w = 0; w < this._allWalls.length; w++) {
            for (var i = 0; i < this._allWalls[w].points.length; i++) {
                cLat += this._allWalls[w].points[i].lat;
                cLng += this._allWalls[w].points[i].lng;
                count++;
            }
        }
        if (count === 0) return;
        cLat /= count;
        cLng /= count;

        // Preview ALL walls on this floor (exterior + interior) shifting together
        var drawPreview = function (dLat, dLng) {
            self._wallHighlightLayer.clearLayers();
            for (var w = 0; w < self._allWalls.length; w++) {
                var ww = self._allWalls[w];
                var isHighlighted = wi >= 0 && w === wi;
                var color = isHighlighted ? '#60a5fa' : (wi < 0 ? '#60a5fa' : '#94a3b8');
                for (var j = 0; j < ww.points.length - 1; j++) {
                    L.polyline(
                        [[ww.points[j].lat + dLat, ww.points[j].lng + dLng],
                         [ww.points[j + 1].lat + dLat, ww.points[j + 1].lng + dLng]],
                        { color: color, weight: isHighlighted ? 4 : (wi < 0 ? 3 : 2), opacity: 0.8, interactive: false }
                    ).addTo(self._wallHighlightLayer);
                }
            }
        };
        drawPreview(0, 0);

        var moveHandler = function (e) {
            drawPreview(e.latlng.lat - cLat, e.latlng.lng - cLng);
        };
        var finishMove = function (e) {
            m.off('mousemove', moveHandler);
            m.off('click', finishMove);
            self._activeMoveHandlers = null;
            self._stopEdgePan();
            m.dragging.enable();
            m.getContainer().style.cursor = '';
            self._wallHighlightLayer.clearLayers();
            self._wallSelection = { wallIdx: null, segIdx: null };

            var dLat = e.latlng.lat - cLat;
            var dLng = e.latlng.lng - cLng;

            // Apply delta to ALL walls on this floor
            for (var w = 0; w < self._allWalls.length; w++) {
                for (var k = 0; k < self._allWalls[w].points.length; k++) {
                    self._allWalls[w].points[k].lat += dLat;
                    self._allWalls[w].points[k].lng += dLng;
                }
            }

            // Save this floor's walls, then tell Blazor to move the entire building
            self._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(self._allWalls));
            self._dotNetRef.invokeMethodAsync('OnBuildingMoveFromJs', dLat, dLng);
        };
        self._activeMoveHandlers = { moveHandler: moveHandler, finishHandler: finishMove };
        m.on('mousemove', moveHandler);
        m.on('click', finishMove);
    },

    deleteLastWall: function () {
        if (!this._allWalls || this._allWalls.length === 0) return;
        this._allWalls.pop();
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls));
    },

    // Snap to nearby vertices from existing walls and background walls (adjacent floors)
    // Snap to nearby wall vertices (priority) or perpendicular projection onto wall segments.
    // Convex hull (Andrew's monotone chain). Input: [[lat,lng],...]. Returns hull points.
    // Monotone chain convex hull. Accepts [{lat,lng}, ...], returns [[lat,lng], ...] for Leaflet.
    _convexHull: function (points) {
        if (!points || points.length < 3) return points ? points.map(function (p) { return [p.lat, p.lng]; }) : [];
        var sorted = points.slice().sort(function (a, b) { return a.lat - b.lat || a.lng - b.lng; });
        var unique = [sorted[0]];
        for (var i = 1; i < sorted.length; i++) {
            if (sorted[i].lat !== sorted[i - 1].lat || sorted[i].lng !== sorted[i - 1].lng) unique.push(sorted[i]);
        }
        if (unique.length < 3) return unique.map(function (p) { return [p.lat, p.lng]; });
        function cross(o, a, b) { return (a.lat - o.lat) * (b.lng - o.lng) - (a.lng - o.lng) * (b.lat - o.lat); }
        var lower = [];
        for (var i = 0; i < unique.length; i++) {
            while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], unique[i]) <= 0) lower.pop();
            lower.push(unique[i]);
        }
        var upper = [];
        for (var i = unique.length - 1; i >= 0; i--) {
            while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], unique[i]) <= 0) upper.pop();
            upper.push(unique[i]);
        }
        lower.pop(); upper.pop();
        return lower.concat(upper).map(function (p) { return [p.lat, p.lng]; });
    },

    // Distance from point to line segment (returns meters)
    _pointToSegmentDistanceM: function (m, lat, lng, a, b) {
        var cosLat = Math.cos(lat * Math.PI / 180);
        var ax = (a.lng - lng) * cosLat, ay = a.lat - lat;
        var bx = (b.lng - lng) * cosLat, by = b.lat - lat;
        var dx = bx - ax, dy = by - ay;
        var lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-10) return m.distance(L.latLng(lat, lng), L.latLng(a.lat, a.lng));
        var t = Math.max(0, Math.min(1, ((-ax) * dx + (-ay) * dy) / lenSq));
        var closestLat = a.lat + t * (b.lat - a.lat);
        var closestLng = a.lng + t * (b.lng - a.lng);
        return m.distance(L.latLng(lat, lng), L.latLng(closestLat, closestLng));
    },

    // Min distance (meters) from point to any wall segment in _allWalls
    _nearestWallDistanceM: function (lat, lng) {
        var m = this._map;
        if (!m || !this._allWalls || this._allWalls.length === 0) return Infinity;
        var minDist = Infinity;
        for (var wi = 0; wi < this._allWalls.length; wi++) {
            var pts = this._allWalls[wi].points;
            for (var pi = 0; pi < pts.length - 1; pi++) {
                var d = this._pointToSegmentDistanceM(m, lat, lng, pts[pi], pts[pi + 1]);
                if (d < minDist) minDist = d;
            }
        }
        return minDist;
    },

    // Show a temporary warning toast overlaying the map
    _showDrawWarning: function (msg) {
        var container = this._map && this._map.getContainer();
        if (!container) return;
        var toast = document.createElement('div');
        toast.className = 'fp-draw-warning';
        toast.textContent = msg;
        container.appendChild(toast);
        setTimeout(function () {
            toast.style.opacity = '0';
            setTimeout(function () { if (toast.parentNode) toast.remove(); }, 500);
        }, 6000);
    },

    // Returns { lat, lng, type: 'vertex'|'segment', segA, segB } or null.
    // segA/segB are the segment endpoints (only for type='segment').
    _snapToVertex: function (lat, lng, snapPixels, bgMaxMeters) {
        var m = this._map;
        if (!m) return null;
        var mousePixel = m.latLngToContainerPoint(L.latLng(lat, lng));
        var mouseLl = L.latLng(lat, lng);
        var bestVertexDist = snapPixels;
        var bestVertexPt = null;
        var bestSegDist = snapPixels;
        var bestSegPt = null;
        if (bgMaxMeters === undefined) bgMaxMeters = 6; // default ~20ft

        // maxMeters: optional real-world distance cap (for background walls)
        function checkWalls(walls, maxMeters) {
            if (!walls) return;
            for (var wi = 0; wi < walls.length; wi++) {
                var pts = walls[wi].points;
                if (!pts) continue;
                // Check vertices (include adjacent segment endpoints for angle reference)
                for (var pi = 0; pi < pts.length; pi++) {
                    var px = m.latLngToContainerPoint(L.latLng(pts[pi].lat, pts[pi].lng));
                    var d = mousePixel.distanceTo(px);
                    if (d < bestVertexDist) {
                        // Enforce real-world distance cap for background walls
                        if (maxMeters && m.distance(mouseLl, L.latLng(pts[pi].lat, pts[pi].lng)) > maxMeters) continue;
                        bestVertexDist = d;
                        // Include adjacent segment for angle reference
                        var adjA = null, adjB = null;
                        if (pi > 0) { adjA = { lat: pts[pi - 1].lat, lng: pts[pi - 1].lng }; adjB = { lat: pts[pi].lat, lng: pts[pi].lng }; }
                        else if (pi < pts.length - 1) { adjA = { lat: pts[pi].lat, lng: pts[pi].lng }; adjB = { lat: pts[pi + 1].lat, lng: pts[pi + 1].lng }; }
                        bestVertexPt = { lat: pts[pi].lat, lng: pts[pi].lng, type: 'vertex', segA: adjA, segB: adjB };
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
                    if (t < -0.02 || t > 1.02) continue;
                    var projPx = L.point(aPx.x + t * dx, aPx.y + t * dy);
                    var dist = mousePixel.distanceTo(projPx);
                    if (dist < bestSegDist) {
                        if (maxMeters) {
                            var projLat = pts[si].lat + t * (pts[si + 1].lat - pts[si].lat);
                            var projLng = pts[si].lng + t * (pts[si + 1].lng - pts[si].lng);
                            if (m.distance(mouseLl, L.latLng(projLat, projLng)) > maxMeters) continue;
                        }
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
        checkWalls(this._bgWalls, bgMaxMeters); // real-world distance cap for other buildings
        return bestVertexPt || bestSegPt;
    },

    // Find the angle of the nearest background wall segment (unlimited distance).
    // Used for cornerstone placement to align new buildings to neighbors.
    _nearestBgWallAngle: function (lat, lng) {
        var m = this._map;
        if (!m || !this._bgWalls || this._bgWalls.length === 0) return null;
        var mouseLl = L.latLng(lat, lng);
        var bestDist = Infinity;
        var bestAngle = null;
        for (var wi = 0; wi < this._bgWalls.length; wi++) {
            var pts = this._bgWalls[wi].points;
            if (!pts || pts.length < 2) continue;
            for (var si = 0; si < pts.length - 1; si++) {
                var midLat = (pts[si].lat + pts[si + 1].lat) / 2;
                var midLng = (pts[si].lng + pts[si + 1].lng) / 2;
                var dist = m.distance(mouseLl, L.latLng(midLat, midLng));
                if (dist < bestDist) {
                    var cosLat = Math.cos(pts[si].lat * Math.PI / 180);
                    var dx = (pts[si + 1].lng - pts[si].lng) * cosLat;
                    var dy = pts[si + 1].lat - pts[si].lat;
                    if (Math.sqrt(dx * dx + dy * dy) > 1e-8) {
                        bestDist = dist;
                        bestAngle = Math.atan2(dy, dx);
                    }
                }
            }
        }
        return bestAngle;
    },

    // Check if drawing line from prev to snap point is within ±5° of perpendicular to the target wall.
    // If so, returns adjusted snap point at the exact perpendicular foot; otherwise null.
    _perpSnap: function (prev, vtxSnap) {
        if (!vtxSnap || vtxSnap.type !== 'segment' || !vtxSnap.segA || !vtxSnap.segB) return null;
        var m = this._map;
        if (!m) return null;

        var prevPx = m.latLngToContainerPoint(L.latLng(prev.lat, prev.lng));
        var snapPx = m.latLngToContainerPoint(L.latLng(vtxSnap.lat, vtxSnap.lng));
        var aPx = m.latLngToContainerPoint(L.latLng(vtxSnap.segA.lat, vtxSnap.segA.lng));
        var bPx = m.latLngToContainerPoint(L.latLng(vtxSnap.segB.lat, vtxSnap.segB.lng));

        // Wall direction vector
        var wdx = bPx.x - aPx.x, wdy = bPx.y - aPx.y;
        var wlen = Math.sqrt(wdx * wdx + wdy * wdy);
        if (wlen < 1) return null;

        // Drawing direction: from prev to current snap point
        var ddx = snapPx.x - prevPx.x, ddy = snapPx.y - prevPx.y;
        var dlen = Math.sqrt(ddx * ddx + ddy * ddy);
        if (dlen < 1) return null;

        // cos(angle) between draw direction and wall direction
        // Perpendicular means cos ≈ 0, i.e. |dot| < sin(5°) ≈ 0.087
        var dot = (ddx * wdx + ddy * wdy) / (dlen * wlen);
        if (Math.abs(dot) > Math.sin(5 * Math.PI / 180)) return null;

        // Compute perpendicular foot of prev onto the wall segment line
        var t = ((prevPx.x - aPx.x) * wdx + (prevPx.y - aPx.y) * wdy) / (wdx * wdx + wdy * wdy);
        if (t < 0.01 || t > 0.99) return null; // outside segment

        var footLatLng = m.containerPointToLatLng(L.point(aPx.x + t * wdx, aPx.y + t * wdy));
        return {
            lat: footLatLng.lat, lng: footLatLng.lng,
            type: 'segment', segA: vtxSnap.segA, segB: vtxSnap.segB, isPerp: true
        };
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

    // A wall being drawn exists only here until it is committed; leaving the page drops it.
    hasPendingWall: function () {
        return !!(this._currentWall && this._currentWall.points && this._currentWall.points.length > 0);
    },

    enterDrawMode: function (wallsJson) {
        var m = this._map;
        if (!m) return;
        var self = this;

        this._isDrawing = true;
        if (!this._beforeUnload) {
            this._beforeUnload = function (e) {
                if (self.hasPendingWall()) { e.preventDefault(); e.returnValue = ''; }
            };
            window.addEventListener('beforeunload', this._beforeUnload);
        }
        this._clearMultiSel();
        this._updateWallToolbox();
        this._allWalls = JSON.parse(wallsJson);
        this._distanceWarnShown = false;
        m.dragging.disable();
        this._drawPan = false;
        this._startEdgePan();
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

        // Length snap: find matching parallel segment lengths in current wall + all existing walls
        this._snapLength = function (curLen, isRefDir) {
            if (self._refAngle === null) return null;
            var refA = self._refAngle;
            var curMeters = curLen * 111320;
            var bestDiff = 1.0; // snap threshold in meters
            var bestLen = null;

            var bestSource = '';
            function checkPoints(pts, source) {
                if (!pts || pts.length < 2) return;
                for (var j = 0; j < pts.length - 1; j++) {
                    var sCosLat = Math.cos(pts[j].lat * Math.PI / 180);
                    var sdx = (pts[j + 1].lng - pts[j].lng) * sCosLat;
                    var sdy = pts[j + 1].lat - pts[j].lat;
                    var pRef = Math.abs(sdx * Math.cos(refA) + sdy * Math.sin(refA));
                    var pPerp = Math.abs(sdx * Math.cos(refA + Math.PI / 2) + sdy * Math.sin(refA + Math.PI / 2));
                    var segIsRef = pRef >= pPerp;
                    if (segIsRef !== isRefDir) continue;
                    var segLen = m.distance(L.latLng(pts[j].lat, pts[j].lng), L.latLng(pts[j + 1].lat, pts[j + 1].lng));
                    var diff = Math.abs(curMeters - segLen);
                    if (diff < bestDiff && diff > 0.01) {
                        bestDiff = diff;
                        bestLen = curLen * (segLen / curMeters);
                        bestSource = source + ' seg' + j + ' (' + (segLen * 3.28084).toFixed(1) + "')";
                    }
                }
            }

            // Check current wall being drawn
            if (self._currentWall) checkPoints(self._currentWall.points, 'currentWall(' + (self._currentWall.material || '?') + ')');
            // Check all existing walls on this floor (same building only)
            if (self._allWalls) {
                for (var wi = 0; wi < self._allWalls.length; wi++) {
                    var w = self._allWalls[wi];
                    var wLabel = 'wall[' + wi + '](' + (w.material || '?') + ' @' + (w.points[0].lat).toFixed(5) + ',' + (w.points[0].lng).toFixed(5) + ')';
                    checkPoints(w.points, wLabel);
                }
            }
            // First shape on empty floor: also match same-building adjacent floor segment lengths
            if ((!self._allWalls || self._allWalls.length === 0) && self._sameBuildingWalls) {
                for (var bwi = 0; bwi < self._sameBuildingWalls.length; bwi++) {
                    checkPoints(self._sameBuildingWalls[bwi].points, 'sameBldg[' + bwi + ']');
                }
            }
            // if (bestLen !== null) console.log('lengthSnap:', bestSource, 'cur=' + (curMeters * 3.28084).toFixed(1) + "'");
            return bestLen;
        };

        // Click handler
        this._wallClickHandler = function (e) {
            // Close-shape snap
            if (self._snapToClose && self._currentWall && self._currentWall.points.length >= 3) {
                var fp = self._currentWall.points[0];
                var lastPt = self._currentWall.points[self._currentWall.points.length - 1];
                self._currentWall.points.push({ lat: fp.lat, lng: fp.lng });
                // Get current material from C# binding for the closing segment
                self._dotNetRef.invokeMethodAsync('GetCurrentWallMaterial').then(function (matInfo) {
                    if (self._currentWall && self._currentWall.materials) {
                        self._currentWall.materials.push(matInfo.material);
                        if (self._currentWallSegLines) {
                            L.polyline([[lastPt.lat, lastPt.lng], [fp.lat, fp.lng]],
                                { color: matInfo.color, weight: 4, opacity: 0.9 }).addTo(self._currentWallSegLines);
                        }
                    }
                    self.commitCurrentWall();
                });
                return;
            }

            var lat = e.latlng.lat, lng = e.latlng.lng;
            var didSnapToWall = false;
            // Cornerstone: first point of first shape on empty floor
            var isCornerstone = (!self._currentWall || self._currentWall.points.length === 0) &&
                (!self._allWalls || self._allWalls.length === 0);

            // Vertex/segment snap: snap to nearby existing wall vertices or perpendicular to segments
            if (!e.originalEvent.shiftKey) {
                // Wider bg snap (50ft/15m) for cornerstone, normal (20ft/6m) otherwise
                var vtxSnap = self._snapToVertex(lat, lng, 10, isCornerstone ? 15 : undefined);
                if (vtxSnap) {
                    // For segment snaps with a previous point, apply perpendicular adjustment
                    if (vtxSnap.type === 'segment' && self._currentWall && self._currentWall.points.length > 0) {
                        var prevPt = self._currentWall.points[self._currentWall.points.length - 1];
                        var perpAdj = self._perpSnap(prevPt, vtxSnap);
                        if (perpAdj) {
                            lat = perpAdj.lat;
                            lng = perpAdj.lng;
                        } else {
                            lat = vtxSnap.lat;
                            lng = vtxSnap.lng;
                        }
                    } else {
                        lat = vtxSnap.lat;
                        lng = vtxSnap.lng;
                    }
                    didSnapToWall = true;
                    // Set reference angle from snapped wall when starting a new shape
                    if (self._refAngle === null && vtxSnap.segA && vtxSnap.segB &&
                        (!self._currentWall || self._currentWall.points.length === 0)) {
                        var sCosLat = Math.cos(vtxSnap.segA.lat * Math.PI / 180);
                        var sdx = (vtxSnap.segB.lng - vtxSnap.segA.lng) * sCosLat;
                        var sdy = vtxSnap.segB.lat - vtxSnap.segA.lat;
                        self._refAngle = Math.atan2(sdy, sdx);
                    }
                }
                // Cornerstone: adopt angle from nearest bg wall even without vertex snap
                if (self._refAngle === null && isCornerstone) {
                    var bgAngle = self._nearestBgWallAngle(lat, lng);
                    if (bgAngle !== null) self._refAngle = bgAngle;
                }
            }

            if (self._currentWall && self._currentWall.points.length > 0) {
                var prev = self._currentWall.points[self._currentWall.points.length - 1];
                // Only apply angle snap if we didn't snap to an existing wall and shift isn't held
                if (!didSnapToWall && !e.originalEvent.shiftKey) {
                    var snapped = self._snapPoint(prev, lat, lng, false);
                    lat = snapped.lat;
                    lng = snapped.lng;
                }
                // Set reference angle from first segment; shift overrides any angle inherited from wall snap
                if ((self._refAngle === null || e.originalEvent.shiftKey) && self._currentWall.points.length === 1) {
                    var cosLat2 = Math.cos(prev.lat * Math.PI / 180);
                    var dx2 = (lng - prev.lng) * cosLat2;
                    var dy2 = lat - prev.lat;
                    self._refAngle = Math.atan2(dy2, dx2);
                }
            }
            // Warn once if starting a new unconnected wall far from existing walls
            var isFirstPoint = !self._currentWall || self._currentWall.points.length === 0;
            if (isFirstPoint && !didSnapToWall && !self._distanceWarnShown &&
                self._allWalls && self._allWalls.length > 0) {
                var nearestM = self._nearestWallDistanceM(lat, lng);
                if (nearestM > 15.24) { // ~50 ft
                    self._distanceWarnShown = true;
                    self._showDrawWarning('This point is far from existing walls. Click "Done Editing" to finish the current building before starting a new one.');
                }
            }

            self._dotNetRef.invokeMethodAsync('OnMapClickForWall', lat, lng);
        };

        // Double-click finishes the current shape (stays in draw mode).
        // The second click already queued an addWallPoint via async C# interop,
        // so we delay briefly to let that point arrive before committing.
        this._wallDblClickHandler = function (e) {
            L.DomEvent.stopPropagation(e);
            L.DomEvent.preventDefault(e);
            // The dblclick includes two click events that each add a point.
            // The second click's point is a duplicate - wait for it to arrive
            // from C# interop, pop it, then commit.
            setTimeout(function () {
                if (self._currentWall && self._currentWall.points.length > 1) {
                    self._currentWall.points.pop();
                    if (self._currentWall.materials && self._currentWall.materials.length > 0)
                        self._currentWall.materials.pop();
                    // Remove the duplicate vertex dot and segment line
                    if (self._currentWallVertices) {
                        var layers = self._currentWallVertices.getLayers();
                        if (layers.length > 0) self._currentWallVertices.removeLayer(layers[layers.length - 1]);
                    }
                    if (self._currentWallSegLines) {
                        var segs = self._currentWallSegLines.getLayers();
                        if (segs.length > 0) self._currentWallSegLines.removeLayer(segs[segs.length - 1]);
                    }
                    if (self._currentWallLabels) {
                        var labels = self._currentWallLabels.getLayers();
                        if (labels.length > 0) self._currentWallLabels.removeLayer(labels[labels.length - 1]);
                    }
                }
                self.commitCurrentWall();
            }, 80);
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
                // Wider bg snap (50ft/15m) for cornerstone (empty floor)
                var earlyBgMax = (!self._allWalls || self._allWalls.length === 0) ? 15 : undefined;
                var earlySnap = e.originalEvent.shiftKey ? null : self._snapToVertex(e.latlng.lat, e.latlng.lng, 10, earlyBgMax);
                if (!earlySnap && self._snapIndicator) {
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
                if (self._snapGuideLine) { m.removeLayer(self._snapGuideLine); self._snapGuideLine = null; }
                if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }
                return;
            }

            // Vertex/segment snap: check for nearby existing wall vertices or perpendicular projection
            var vtxSnap = e.originalEvent.shiftKey ? null : self._snapToVertex(e.latlng.lat, e.latlng.lng, 10);
            if (vtxSnap) {
                // For segment snaps, check if we're within ±5° of perpendicular and adjust
                var perpAdj = (vtxSnap.type === 'segment') ? self._perpSnap(prev, vtxSnap) : null;
                var snapTarget = perpAdj || vtxSnap;

                // Preview line: always dashed blue for snap (green right-angle marker is sufficient for perp feedback)
                var lineStyle = { color: '#60a5fa', weight: 2, dashArray: '6,4', opacity: 0.8 };
                if (!self._previewLine) {
                    self._previewLine = L.polyline([[prev.lat, prev.lng], [snapTarget.lat, snapTarget.lng]], lineStyle).addTo(m);
                } else {
                    self._previewLine.setLatLngs([[prev.lat, prev.lng], [snapTarget.lat, snapTarget.lng]]);
                    self._previewLine.setStyle(lineStyle);
                }
                // Show guide line along target wall for any segment snap
                if (vtxSnap.type === 'segment' && vtxSnap.segA && vtxSnap.segB) {
                    if (!self._snapGuideLine) {
                        self._snapGuideLine = L.polyline(
                            [[vtxSnap.segA.lat, vtxSnap.segA.lng], [vtxSnap.segB.lat, vtxSnap.segB.lng]],
                            { color: '#22c55e', weight: 2, dashArray: '4,4', opacity: 0.5, interactive: false }
                        ).addTo(m);
                    } else {
                        self._snapGuideLine.setLatLngs([[vtxSnap.segA.lat, vtxSnap.segA.lng], [vtxSnap.segB.lat, vtxSnap.segB.lng]]);
                    }
                    // Right angle marker: only when actually snapped to perpendicular
                    if (perpAdj) {
                        var sp = m.latLngToContainerPoint(L.latLng(snapTarget.lat, snapTarget.lng));
                        var ap2 = m.latLngToContainerPoint(L.latLng(vtxSnap.segA.lat, vtxSnap.segA.lng));
                        var bp2 = m.latLngToContainerPoint(L.latLng(vtxSnap.segB.lat, vtxSnap.segB.lng));
                        var wdx = bp2.x - ap2.x, wdy = bp2.y - ap2.y;
                        var wlen = Math.sqrt(wdx * wdx + wdy * wdy);
                        if (wlen > 0) {
                            var ux = wdx / wlen, uy = wdy / wlen;
                            var pp = m.latLngToContainerPoint(L.latLng(prev.lat, prev.lng));
                            var perpSide = (pp.x - sp.x) * (-uy) + (pp.y - sp.y) * ux;
                            var nx = perpSide >= 0 ? -uy : uy;
                            var ny = perpSide >= 0 ? ux : -ux;
                            var sz = 8;
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
                        if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }
                    }
                } else {
                    if (self._snapGuideLine) { m.removeLayer(self._snapGuideLine); self._snapGuideLine = null; }
                    if (self._snapAngleMarker) { m.removeLayer(self._snapAngleMarker); self._snapAngleMarker = null; }
                }
                self._updatePreviewLength(prev, snapTarget);
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
        this._isDrawing = false;
        if (this._beforeUnload) {
            window.removeEventListener('beforeunload', this._beforeUnload);
            this._beforeUnload = null;
        }

        // Commit any in-progress wall (reuses same logic as Finish Shape)
        this.commitCurrentWall();

        // Tear down draw mode
        this._stopEdgePan();
        m.dragging.enable();
        this._drawPan = false;
        m.getContainer().style.cursor = '';
        if (this._wallClickHandler) { m.off('click', this._wallClickHandler); this._wallClickHandler = null; }
        if (this._wallDblClickHandler) { m.off('dblclick', this._wallDblClickHandler); this._wallDblClickHandler = null; }
        if (this._wallMoveHandler) { m.off('mousemove', this._wallMoveHandler); this._wallMoveHandler = null; }
        m.doubleClickZoom.enable();
        this._updateWallToolbox();
    },

    addWallPoint: function (lat, lng, material, color) {
        var m = this._map;
        if (!m) return;

        if (!this._currentWall) {
            this._currentWall = { points: [], material: material, materials: [] };
            this._currentWallSegLines = L.layerGroup().addTo(this._wallLayer || m);
            this._currentWallVertices = L.layerGroup().addTo(m);
            this._currentWallLabels = L.layerGroup().addTo(m);
        }

        // Add vertex dot
        L.circleMarker([lat, lng], {
            radius: 5, color: color, fillColor: '#fff', fillOpacity: 1, weight: 2
        }).addTo(this._currentWallVertices);

        // Track per-segment material (added when we have a previous point to connect)
        var pts = this._currentWall.points;
        if (pts.length >= 1) {
            var prev = pts[pts.length - 1];
            this._currentWall.materials.push(material);
            L.polyline([[prev.lat, prev.lng], [lat, lng]], { color: color, weight: 4, opacity: 0.9 })
                .addTo(this._currentWallSegLines);
        }

        this._currentWall.points.push({ lat: lat, lng: lng });

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

    // Commit the current wall and reset for a new one, staying in draw mode.
    // Uses the same simple commit logic as exitDrawMode (Done Drawing) to avoid
    // the point-manipulation issues that can create zero-length segments.
    commitCurrentWall: function () {
        var m = this._map;
        if (!m) return;

        if (this._currentWall && this._currentWall.points.length >= 2) {
            // Clean up per-segment materials: if all same, simplify
            if (this._currentWall.materials && this._currentWall.materials.length > 0) {
                var allSame = true;
                var first = this._currentWall.materials[0];
                for (var mi = 1; mi < this._currentWall.materials.length; mi++) {
                    if (this._currentWall.materials[mi] !== first) { allSame = false; break; }
                }
                if (allSame) {
                    this._currentWall.material = first;
                    delete this._currentWall.materials;
                }
            }

            if (!this._allWalls) this._allWalls = [];

            // Auto-split: if drawing a 2-point segment near an existing wall,
            // split the existing wall and replace that section with the new material.
            // Works for any material - allows replacing wall sections with doors, windows, or different wall types.
            var cw = this._currentWall;
            var didSplit = false;

            if (cw.points.length === 2) {
                var splitTolerance = 0.15; // ~0.5 ft - both points must be essentially on the wall to trigger split
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
                        if (maxD < splitTolerance && maxD < bestD) {
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
                    var origMat;
                    if (!targetWall.materials) {
                        origMat = targetWall.material;
                        targetWall.materials = [];
                        for (var j = 0; j < targetWall.points.length - 1; j++) targetWall.materials.push(origMat);
                    } else {
                        origMat = targetWall.materials[targetSi] || targetWall.material;
                        targetWall.materials.splice(targetSi, 0, origMat, origMat);
                    }
                    targetWall.materials[targetSi + 1] = cw.material;
                    if (targetWall.spans) {
                        var origSpan = targetWall.spans[targetSi] || null;
                        targetWall.spans.splice(targetSi, 0, origSpan, origSpan);
                        targetWall.spans[targetSi + 1] = null;
                    }
                    didSplit = true;
                }
            }

            if (!didSplit) {
                this._allWalls.push(cw);
                this._joinAtEnds(cw);
            }
        }

        // Always notify C# to save walls and reset _isDrawingShape
        this._dotNetRef.invokeMethodAsync('SaveWallsFromJs', JSON.stringify(this._allWalls || []));

        // Clean up current wall visual state (but stay in draw mode)
        this._currentWall = null;
        this._refAngle = null;
        if (this._currentWallSegLines) { this._currentWallSegLines.remove(); this._currentWallSegLines = null; }
        if (this._currentWallVertices) { m.removeLayer(this._currentWallVertices); this._currentWallVertices = null; }
        if (this._currentWallLabels) { m.removeLayer(this._currentWallLabels); this._currentWallLabels = null; }
        if (this._previewLine) { m.removeLayer(this._previewLine); this._previewLine = null; }
        if (this._snapIndicator) { m.removeLayer(this._snapIndicator); this._snapIndicator = null; }
        if (this._snapGuideLine) { m.removeLayer(this._snapGuideLine); this._snapGuideLine = null; }
        if (this._snapAngleMarker) { m.removeLayer(this._snapAngleMarker); this._snapAngleMarker = null; }
        if (this._previewLengthLabel) { m.removeLayer(this._previewLengthLabel); this._previewLengthLabel = null; }
    },

    // ── Position Mode (4-corner resize + drag-to-move) ─────────────

    enterPositionMode: function (swLat, swLng, neLat, neLng, imageId) {
        var m = this._map;
        if (!m) return;
        var self = this;

        this.exitPositionMode();

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

        // Find the target overlay (multi-image or legacy single)
        var targetOverlay = imageId ? self._getOverlay(imageId) : self._overlay;
        var rotation = targetOverlay ? (targetOverlay._rotationDeg || 0) : 0;

        function makeHandle(latlng, corner) {
            var icon = L.divIcon({
                className: 'fp-corner-handle fp-corner-' + corner,
                iconSize: [12, 12],
                iconAnchor: [6, 6]
            });
            return L.marker(latlng, { icon: icon, draggable: true }).addTo(m);
        }

        // Place handles at rotated corners if image is rotated
        var axisBounds = L.latLngBounds(sw, ne);
        var rc = rotation ? self._getRotatedCorners(axisBounds, rotation, m) : {
            sw: sw, ne: ne,
            nw: L.latLng(ne.lat, sw.lng),
            se: L.latLng(sw.lat, ne.lng)
        };

        var swM = makeHandle(rc.sw, 'sw');
        var neM = makeHandle(rc.ne, 'ne');
        var nwM = makeHandle(rc.nw, 'nw');
        var seM = makeHandle(rc.se, 'se');
        this._corners = [swM, neM, nwM, seM];

        // Set/update cursor on marker elements to match the rotated diagonal direction
        // CW screen rotation subtracts from diagonal angle, so negate
        function updateCursors() {
            var c1 = self._getResizeCursor(45, -rotation);
            var c2 = self._getResizeCursor(135, -rotation);
            [swM, neM, nwM, seM].forEach(function (marker, i) {
                var el = marker.getElement();
                if (el) el.style.cursor = (i < 2) ? c1 : c2;
            });
        }
        setTimeout(updateCursors, 50);

        // Helper: update overlay bounds and reposition all handles from new axis-aligned bounds
        function updateFromBounds(newBounds, draggedMarker) {
            if (targetOverlay) targetOverlay.setBounds([newBounds.getSouthWest(), newBounds.getNorthEast()]);
            if (rotation) {
                var rc2 = self._getRotatedCorners(newBounds, rotation, m);
                if (draggedMarker !== swM) swM.setLatLng(rc2.sw);
                if (draggedMarker !== neM) neM.setLatLng(rc2.ne);
                if (draggedMarker !== nwM) nwM.setLatLng(rc2.nw);
                if (draggedMarker !== seM) seM.setLatLng(rc2.se);
            }
        }

        // Allow setImageRotation to update handles live during position mode
        self._positionUpdateFn = function (newDeg) {
            rotation = newDeg;
            var curBounds = targetOverlay ? targetOverlay.getBounds() : axisBounds;
            var rc2 = self._getRotatedCorners(curBounds, rotation, m);
            swM.setLatLng(rc2.sw);
            neM.setLatLng(rc2.ne);
            nwM.setLatLng(rc2.nw);
            seM.setLatLng(rc2.se);
            updateCursors();
        };

        // Corner drag handlers - for rotated images, un-rotate drag positions to get axis-aligned bounds
        // SW and NE are on one diagonal, NW and SE on the other
        swM.on('drag', function () {
            if (rotation) {
                var newBounds = self._boundsFromRotatedDiagonal(swM.getLatLng(), neM.getLatLng(), rotation, m);
                updateFromBounds(newBounds, swM);
            } else {
                var s = swM.getLatLng(), n = neM.getLatLng();
                nwM.setLatLng(L.latLng(n.lat, s.lng));
                seM.setLatLng(L.latLng(s.lat, n.lng));
                if (targetOverlay) targetOverlay.setBounds([s, n]);
            }
        });
        neM.on('drag', function () {
            if (rotation) {
                var newBounds = self._boundsFromRotatedDiagonal(swM.getLatLng(), neM.getLatLng(), rotation, m);
                updateFromBounds(newBounds, neM);
            } else {
                var s = swM.getLatLng(), n = neM.getLatLng();
                nwM.setLatLng(L.latLng(n.lat, s.lng));
                seM.setLatLng(L.latLng(s.lat, n.lng));
                if (targetOverlay) targetOverlay.setBounds([s, n]);
            }
        });
        nwM.on('drag', function () {
            if (rotation) {
                var newBounds = self._boundsFromRotatedDiagonal(nwM.getLatLng(), seM.getLatLng(), rotation, m);
                updateFromBounds(newBounds, nwM);
            } else {
                var nw = nwM.getLatLng(), se = seM.getLatLng();
                swM.setLatLng(L.latLng(se.lat, nw.lng));
                neM.setLatLng(L.latLng(nw.lat, se.lng));
                if (targetOverlay) targetOverlay.setBounds([L.latLng(se.lat, nw.lng), L.latLng(nw.lat, se.lng)]);
            }
        });
        seM.on('drag', function () {
            if (rotation) {
                var newBounds = self._boundsFromRotatedDiagonal(nwM.getLatLng(), seM.getLatLng(), rotation, m);
                updateFromBounds(newBounds, seM);
            } else {
                var nw = nwM.getLatLng(), se = seM.getLatLng();
                swM.setLatLng(L.latLng(se.lat, nw.lng));
                neM.setLatLng(L.latLng(nw.lat, se.lng));
                if (targetOverlay) targetOverlay.setBounds([L.latLng(se.lat, nw.lng), L.latLng(nw.lat, se.lng)]);
            }
        });

        function save() {
            // Always save axis-aligned bounds (from the overlay, not from handle positions)
            var b = targetOverlay ? targetOverlay.getBounds() : axisBounds;
            var s = b.getSouthWest(), n = b.getNorthEast();
            if (imageId) {
                self._dotNetRef.invokeMethodAsync('OnImageBoundsChangedFromJs', imageId, s.lat, s.lng, n.lat, n.lng);
            } else {
                self._dotNetRef.invokeMethodAsync('OnBoundsChangedFromJs', s.lat, s.lng, n.lat, n.lng);
            }
        }
        swM.on('dragend', save);
        neM.on('dragend', save);
        nwM.on('dragend', save);
        seM.on('dragend', save);

        // ── Drag overlay body to move ──
        if (targetOverlay) {
            self._positionDragState = null;

            function startDrag(px) {
                self._positionDragState = {
                    startPx: px,
                    startBounds: targetOverlay.getBounds(),
                    startSw: swM.getLatLng(), startNe: neM.getLatLng(),
                    startNw: nwM.getLatLng(), startSe: seM.getLatLng()
                };
                m.dragging.disable();
            }

            function moveDrag(curPx) {
                if (!self._positionDragState) return;
                var ds = self._positionDragState;
                var startLL = m.containerPointToLatLng(ds.startPx);
                var curLL = m.containerPointToLatLng(curPx);
                var dLat = curLL.lat - startLL.lat;
                var dLng = curLL.lng - startLL.lng;

                // Shift axis-aligned bounds
                var bSw = ds.startBounds.getSouthWest();
                var bNe = ds.startBounds.getNorthEast();
                targetOverlay.setBounds([
                    L.latLng(bSw.lat + dLat, bSw.lng + dLng),
                    L.latLng(bNe.lat + dLat, bNe.lng + dLng)
                ]);

                // Shift all handles (they're at rotated positions, shift by same delta)
                swM.setLatLng(L.latLng(ds.startSw.lat + dLat, ds.startSw.lng + dLng));
                neM.setLatLng(L.latLng(ds.startNe.lat + dLat, ds.startNe.lng + dLng));
                nwM.setLatLng(L.latLng(ds.startNw.lat + dLat, ds.startNw.lng + dLng));
                seM.setLatLng(L.latLng(ds.startSe.lat + dLat, ds.startSe.lng + dLng));
            }

            function endDrag() {
                if (!self._positionDragState) return;
                self._positionDragState = null;
                m.dragging.enable();
                save();
            }

            self._positionMouseDown = function (e) {
                if (e.button !== 0) return;
                e.stopPropagation();
                e.preventDefault();
                startDrag(m.mouseEventToContainerPoint(e));
            };
            self._positionMouseMove = function (e) {
                moveDrag(m.mouseEventToContainerPoint(e));
            };
            self._positionMouseUp = function () { endDrag(); };

            self._positionTouchStart = function (e) {
                if (e.touches.length !== 1) return;
                e.stopPropagation();
                e.preventDefault();
                var touch = e.touches[0];
                var rect = m.getContainer().getBoundingClientRect();
                startDrag(L.point(touch.clientX - rect.left, touch.clientY - rect.top));
            };
            self._positionTouchMove = function (e) {
                if (!self._positionDragState || e.touches.length !== 1) return;
                e.preventDefault();
                var touch = e.touches[0];
                var rect = m.getContainer().getBoundingClientRect();
                moveDrag(L.point(touch.clientX - rect.left, touch.clientY - rect.top));
            };
            self._positionTouchEnd = function () { endDrag(); };

            document.addEventListener('mousemove', self._positionMouseMove);
            document.addEventListener('mouseup', self._positionMouseUp);
            document.addEventListener('touchmove', self._positionTouchMove, { passive: false });
            document.addEventListener('touchend', self._positionTouchEnd);

            // Re-enable pointer events on the target overlay and attach drag handlers
            function attachDragToOverlay() {
                var el = targetOverlay.getElement();
                if (el) {
                    el.style.pointerEvents = 'auto';
                    el.style.cursor = 'move';
                    el.addEventListener('mousedown', self._positionMouseDown);
                    el.addEventListener('touchstart', self._positionTouchStart, { passive: false });
                } else {
                    targetOverlay.once('load', function () {
                        var el2 = targetOverlay.getElement();
                        if (el2) {
                            el2.style.pointerEvents = 'auto';
                            el2.style.cursor = 'move';
                            el2.addEventListener('mousedown', self._positionMouseDown);
                            el2.addEventListener('touchstart', self._positionTouchStart, { passive: false });
                        }
                    });
                }
            }
            attachDragToOverlay();
        }
    },

    exitPositionMode: function () {
        var m = this._map;
        if (!m) return;
        if (this._corners) {
            this._corners.forEach(function (c) { m.removeLayer(c); });
            this._corners = null;
        }
        // Clean up drag-to-move listeners and reset cursor
        if (this._positionMouseDown) {
            this._overlays.forEach(function (o) {
                var el = o.overlay.getElement();
                if (el) {
                    el.removeEventListener('mousedown', this._positionMouseDown);
                    el.removeEventListener('touchstart', this._positionTouchStart);
                    el.style.cursor = '';
                }
            }.bind(this));
        }
        if (this._positionMouseMove) {
            document.removeEventListener('mousemove', this._positionMouseMove);
            this._positionMouseMove = null;
        }
        if (this._positionMouseUp) {
            document.removeEventListener('mouseup', this._positionMouseUp);
            this._positionMouseUp = null;
        }
        if (this._positionTouchMove) {
            document.removeEventListener('touchmove', this._positionTouchMove);
            this._positionTouchMove = null;
        }
        if (this._positionTouchEnd) {
            document.removeEventListener('touchend', this._positionTouchEnd);
            this._positionTouchEnd = null;
        }
        this._positionDragState = null;
        this._positionMouseDown = null;
        this._positionTouchStart = null;
        this._positionUpdateFn = null;
    },

    // ── Building Move Mode ──────────────────────────────────────────

    enterMoveMode: function (centerLat, centerLng) {
        var m = this._map;
        if (!m) return;
        var self = this;

        this.exitMoveMode();
        m.dragging.disable();
        this._startEdgePan();

        var ci = L.divIcon({ className: 'fp-move-handle', html: '\u2725', iconSize: [28, 28], iconAnchor: [14, 14] });
        this._moveMarker = L.marker([centerLat, centerLng], { icon: ci, draggable: true }).addTo(m);
        this._moveStartCenter = L.latLng(centerLat, centerLng);

        // Live preview: move overlays and walls during drag + edge pan
        this._moveMarker.on('drag', function (e) {
            if (!self._moveStartCenter) return;
            var pos = e.target.getLatLng();
            var dLat = pos.lat - self._moveStartCenter.lat;
            var dLng = pos.lng - self._moveStartCenter.lng;

            // Shift legacy single overlay
            if (self._overlay) {
                var b = self._overlay.getBounds();
                var sw = b.getSouthWest();
                var ne = b.getNorthEast();
                self._overlay.setBounds([[sw.lat + dLat, sw.lng + dLng], [ne.lat + dLat, ne.lng + dLng]]);
            }

            // Shift all multi-image overlays
            self._overlays.forEach(function (o) {
                var ob = o.overlay.getBounds();
                var osw = ob.getSouthWest();
                var one = ob.getNorthEast();
                o.overlay.setBounds([[osw.lat + dLat, osw.lng + dLng], [one.lat + dLat, one.lng + dLng]]);
            });

            self._moveStartCenter = pos;

            // Edge pan during marker drag
            var container = m.getContainer();
            var rect = container.getBoundingClientRect();
            var markerPx = m.latLngToContainerPoint(pos);
            var edgeZone = 40, panSpeed = 4;
            var dx = 0, dy = 0;
            if (markerPx.x < edgeZone) dx = -panSpeed * (1 - markerPx.x / edgeZone);
            else if (markerPx.x > rect.width - edgeZone) dx = panSpeed * (1 - (rect.width - markerPx.x) / edgeZone);
            if (markerPx.y < edgeZone) dy = -panSpeed * (1 - markerPx.y / edgeZone);
            else if (markerPx.y > rect.height - edgeZone) dy = panSpeed * (1 - (rect.height - markerPx.y) / edgeZone);
            if (dx !== 0 || dy !== 0) m.panBy([dx, dy], { animate: false });
        });

        this._moveMarker.on('dragend', function (e) {
            var pos = e.target.getLatLng();
            self._dotNetRef.invokeMethodAsync('OnBuildingMovedFromJs', pos.lat, pos.lng);
        });
    },

    exitMoveMode: function () {
        this._stopEdgePan();
        if (this._moveMarker && this._map) {
            this._map.removeLayer(this._moveMarker);
            this._moveMarker = null;
        }
        if (this._map) this._map.dragging.enable();
    },

    // ── Heatmap ──────────────────────────────────────────────────────

    // The per-band ramp, pushed from the server so points, the heat surface and the cluster dots
    // all read on the curve SignalClassification owns. Seeded with the 5 GHz one for the first
    // paint; three separate tables here is how they drifted apart before.
    _signalStops: [
        { s: -60, hex: '#10b981' }, { s: -70, hex: '#22c55e' }, { s: -78, hex: '#eab308' },
        { s: -85, hex: '#f97316' }, { s: -95, hex: '#ef4444' }
    ],

    setSignalStops: function (stops) { if (stops && stops.length) this._signalStops = stops; },

    _signalRamp: function (dbm) {
        var st = this._signalStops;
        var rgb = function (hex) {
            return { r: parseInt(hex.substr(1, 2), 16), g: parseInt(hex.substr(3, 2), 16), b: parseInt(hex.substr(5, 2), 16) };
        };
        if (dbm >= st[0].s) return rgb(st[0].hex);
        if (dbm <= st[st.length - 1].s) return rgb(st[st.length - 1].hex);
        for (var j = 0; j < st.length - 1; j++) {
            if (dbm <= st[j].s && dbm >= st[j + 1].s) {
                var t = (dbm - st[j + 1].s) / (st[j].s - st[j + 1].s);
                var a = rgb(st[j].hex), b = rgb(st[j + 1].hex);
                return {
                    r: Math.round(a.r * t + b.r * (1 - t)),
                    g: Math.round(a.g * t + b.g * (1 - t)),
                    b: Math.round(a.b * t + b.b * (1 - t))
                };
            }
        }
        return rgb(st[st.length - 1].hex);
    },

    computeHeatmap: function (baseUrl, activeFloor, band, excludePlannedAps, signalMeasurements) {
        var m = this._map;
        if (!m) return;
        var self = this;
        var requestId = ++this._heatmapRequestId;

        // Abort any in-flight heatmap fetch
        if (this._heatmapAbort) this._heatmapAbort.abort();
        this._heatmapAbort = new AbortController();

        // Store params so JS-initiated recomputes (sim toggles, sliders) work without arguments
        if (baseUrl) this._heatmapBaseUrl = baseUrl;
        if (activeFloor != null) this._heatmapFloor = activeFloor;
        if (band) this._heatmapBand = band;
        if (excludePlannedAps != null) this._excludePlannedAps = excludePlannedAps;
        if (signalMeasurements !== undefined) this._signalMeasurements = signalMeasurements;
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
        var res = maxDim > 600 ? Math.ceil(maxDim / 600) : 1.0;

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
        // Antenna mode overrides are keyed by MAC only (all-bands physical switch)
        if (Object.keys(self._antennaModeOverrides).length > 0) {
            body.antennaModeOverrides = self._antennaModeOverrides;
        }
        // Disabled APs to exclude from heatmap
        var disabledList = Object.keys(self._disabledAps);
        if (!self._excludePlannedAps) {
            Object.keys(self._disabledForPlanAps).forEach(function (mac) {
                if (disabledList.indexOf(mac) === -1) disabledList.push(mac);
            });
        }
        if (disabledList.length > 0) {
            body.disabledMacs = disabledList;
        }
        if (self._excludePlannedAps) {
            body.excludePlannedAps = true;
        }
        // Include signal measurements for IDW adjustment when available
        if (self._signalMeasurements && self._signalMeasurements.length > 0) {
            body.signalMeasurements = self._signalMeasurements;
        }

        fetch(baseUrl + '/api/floor-plan/heatmap', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
            signal: self._heatmapAbort.signal
        })
        .then(function (r) { if (!r.ok) throw new Error('Heatmap request failed: ' + r.status); return r.json(); })
        .then(function (data) {
            if (!data || !data.data) return;
            if (requestId !== self._heatmapRequestId) return; // stale request, discard

            var canvas = document.createElement('canvas');
            canvas.width = data.width;
            canvas.height = data.height;
            var ctx = canvas.getContext('2d');
            var imgData = ctx.createImageData(data.width, data.height);

            // Smooth color gradient function
            // The heat surface reads the same per-band ramp as the points and the cluster dots.
            function lerpColor(sig) { return fpEditor._signalRamp(sig); }

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
            // Add new overlay first, then remove old - avoids a blank frame between swap
            if (self._heatmapOverlay) m.removeLayer(self._heatmapOverlay);
            self._heatmapOverlay = L.imageOverlay(dataUrl, bounds, {
                opacity: 0.6, pane: 'heatmapPane', interactive: false
            }).addTo(m);

            // Contour lines using marching squares
            if (self._contourLayer) m.removeLayer(self._contourLayer);
            self._contourLayer = L.layerGroup().addTo(m);

            // Where the lines are drawn is fixed; their color is the ramp's at that dBm, so a
            // contour sits in the shade it divides. The colors were hardcoded and band-blind:
            // -60 drew yellow over a surface painting it excellent green.
            var thresholds = [-45, -50, -55, -60, -65, -70, -75, -80, -85].map(function (db) {
                var c = fpEditor._signalRamp(db);
                return { db: db, color: 'rgb(' + c.r + ',' + c.g + ',' + c.b + ')', label: String(db) };
            });
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
                    L.polyline(s, { color: th.color, weight: 1, opacity: 0.4, interactive: false, pane: 'wallPane' })
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
        .catch(function (err) { if (err.name !== 'AbortError') console.error('Heatmap error:', err); });
    },

    clearHeatmap: function () {
        this._heatmapBaseUrl = null; // stop moveend from recomputing
        this._signalMeasurements = null;
        if (this._heatmapAbort) this._heatmapAbort.abort(); // cancel in-flight fetch
        this._heatmapRequestId++; // invalidate any in-flight compute
        if (this._heatmapOverlay && this._map) {
            this._map.removeLayer(this._heatmapOverlay);
            this._heatmapOverlay = null;
        }
        if (this._contourLayer && this._map) {
            this._map.removeLayer(this._contourLayer);
            this._contourLayer = null;
        }
    },

    // ── Signal Data Overlay ────────────────────────────────────────

    // Cluster dots read the same ramp as the points they stand for.
    _signalColor: function (dbm) {
        var c = this._signalRamp(dbm);
        return 'rgb(' + c.r + ',' + c.g + ',' + c.b + ')';
    },

    updateSignalData: function (markersJson) {
        if (!this._map || !this._signalClusterGroup) return;
        var self = this;

        // Save state before clearing (open popup, spiderfied cluster)
        var openPopupKey = null;
        var spiderfiedKeys = [];

        this._signalClusterGroup.eachLayer(function (layer) {
            if (layer.isPopupOpen && layer.isPopupOpen()) {
                openPopupKey = layer.options.markerKey;
            }
        });

        if (this._signalCurrentSpider) {
            var childMarkers = this._signalCurrentSpider.getAllChildMarkers();
            childMarkers.forEach(function (m) {
                spiderfiedKeys.push(m.options.markerKey);
                if (m.isPopupOpen && m.isPopupOpen()) {
                    openPopupKey = m.options.markerKey;
                }
            });
        }

        this._signalClusterGroup.clearLayers();
        this._signalCurrentSpider = null;

        var markers = JSON.parse(markersJson);
        var markerMap = {};
        var markerToReopen = null;

        markers.forEach(function (m) {
            var marker = L.circleMarker([m.lat, m.lng], {
                radius: 8,
                fillColor: m.color,
                color: '#fff',
                weight: 2,
                opacity: 1,
                fillOpacity: 0.8,
                signalDbm: m.signalDbm,
                markerKey: m.key
            });
            if (m.popup) marker.bindPopup(m.popup);
            self._signalClusterGroup.addLayer(marker);
            if (m.key) markerMap[m.key] = marker;

            if (openPopupKey && m.key === openPopupKey) {
                markerToReopen = marker;
            }
        });

        // Restore spider and/or popup
        if (spiderfiedKeys.length > 0) {
            setTimeout(function () {
                var clusterToSpiderfy = null;
                for (var i = 0; i < spiderfiedKeys.length; i++) {
                    var marker = markerMap[spiderfiedKeys[i]];
                    if (marker) {
                        var parent = self._signalClusterGroup.getVisibleParent(marker);
                        if (parent && parent !== marker && parent.spiderfy) {
                            clusterToSpiderfy = parent;
                            break;
                        }
                    }
                }

                if (clusterToSpiderfy) {
                    clusterToSpiderfy.spiderfy();
                    if (openPopupKey) {
                        setTimeout(function () {
                            var m = markerMap[openPopupKey];
                            if (m) m.openPopup();
                        }, 100);
                    }
                } else if (markerToReopen) {
                    markerToReopen.openPopup();
                }
            }, 50);
        } else if (markerToReopen) {
            markerToReopen.openPopup();
        }
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
        var m = this._map;
        if (m) {
            this._overlays.forEach(function (o) { m.removeLayer(o.overlay); });
        }
        this._overlays = [];
        this._selectedOverlayId = null;
        if (this._heatmapOverlay && this._map) { this._map.removeLayer(this._heatmapOverlay); this._heatmapOverlay = null; }
        if (this._contourLayer && this._map) { this._map.removeLayer(this._contourLayer); this._contourLayer = null; }
        if (this._signalClusterGroup) this._signalClusterGroup.clearLayers();
        if (this._bgWallLayer) this._bgWallLayer.clearLayers();
        if (this._wallLayer) this._wallLayer.clearLayers();
        this._multiSel = null;
        this._updateWallToolbox();
    },

    destroy: function () {
        if (this._beforeUnload) {
            window.removeEventListener('beforeunload', this._beforeUnload);
            this._beforeUnload = null;
        }
        this._removeEscapeHandler();
        this._stopEdgePan();
        this.exitPositionMode();
        if (this._savedViewZoomHandler && this._map) {
            this._map.off('zoomend', this._savedViewZoomHandler);
            this._savedViewZoomHandler = null;
        }
        this._savedView = null;
        SteppedScaleBar.remove(this._scaleBar); this._scaleBar = null;
        if (this._map) { this._map.remove(); this._map = null; }
        this._dotNetRef = null;
        this._overlay = null;
        this._overlays = [];
        this._selectedOverlayId = null;
        this._apLayer = null;
        this._apGlowLayer = null;
        this._bgWallLayer = null;
        this._bgHitAreaLayer = null;
        this._wallLayer = null;
        this._wallHighlightLayer = null;
        this._heatmapOverlay = null;
        this._contourLayer = null;
        this._signalClusterGroup = null;
        // Bound to the removed map: the next initMap binds them again
        this._marqueeBound = false;
        this._wallMapClickBound = false;
        this._wallToolbox = null;
        this._multiSel = null;
        this._apMarkers = {};
        this._apPopupTarget = null;
        this._activeMoveHandlers = null;
    }
};
