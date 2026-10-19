/**
 * Geolocation support for OpenSpeedTest
 * Continuously tracks location for accurate position at result submission
 */

var geoLocation = {
    latitude: null,
    longitude: null,
    accuracy: null,
    watchId: null
};

/**
 * Plain-HTTP handoff. Geolocation needs a secure context, but TLS roughly halves throughput
 * on modest hosts (no sendfile on the download leg). So when config.js names a plain HTTP
 * target, the HTTPS page takes one fix and hands it to the same page over HTTP in the URL
 * fragment, which the result POST below then carries.
 *   plainHttpUrl  - full http:// origin (the server, whose plain host can differ)
 *   plainHttpPort - port on this same host (agents)
 */
function plainHttpHandoffUrl() {
    if (window.location.protocol !== "https:") {
        return null;
    }
    var origin = null;
    if (typeof plainHttpUrl === "string" && /^http:\/\/[^\/]+/.test(plainHttpUrl)) {
        origin = plainHttpUrl.replace(/\/+$/, "");
    } else if (typeof plainHttpPort === "number" && plainHttpPort > 0) {
        origin = "http://" + window.location.hostname + ":" + plainHttpPort;
    }
    return origin ? origin + window.location.pathname + window.location.search : null;
}

// Blocks the HTTPS page while the handoff waits on a permission prompt: a test started here
// would be cut off by the redirect once the user answers.
function showHandoffOverlay(message, onSkip) {
    function build() {
        var overlay = document.createElement("div");
        overlay.id = "netopt-handoff";
        overlay.style.cssText = "position:fixed;inset:0;z-index:2147483647;display:flex;" +
            "align-items:center;justify-content:center;background:rgba(0,0,0,0.6);" +
            "font-family:sans-serif;";
        var card = document.createElement("div");
        card.style.cssText = "max-width:320px;margin:16px;padding:20px;border-radius:8px;" +
            "background:#161618;color:#ededef;text-align:center;line-height:1.4;";
        var text = document.createElement("p");
        text.style.cssText = "margin:0 0 16px;";
        text.textContent = message;
        var skip = document.createElement("button");
        skip.type = "button";
        skip.textContent = "Skip";
        skip.style.cssText = "padding:8px 20px;border:0;border-radius:6px;background:#0550B5;" +
            "color:#fff;font-size:15px;cursor:pointer;";
        skip.addEventListener("click", onSkip);
        card.appendChild(text);
        card.appendChild(skip);
        overlay.appendChild(card);
        document.body.appendChild(overlay);
    }
    if (document.body) {
        build();
    } else {
        document.addEventListener("DOMContentLoaded", build);
    }
}

function handOffToPlainHttp(url) {
    var done = false;
    function go(position) {
        if (done) return;
        done = true;
        var fragment = position
            ? "#lat=" + position.coords.latitude.toFixed(6) +
              "&lng=" + position.coords.longitude.toFixed(6) +
              "&acc=" + Math.round(position.coords.accuracy)
            : "";
        window.location.replace(url + fragment);
    }
    function locate() {
        navigator.geolocation.getCurrentPosition(go, function() { go(null); },
            { enableHighAccuracy: true, timeout: 5000, maximumAge: 60000 });
    }
    if (!navigator.geolocation || !window.isSecureContext) {
        go(null);
        return;
    }
    // The query (including the app's ?R auto-start) moves to the HTTP page only; left here,
    // the app would start a test on this page before the redirect.
    if (window.history && window.history.replaceState) {
        window.history.replaceState(null, "", window.location.pathname);
    }
    var ASK = "Allow location access to tag this result with where you are, or skip.";
    var LOCATING = "Getting your location...";
    function wait(message) {
        showHandoffOverlay(message, function() { go(null); });
        locate();
    }
    // A pending prompt holds the geolocation timeout, and Firefox's "Not now" never calls back.
    setTimeout(function() { go(null); }, 15000);
    if (navigator.permissions && navigator.permissions.query) {
        navigator.permissions.query({ name: "geolocation" }).then(function(status) {
            if (status.state === "denied") {
                go(null);
            } else {
                wait(status.state === "granted" ? LOCATING : ASK);
            }
        }, function() { wait(ASK); });
    } else {
        wait(ASK);
    }
}

