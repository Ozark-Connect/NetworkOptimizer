/**
 * Geolocation support for OpenSpeedTest
 * Captures location when test starts and appends to result URL
 */

var geoLocation = {
    latitude: null,
    longitude: null,
    accuracy: null,
    requested: false,
    error: null
};

/**
 * Request geolocation permission and capture position
 * Called when the speed test starts
 */
function requestLocationForSpeedTest() {
    if (!navigator.geolocation) {
        console.log("Geolocation not supported");
        return;
    }

    if (geoLocation.requested) {
        return; // Already requested
    }

    geoLocation.requested = true;

    navigator.geolocation.getCurrentPosition(
        function(position) {
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            console.log("Location captured:", geoLocation.latitude, geoLocation.longitude);
        },
        function(error) {
            geoLocation.error = error.message;
            console.log("Location denied or unavailable:", error.message);
        },
        {
            enableHighAccuracy: false,
            timeout: 5000,
            maximumAge: 300000 // Cache for 5 minutes
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
    return "&lat=" + geoLocation.latitude.toFixed(6) +
           "&lng=" + geoLocation.longitude.toFixed(6) +
           "&acc=" + Math.round(geoLocation.accuracy);
}

/**
 * Intercept XMLHttpRequest to append location params to speed test results
 * This hooks into the existing OpenSpeedTest save mechanism
 */
(function() {
    var originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function(method, url) {
        // Check if this is a request to save speed test results
        if (typeof url === 'string' && url.indexOf('/api/public/speedtest/results') !== -1) {
            // Append location params if available
            var locationParams = getLocationParams();
            if (locationParams) {
                url = url + locationParams;
                console.log("Appended location to speed test result URL");
            }
        }
        return originalOpen.apply(this, arguments);
    };
})();

// Request location when page loads (silent browser permission popup)
if (document.readyState === 'complete') {
    requestLocationForSpeedTest();
} else {
    window.addEventListener('load', requestLocationForSpeedTest);
}
