package main

import (
	"context"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// iwChannel is one interface's operating channel as `iw dev` prints it. CenterMHz is the block
// center, which is the only place the AP says which 320 MHz block a 6 GHz radio occupies: the
// primary is valid in one block of each of the two overlapping channelizations, and mca-dump
// reports only the primary.
type iwChannel struct {
	PrimaryMHz int
	WidthMHz   int
	CenterMHz  int
}

var (
	iwInterfaceRe = regexp.MustCompile(`^Interface\s+(\S+)`)
	iwChannelRe   = regexp.MustCompile(`^channel\s+\d+\s+\((\d+)\s+MHz\),\s+width:\s+(\d+)\s+MHz,\s+center1:\s+(\d+)\s+MHz`)
)

// parseIwDev reads `iw dev` into a map of interface name to its channel line. An interface with no
// channel line (mld-wifi0) is absent; every interface that carries one, VAP or radio netdev, is
// kept, and the caller decides which to attribute.
func parseIwDev(out string) map[string]iwChannel {
	found := map[string]iwChannel{}
	current := ""
	for _, raw := range strings.Split(out, "\n") {
		line := strings.TrimSpace(raw)
		if m := iwInterfaceRe.FindStringSubmatch(line); m != nil {
			current = m[1]
			continue
		}
		if current == "" {
			continue
		}
		m := iwChannelRe.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		primary, _ := strconv.Atoi(m[1])
		width, _ := strconv.Atoi(m[2])
		center, _ := strconv.Atoi(m[3])
		if primary == 0 || width == 0 || center == 0 {
			continue
		}
		found[current] = iwChannel{PrimaryMHz: primary, WidthMHz: width, CenterMHz: center}
	}
	return found
}

// collectRadioCenters runs one `iw dev` pass. A missing tool or an unreadable answer yields an
// empty map, never an error: the center is an enrichment and its absence must not fail the tier.
func collectRadioCenters(ctx context.Context) map[string]iwChannel {
	out, err := runCommand(ctx, 5*time.Second, "iw", "dev")
	if err != nil {
		return nil
	}
	return parseIwDev(out)
}

// centerForRadio resolves a radio's block center from the interface map: the radio's own netdev
// first, then any VAP that is up on it (every VAP on a radio prints the same line). Zero when
// nothing on the radio carries a channel line, and never for the scan radio, which hops.
func centerForRadio(radio RadioState, vaps []VapState, centers map[string]iwChannel) int {
	if radio.ScanRadio || len(centers) == 0 {
		return 0
	}
	if ch, ok := centers[radio.Name]; ok {
		return ch.CenterMHz
	}
	for _, v := range vaps {
		if v.RadioName != radio.Name || !v.Up {
			continue
		}
		if ch, ok := centers[v.Name]; ok {
			return ch.CenterMHz
		}
	}
	return 0
}
