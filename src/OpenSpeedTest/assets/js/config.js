/**
 * OpenSpeedTest Configuration
 * Values are injected at container startup by docker-entrypoint.sh
 * Placeholders are replaced with actual values via sed
 */

// These will be replaced by the entrypoint script
// __SAVE_DATA__ becomes true/false
// __SAVE_DATA_URL__ becomes the actual URL
var saveData = __SAVE_DATA__;
var saveDataURL = "__SAVE_DATA_URL__";

// Fix for missing variable bug in OpenSpeedTest
var OpenSpeedTestdb = "";
