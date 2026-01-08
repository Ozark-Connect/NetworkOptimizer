/**
 * Geolocation support for OpenSpeedTest
 * Captures fresh location before submitting results
 */

var geoLocation = {
    latitude: null,
    longitude: null,
    accuracy: null,
    permissionGranted: false,
    error: null
};

/**
 * Request geolocation permission on page load (triggers browser prompt)
 */
function requestLocationPermission() {
    if (!navigator.geolocation) {
        console.log("Geolocation not supported");
        return;
    }

    // Initial request just to get permission - use low accuracy to be fast
    navigator.geolocation.getCurrentPosition(
        function(position) {
            geoLocation.permissionGranted = true;
            geoLocation.latitude = position.coords.latitude;
            geoLocation.longitude = position.coords.longitude;
            geoLocation.accuracy = position.coords.accuracy;
            console.log("Location permission granted, initial fix:", geoLocation.latitude, geoLocation.longitude);
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
 * Get fresh location - returns a Promise
 * Used right before submitting results for best accuracy
 */
function getFreshLocation() {
    return new Promise(function(resolve) {
        if (!navigator.geolocation) {
            resolve(false);
            return;
        }

        navigator.geolocation.getCurrentPosition(
            function(position) {
                geoLocation.latitude = position.coords.latitude;
                geoLocation.longitude = position.coords.longitude;
                geoLocation.accuracy = position.coords.accuracy;
                console.log("Fresh location captured:", geoLocation.latitude, geoLocation.longitude, "accuracy:", geoLocation.accuracy);
                resolve(true);
            },
            function(error) {
                console.log("Fresh location failed:", error.message);
                resolve(false); // Still resolve - we'll use cached location if available
            },
            {
                enableHighAccuracy: true,  // Request best accuracy
                timeout: 3000,             // Wait up to 3 seconds
                maximumAge: 0              // Force fresh reading
            }
        );
    });
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
 * Intercept XMLHttpRequest to get fresh location before submitting speed test results
 */
(function() {
    var originalOpen = XMLHttpRequest.prototype.open;
    var originalSend = XMLHttpRequest.prototype.send;
    var pendingUrl = null;
    var pendingMethod = null;

    XMLHttpRequest.prototype.open = function(method, url) {
        // Store the URL for later modification in send()
        this._speedTestUrl = url;
        this._speedTestMethod = method;
        pendingUrl = url;
        pendingMethod = method;
        return originalOpen.apply(this, arguments);
    };

    XMLHttpRequest.prototype.send = function(body) {
        var xhr = this;
        var args = arguments;

        // Check if this is a speed test result submission
        if (typeof this._speedTestUrl === 'string' &&
            this._speedTestUrl.indexOf('/api/public/speedtest/results') !== -1) {

            console.log("Speed test result detected, getting fresh location...");

            // Get fresh location before sending
            getFreshLocation().then(function() {
                // Re-open with updated URL including location params
                var locationParams = getLocationParams();
                var newUrl = xhr._speedTestUrl;
                if (locationParams) {
                    newUrl = xhr._speedTestUrl + locationParams;
                    console.log("Appended fresh location to speed test result URL");
                }

                // Re-open the request with the updated URL
                originalOpen.call(xhr, xhr._speedTestMethod, newUrl);

                // Copy back any headers that were set (OpenSpeedTest doesn't set custom headers)
                // Then send
                originalSend.apply(xhr, args);
            });
        } else {
            // Not a speed test - send immediately
            return originalSend.apply(this, arguments);
        }
    };
})();

// Request location permission when page loads (triggers browser prompt early)
if (document.readyState === 'complete') {
    requestLocationPermission();
} else {
    window.addEventListener('load', requestLocationPermission);
}
