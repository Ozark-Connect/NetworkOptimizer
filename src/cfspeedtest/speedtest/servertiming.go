package speedtest

import (
	"net/http"
	"regexp"
	"strconv"
)

var serverTimingRe = regexp.MustCompile(`cfRequestDuration;dur=([\d.]+)`)

// parseServerTiming extracts the cfRequestDuration value (in ms) from the
// Server-Timing response header. Returns 0 if the header is missing or
// cannot be parsed.
func parseServerTiming(resp *http.Response) float64 {
	header := resp.Header.Get("Server-Timing")
	if header == "" {
		return 0
	}
	m := serverTimingRe.FindStringSubmatch(header)
	if len(m) < 2 {
		return 0
	}
	v, err := strconv.ParseFloat(m[1], 64)
	if err != nil {
		return 0
	}
	return v
}
