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
 * Updates continuously so we always have the freshest position
 */
function startLocationWatch() {
    if (!navigator.geolocation) {
        return;
    }

    // First, get a quick low-accuracy fix to trigger permission prompt
    navigator.geolocation.getCurrentPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            // Now start watching with high accuracy
            startHighAccuracyWatch();
        },
        function(error) {},
        {
            enableHighAccuracy: false,
            timeout: 5000,
            maximumAge: 300000
        }
    );
}

/**
 * Start high-accuracy location watch
 * This runs continuously to keep location fresh
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
 * Get location parameters to append to save URL
 * Returns empty string if location not available
 */
function getLocationParams() {
    if (geoLocation.latitude === null || geoLocation.longitude === null) {
        return "";
    }
    return "lat=" + geoLocation.latitude.toFixed(6) +
           "&lng=" + geoLocation.longitude.toFixed(6) +
           "&acc=" + Math.round(geoLocation.accuracy);
}

/**
 * Intercept XMLHttpRequest to append location params to speed test results
 */
(function() {
    var originalOpen = XMLHttpRequest.prototype.open;

    XMLHttpRequest.prototype.open = function(method, url) {
        if (typeof url === 'string' && url.indexOf('/api/public/speedtest/results') !== -1) {
            var locationParams = getLocationParams();
            if (locationParams) {
                url = url + locationParams;
            }
        }
        return originalOpen.apply(this, [method, url].concat(Array.prototype.slice.call(arguments, 2)));
    };
})();

// Start location tracking when page loads
if (document.readyState === 'complete') {
    startLocationWatch();
} else {
    window.addEventListener('load', startLocationWatch);
}
