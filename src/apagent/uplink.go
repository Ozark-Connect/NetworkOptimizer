package main

import (
	"context"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// LAN Health event types. The server's link sink keys on these strings, so they are a contract.
const (
	// EventLinkDown and EventLinkUp are the AP's own wired link changing, from the kernel log line
	// the PHY driver prints. UniFi Network records none of these, and a drop of a few seconds
	// leaves no trace anywhere else.
	EventLinkDown = "link_down"
	EventLinkUp   = "link_up"
	// EventUplinkCounters carries the wired uplink's carrier_changes and receive CRC count, sent only
	// when one of them moves.
	EventUplinkCounters = "uplink_counters"
	// EventUplinkReconfigure is a provision re-up of the uplink: it explains the link change beside it.
	EventUplinkReconfigure = "uplink_reconfigure"
	// EventUplinkLost is uplink-monitor reporting it cannot find the uplink.
	EventUplinkLost = "uplink_lost"
	// EventInformFailed is the AP failing to reach the UniFi Console while its LAN link stays up.
	EventInformFailed = "inform_failed"

	uplinkCountersEvery = 60 * time.Second
)

// UplinkCounters is one reading of a wired interface's link counters.
type UplinkCounters struct {
	CarrierChanges *int64 `json:"carrier_changes,omitempty"`
	RxCrcErrors    *int64 `json:"rx_crc_errors,omitempty"`
}

// Matches "eth0: PHY Link is down", "eth0: PHY Link up speed: 2500", "eth0: Link is Up - 1Gbps/Full".
var linkLineRe = regexp.MustCompile(`\b(eth\d+):\s+(?:PHY\s+)?[Ll]ink\s+(?:is\s+)?([Uu]p|[Dd]own)\b(.*)$`)

var speedMbpsRe = regexp.MustCompile(`(?i)speed:?\s*(\d+)`)
var speedUnitRe = regexp.MustCompile(`(?i)(\d+(?:\.\d+)?)\s*(Gbps|Mbps)`)

// parseLinkLine turns a PHY driver's link line into a link event. Iface carries the interface; the
// detail carries the speed in Mbps on a link up when the line states one.
func parseLinkLine(line string, now time.Time) (Event, bool) {
	if strings.Contains(line, "never came up") {
		return Event{}, false
	}
	m := linkLineRe.FindStringSubmatch(line)
	if m == nil {
		return Event{}, false
	}
	ev := Event{Iface: m[1], CollectedAt: now}
	if strings.EqualFold(m[2], "down") {
		ev.Type = EventLinkDown
		return ev, true
	}
	ev.Type = EventLinkUp
	rest := m[3]
	if s := speedMbpsRe.FindStringSubmatch(rest); s != nil {
		ev.Detail = s[1]
	} else if s := speedUnitRe.FindStringSubmatch(rest); s != nil {
		if v, err := strconv.ParseFloat(s[1], 64); err == nil {
			if strings.EqualFold(s[2], "gbps") {
				v *= 1000
			}
			ev.Detail = strconv.Itoa(int(v))
		}
	}
	return ev, true
}

// parseUplinkContextLine picks up the AP log lines that explain or qualify a link change.
func parseUplinkContextLine(line string, now time.Time) (Event, bool) {
	switch {
	case strings.Contains(line, "configure_uplink") && strings.Contains(line, " up"):
		return Event{Type: EventUplinkReconfigure, Detail: lastField(line), CollectedAt: now}, true
	case strings.Contains(line, "uplink-monitor") && strings.Contains(line, "cannot find uplink"):
		return Event{Type: EventUplinkLost, CollectedAt: now}, true
	case strings.Contains(line, "inform failed"):
		return Event{Type: EventInformFailed, CollectedAt: now}, true
	}
	return Event{}, false
}

func lastField(line string) string {
	f := strings.Fields(line)
	if len(f) == 0 {
		return ""
	}
	return f[len(f)-1]
}

// UplinkCounterSource reads the wired interfaces' carrier_changes and CRC counts once a minute and
// puts a reading on the ring when either moved, so the server sees a drop count it can trust
// between kernel lines (a rotated log loses lines; the counter does not).
type UplinkCounterSource struct {
	ring    *EventRing
	sysNet  string
	last    map[string]UplinkCounters
	ethtool func(ctx context.Context, iface string) (string, error)
}

// NewUplinkCounterSource reads /sys/class/net and ethtool -S.
func NewUplinkCounterSource(ring *EventRing) *UplinkCounterSource {
	return &UplinkCounterSource{
		ring:   ring,
		sysNet: "/sys/class/net",
		last:   map[string]UplinkCounters{},
		ethtool: func(ctx context.Context, iface string) (string, error) {
			return runCommand(ctx, 5*time.Second, "ethtool", "-S", iface)
		},
	}
}

// Run reads until ctx is cancelled.
func (s *UplinkCounterSource) Run(ctx context.Context) {
	ticker := time.NewTicker(uplinkCountersEvery)
	defer ticker.Stop()
	for {
		s.pass(ctx, time.Now().UTC())
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (s *UplinkCounterSource) pass(ctx context.Context, now time.Time) {
	ifaces, _ := filepath.Glob(filepath.Join(s.sysNet, "eth*"))
	for _, path := range ifaces {
		iface := filepath.Base(path)
		// eth0.N VLAN sub-interfaces repeat the parent's counters.
		if strings.Contains(iface, ".") {
			continue
		}
		var c UplinkCounters
		if v, ok := readInt(filepath.Join(path, "carrier_changes")); ok {
			c.CarrierChanges = &v
		}
		if out, err := s.ethtool(ctx, iface); err == nil {
			if v, ok := parseCrcCount(out); ok {
				c.RxCrcErrors = &v
			}
		}
		if c.CarrierChanges == nil && c.RxCrcErrors == nil {
			continue
		}
		prev, seen := s.last[iface]
		s.last[iface] = c
		if seen && sameCounters(prev, c) {
			continue
		}
		copied := c
		s.ring.Add(Event{Type: EventUplinkCounters, Iface: iface, Counters: &copied, CollectedAt: now})
	}
}

func sameCounters(a, b UplinkCounters) bool {
	eq := func(x, y *int64) bool { return (x == nil && y == nil) || (x != nil && y != nil && *x == *y) }
	return eq(a.CarrierChanges, b.CarrierChanges) && eq(a.RxCrcErrors, b.RxCrcErrors)
}

func readInt(path string) (int64, bool) {
	b, err := os.ReadFile(path)
	if err != nil {
		return 0, false
	}
	v, err := strconv.ParseInt(strings.TrimSpace(string(b)), 10, 64)
	return v, err == nil
}

// The receive CRC counter's name differs by NIC driver; the first one present wins.
var crcCounterNames = []string{"rx_crc_err", "rx_crc_errors", "rx_fcs_errors", "rx_crc_error"}

func parseCrcCount(ethtoolOut string) (int64, bool) {
	values := map[string]int64{}
	for _, line := range strings.Split(ethtoolOut, "\n") {
		k, v, ok := strings.Cut(strings.TrimSpace(line), ":")
		if !ok {
			continue
		}
		if n, err := strconv.ParseInt(strings.TrimSpace(v), 10, 64); err == nil {
			values[strings.TrimSpace(k)] = n
		}
	}
	for _, name := range crcCounterNames {
		if n, ok := values[name]; ok {
			return n, true
		}
	}
	return 0, false
}