// Runs before the app script parses the URL, so the fragment never reaches its parameters.
(function() {
    var m = /^#lat=(-?\d+(?:\.\d+)?)&lng=(-?\d+(?:\.\d+)?)&acc=(\d+)$/.exec(window.location.hash);
    if (!m) return;
    geoLocation.latitude = parseFloat(m[1]);
    geoLocation.longitude = parseFloat(m[2]);
    geoLocation.accuracy = parseInt(m[3], 10);
    if (window.history && window.history.replaceState) {
        window.history.replaceState(null, "", window.location.pathname + window.location.search);
    }
})();

var plainHttpTarget = plainHttpHandoffUrl();
if (plainHttpTarget) {
    handOffToPlainHttp(plainHttpTarget);
}

/**
 * Start watching location with high accuracy
 */
function startLocationWatch() {
    // Geolocation requires HTTPS (secure context)
    if (!navigator.geolocation || !window.isSecureContext) {
        return;
    }

    // First, get a quick low-accuracy fix to trigger permission prompt
    navigator.geolocation.getCurrentPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            startHighAccuracyWatch();
        },
        function(error) {
            // Still try to start watch even if initial request fails
            startHighAccuracyWatch();
        },
        {
            enableHighAccuracy: false,
            timeout: 5000,
            maximumAge: 300000
        }
    );
}

/**
 * Start high-accuracy location watch
 */
function startHighAccuracyWatch() {
    if (geoLocation.watchId !== null) {
        return;
    }

    geoLocation.watchId = navigator.geolocation.watchPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
        },
        function(error) {},
        {
            enableHighAccuracy: true,
            timeout: 10000,
            maximumAge: 0
        }
    );
}

/**
 * Get location as form-encoded string to append to POST body
 */
function getLocationFormData() {
    if (geoLocation.latitude === null || geoLocation.longitude === null) {
        return "";
    }
    return "&lat=" + geoLocation.latitude.toFixed(6) +
           "&lng=" + geoLocation.longitude.toFixed(6) +
           "&acc=" + Math.round(geoLocation.accuracy);
}

/**
 * Intercept XMLHttpRequest to append location to POST body
 */
(function() {
    var originalOpen = XMLHttpRequest.prototype.open;
    var originalSend = XMLHttpRequest.prototype.send;

    XMLHttpRequest.prototype.open = function(method, url) {
        this._isSpeedTestResult = (typeof url === 'string' &&
            url.indexOf('/api/public/speedtest/results') !== -1);
        return originalOpen.apply(this, arguments);
    };

    XMLHttpRequest.prototype.send = function(body) {
        var xhr = this;
        var args = arguments;

        if (this._isSpeedTestResult && body) {
            // If we already have location, send immediately
            var locationData = getLocationFormData();
            if (locationData) {
                body = body + locationData;
                return originalSend.call(xhr, body);
            }

            // No location yet - try one quick getCurrentPosition before sending
            // Geolocation requires HTTPS - wrap in try-catch to handle security errors
            if (navigator.geolocation && window.isSecureContext) {
                try {
                    var sent = false;
                    var sendOnce = function(finalBody) {
                        if (sent) return;
                        sent = true;
                        originalSend.call(xhr, finalBody);
                    };

                    // Safety timeout - if permission prompt is pending and user
                    // hasn't responded, the geolocation timeout doesn't start
                    // counting until they do. Send without location after 3s.
                    setTimeout(function() { sendOnce(body); }, 3000);

                    navigator.geolocation.getCurrentPosition(
                        function(position) {
                            geoLocation.latitude = position.coords.latitude;
                            geoLocation.longitude = position.coords.longitude;
                            geoLocation.accuracy = position.coords.accuracy;
                            var locData = getLocationFormData();
                            sendOnce(locData ? body + locData : body);
                        },
                        function(error) {
                            // Failed - send without location
                            sendOnce(body);
                        },
                        { enableHighAccuracy: true, timeout: 2000, maximumAge: 60000 }
                    );
                    return; // Don't call send yet - callback or timeout will do it
                } catch (e) {
                    // Security error or other exception - continue without location
                }
            }
        }
        return originalSend.call(xhr, body);
    };
})();

// Start location tracking when page loads (not while handing off: the HTTP page gets the fix)
if (!plainHttpTarget) {
    if (document.readyState === 'complete') {
        startLocationWatch();
    } else {
        window.addEventListener('load', startLocationWatch);
    }
}
