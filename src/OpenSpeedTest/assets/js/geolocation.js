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

// Start location tracking when page loads
if (document.readyState === 'complete') {
    startLocationWatch();
} else {
    window.addEventListener('load', startLocationWatch);
}
