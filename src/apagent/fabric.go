package main

import "strings"

// UniFi carries a wireless uplink on its own VAP, and the peer access point on the other end shows
// up in that VAP's station list exactly like a client does. It is fabric, not a client: reporting
// it puts an access point's own interface on the map under a randomized MAC nothing can name, and
// writes it into wifi_client, where it outlives any filtering the server does because historic
// playback reads the series back as truth.
//
// Naming, verified on U7 hardware (fw 8.7.11): the uplink VAPs are "vwireap<N>" on the parent and
// "vwiresta<N>" on the child, with ".staN" suffixed variants for the station side. Nothing else on
// the AP uses the vwire prefix.
//
// Never relax this to "skip MACs that look randomized". A phone using a private Wi-Fi address is a
// real client with a randomized MAC, and an MLO client associates under one per link.
func isFabricVap(vap string) bool {
	name := strings.ToLower(strings.TrimSpace(vap))
	if name == "" {
		return false
	}
	if strings.HasPrefix(name, "vwire") {
		return true
	}
	// The station side of an uplink, e.g. "vwireap10.sta1", already matches above; this catches a
	// bare ".staN" on a differently named uplink VAP.
	if i := strings.LastIndex(name, ".sta"); i > 0 {
		return true
	}
	return false
}
