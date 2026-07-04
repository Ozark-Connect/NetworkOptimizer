// Agent OpenSpeedTest config. Results post same-origin (nginx proxies them to the
// agent's loopback relay, which forwards to the central server with the site slug
// and the real client IP). Fully client-side dynamic, so no per-deployment
// injection is needed - overrides the placeholder config.js from the OpenSpeedTest
// source tree.
var saveData = true;
var saveDataURL = window.location.protocol + "//" + window.location.host + "/api/public/speedtest/results";
var apiPath = "/api/public/speedtest/results";
var externalServerId = "";
var clientResultsUrl = window.location.protocol + "//" + window.location.host + "/client-speedtest";
var OpenSpeedTestdb = "";
