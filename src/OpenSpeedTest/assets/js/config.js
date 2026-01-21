/**
 * OpenSpeedTest Configuration
 * Values are injected at container startup by docker-entrypoint.sh
 * Placeholders are replaced with actual values via sed
 */

// These will be replaced by the entrypoint script
// __SAVE_DATA__ becomes true/false
// __SAVE_DATA_URL__ becomes the actual URL or __DYNAMIC__
// __API_PATH__ becomes the API endpoint path
var saveData = __SAVE_DATA__;
var saveDataURL = "__SAVE_DATA_URL__";
var apiPath = "__API_PATH__";

// If __DYNAMIC__, construct URL from browser location (same host, port 8042)
if (saveDataURL === "__DYNAMIC__") {
    saveDataURL = window.location.protocol + "//" + window.location.hostname + ":8042" + apiPath;
}

// URL for viewing client speed test results (derived from saveDataURL)
// Extract base URL by removing the API path
var clientResultsUrl = saveDataURL.replace(apiPath, "") + "/client-speedtest";

// Fix for missing variable bug in OpenSpeedTest
var OpenSpeedTestdb = "";
