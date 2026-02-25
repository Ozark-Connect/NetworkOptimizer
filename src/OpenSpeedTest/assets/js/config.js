/**
 * OpenSpeedTest Configuration
 * Values are injected at container startup by docker-entrypoint.sh
 * Placeholders are replaced with actual values via sed
 */

// These will be replaced by the entrypoint script
// __SAVE_DATA__ becomes true/false
// __SAVE_DATA_URL__ becomes the actual URL or __DYNAMIC__
// __API_PATH__ becomes the API endpoint path
// __HTTPS_REDIRECT_URL__ becomes the HTTPS URL for mobile redirect (or empty)
var saveData = __SAVE_DATA__;
var saveDataURL = "__SAVE_DATA_URL__";
var apiPath = "__API_PATH__";
var httpsRedirectUrl = "__HTTPS_REDIRECT_URL__";

// Mobile HTTPS redirect: when on HTTP and mobile browser, redirect to HTTPS for geolocation
if (httpsRedirectUrl && httpsRedirectUrl !== "__HTTPS_REDIRECT_URL__" && httpsRedirectUrl !== "") {
    if (window.location.protocol === "http:" && /Mobile|Android|iPhone|iPad|iPod/i.test(navigator.userAgent)) {
        window.location.replace(httpsRedirectUrl + window.location.pathname + window.location.search);
    }
}

// If __DYNAMIC__, construct URL from browser location (same host, port 8042)
if (saveDataURL === "__DYNAMIC__") {
    saveDataURL = window.location.protocol + "//" + window.location.hostname + ":8042" + apiPath;
}

// URL for viewing client speed test results (derived from saveDataURL)
// Extract base URL by splitting on /api
var clientResultsUrl = saveDataURL.split("/api")[0] + "/client-speedtest";

// Fix for missing variable bug in OpenSpeedTest
var OpenSpeedTestdb = "";
