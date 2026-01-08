/**
 * Geolocation support for OpenSpeedTest
 * Continuously tracks location for accurate position at result submission
 */

var geoLocation = {
    latitude: null,
    longitude: null,
    accuracy: null,
    watchId: null,
    error: null
};

/**
 * Start watching location with high accuracy
 * Updates continuously so we always have the freshest position
 */
function startLocationWatch() {
    if (!navigator.geolocation) {
        console.log("Geolocation not supported");
        return;
    }

    // First, get a quick low-accuracy fix to trigger permission prompt
    navigator.geolocation.getCurrentPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            console.log("Initial location fix:", geoLocation.latitude, geoLocation.longitude, "accuracy:", geoLocation.accuracy);

            // Now start watching with high accuracy
            startHighAccuracyWatch();
        },
        function(error) {
            geoLocation.error = error.message;
            console.log("Location denied or unavailable:", error.message);
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
 * This runs continuously to keep location fresh
 */
function startHighAccuracyWatch() {
    if (geoLocation.watchId !== null) {
        return; // Already watching
    }

    geoLocation.watchId = navigator.geolocation.watchPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            console.log("Location updated:", geoLocation.latitude, geoLocation.longitude, "accuracy:", geoLocation.accuracy);
        },
        function(error) {
            console.log("Location watch error:", error.message);
        },
        {
            enableHighAccuracy: true,
            timeout: 10000,
            maximumAge: 0  // Always get fresh reading
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
 * Simple synchronous approach - appends current location to URL
 */
(function() {
    var originalOpen = XMLHttpRequest.prototype.open;

    XMLHttpRequest.prototype.open = function(method, url) {
        // Check if this is a request to save speed test results
        if (typeof url === 'string' && url.indexOf('/api/public/speedtest/results') !== -1) {
            // Append location params if available
            var locationParams = getLocationParams();
            if (locationParams) {
                // URL already ends with ? so just append params
                url = url + locationParams;
                console.log("Appended location to speed test result URL:", url);
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
