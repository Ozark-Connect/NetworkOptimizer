package main

import (
	"bufio"
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"os"
	"strconv"
	"strings"
	"time"
)

// StaEvent carries the association quality stahtd reports and hostapd does not. The UniFi Console
// exposes none of it, so these fields are the whole reason for tailing syslog.
type StaEvent struct {
	// EventType as stahtd reports it: association, success, deny, sta_roam, soft failure, failure.
	EventType string `json:"event_type"`
	// AuthRssi is the RSSI in dBm at the moment the client authenticated, which is what makes a
	// "joined at -91 while another AP was reachable at -67" comparison possible.
	AuthRssi *int `json:"auth_rssi,omitempty"`
	// AvgRssi accompanies sta_roam rather than auth_rssi.
	AvgRssi *int `json:"avg_rssi,omitempty"`
	// Phase deltas in microseconds, raw as reported. Named for the unit because nothing on the AP
	// states it and a millisecond reading would be silently wrong by 1000x.
	AuthDeltaUs    *int64 `json:"auth_delta_us,omitempty"`
	AssocDeltaUs   *int64 `json:"assoc_delta_us,omitempty"`
	WpaAuthDeltaUs *int64 `json:"wpa_auth_delta_us,omitempty"`
	// AuthAlgo is "ft" when 802.11r actually engaged, as opposed to merely being enabled.
	AuthAlgo string `json:"auth_algo,omitempty"`
	// AuthUtc is stahtd's own timestamp, epoch seconds with microsecond precision.
	AuthUtc *float64 `json:"auth_utc,omitempty"`
	// AssocStatus is the 802.11 status code as a string; "0" is success.
	AssocStatus string `json:"assoc_status,omitempty"`
	// DcReason is stahtd's disconnect reason on a deny, e.g. "sta left".
	DcReason string `json:"sta_dc_reason,omitempty"`
	// AuthFailures counts failed attempts on a deny.
	AuthFailures *int `json:"auth_failures,omitempty"`
}

const (
	ubntRoamMarker  = "UBNT_ROAM received"
	syslogPollEvery = 1 * time.Second
)

// extractJSONObject returns the first balanced {...} in a line. stahtd prints its JSON inside a
// log line that can carry trailing text (" - lock2ap, flags: 0x1000"), so unmarshalling the
// remainder of the line fails on exactly the events that matter most.
func extractJSONObject(line string) (string, bool) {
	start := strings.IndexByte(line, '{')
	if start < 0 {
		return "", false
	}
	depth := 0
	inString := false
	escaped := false
	for i := start; i < len(line); i++ {
		c := line[i]
		switch {
		case escaped:
			escaped = false
		case c == '\\' && inString:
			escaped = true
		case c == '"':
			inString = !inString
		case inString:
			// Braces inside a string value are not structure.
		case c == '{':
			depth++
		case c == '}':
			depth--
			if depth == 0 {
				return line[start : i+1], true
			}
		}
	}
	return "", false
}

// stahtd reports every value as a string, including the numeric ones.
func atoiPtr(s string) *int {
	if s == "" {
		return nil
	}
	v, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return nil
	}
	return &v
}

func atoi64Ptr(s string) *int64 {
	if s == "" {
		return nil
	}
	v, err := strconv.ParseInt(strings.TrimSpace(s), 10, 64)
	if err != nil {
		return nil
	}
	return &v
}

func atofPtr(s string) *float64 {
	if s == "" {
		return nil
	}
	v, err := strconv.ParseFloat(strings.TrimSpace(s), 64)
	if err != nil {
		return nil
	}
	return &v
}

// parseStahtdLine turns one syslog line into an event. Returns false for anything that is not a
// STA_ASSOC_TRACKER record.
func parseStahtdLine(line string, now time.Time) (Event, bool) {
	if !strings.Contains(line, stahtdMarker) {
		return Event{}, false
	}
	raw, ok := extractJSONObject(line)
	if !ok {
		return Event{}, false
	}

	var f struct {
		MessageType  string `json:"message_type"`
		EventType    string `json:"event_type"`
		MAC          string `json:"mac"`
		Vap          string `json:"vap"`
		AssocStatus  string `json:"assoc_status"`
		AuthRssi     string `json:"auth_rssi"`
		AvgRssi      string `json:"avg_rssi"`
		AuthDelta    string `json:"auth_delta"`
		AssocDelta   string `json:"assoc_delta"`
		WpaAuthDelta string `json:"wpa_auth_delta"`
		AuthAlgo     string `json:"auth_algo"`
		AuthUtc      string `json:"auth_utc"`
		DcReason     string `json:"sta_dc_reason"`
		AuthFailures string `json:"auth_failures"`
	}
	if err := json.Unmarshal([]byte(raw), &f); err != nil {
		return Event{}, false
	}
	if f.MessageType != stahtdMarker || f.MAC == "" {
		return Event{}, false
	}

	sta := &StaEvent{
		EventType:      f.EventType,
		AuthRssi:       atoiPtr(f.AuthRssi),
		AvgRssi:        atoiPtr(f.AvgRssi),
		AuthDeltaUs:    atoi64Ptr(f.AuthDelta),
		AssocDeltaUs:   atoi64Ptr(f.AssocDelta),
		WpaAuthDeltaUs: atoi64Ptr(f.WpaAuthDelta),
		AuthAlgo:       f.AuthAlgo,
		AuthUtc:        atofPtr(f.AuthUtc),
		AssocStatus:    f.AssocStatus,
		DcReason:       f.DcReason,
		AuthFailures:   atoiPtr(f.AuthFailures),
	}

	ev := Event{
		Type:        "sta_" + strings.ReplaceAll(strings.TrimSpace(f.EventType), " ", "_"),
		Vap:         f.Vap,
		MAC:         strings.ToLower(f.MAC),
		Sta:         sta,
		CollectedAt: now,
	}
	// stahtd's own clock is authoritative for when the association happened; ours is only when we
	// read the line, which lags by the syslog poll.
	if sta.AuthUtc != nil && *sta.AuthUtc > 0 {
		sec, frac := int64(*sta.AuthUtc), *sta.AuthUtc-float64(int64(*sta.AuthUtc))
		t := time.Unix(sec, int64(frac*1e9)).UTC()
		ev.EventTime = &t
	}
	return ev, true
}

// parseUbntRoamLine reads hostapd's cross-AP gossip. Measured shape:
//
//	wifi1ap6: STA <mac> WPA: UBNT_ROAM received: STA roamed to peer AP <bssid>
//
// This is a plain hostapd log line, not stahtd JSON, and it is what lets one AP report roams
// across the whole ESS including clients that have already left it.
func parseUbntRoamLine(line string, now time.Time) (Event, bool) {
	if !strings.Contains(line, ubntRoamMarker) {
		return Event{}, false
	}
	fields := strings.Fields(line)
	mac := firstMACIn(fields)
	peer := lastMACIn(fields)
	if mac == "" || peer == "" || mac == peer {
		return Event{}, false
	}

	vap := ""
	for _, f := range fields {
		if strings.HasSuffix(f, ":") && strings.HasPrefix(f, "wifi") {
			vap = strings.TrimSuffix(f, ":")
			break
		}
	}

	return Event{
		Type:        "roam_to_peer",
		Vap:         vap,
		MAC:         strings.ToLower(mac),
		PeerBssid:   strings.ToLower(peer),
		CollectedAt: now,
	}, true
}

// SyslogSource tails the AP's syslog for the two event families the hostapd control socket does
// not carry: stahtd's association quality records, and hostapd's UBNT_ROAM peer gossip.
type SyslogSource struct {
	path string
	ring *EventRing
}

func NewSyslogSource(path string, ring *EventRing) *SyslogSource {
	return &SyslogSource{path: path, ring: ring}
}

// Run tails the file until ctx is cancelled. It starts at the end: the ring is a live replay
// window, and replaying a boot's worth of history on every agent start would evict it.
func (s *SyslogSource) Run(ctx context.Context) {
	var (
		file   *os.File
		reader *bufio.Reader
		offset int64
	)
	defer func() {
		if file != nil {
			file.Close()
		}
	}()

	open := func() bool {
		f, err := os.Open(s.path)
		if err != nil {
			return false
		}
		end, err := f.Seek(0, io.SeekEnd)
		if err != nil {
			f.Close()
			return false
		}
		if file != nil {
			file.Close()
		}
		file, reader, offset = f, bufio.NewReader(f), end
		return true
	}

	if !open() {
		slog.Warn("syslog unavailable, roam quality and peer gossip will be missing", "path", s.path)
	}

	ticker := time.NewTicker(syslogPollEvery)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}

		if file == nil {
			if !open() {
				continue
			}
		}

		// Rotation shows up as the file shrinking under us; reopen rather than reading garbage.
		if st, err := os.Stat(s.path); err == nil && st.Size() < offset {
			if !open() {
				continue
			}
		}

		for {
			line, err := reader.ReadString('\n')
			if len(line) > 0 {
				offset += int64(len(line))
				s.consume(strings.TrimRight(line, "\r\n"))
			}
			if err != nil {
				break
			}
		}
	}
}

func (s *SyslogSource) consume(line string) {
	now := time.Now().UTC()
	if ev, ok := parseStahtdLine(line, now); ok {
		s.ring.Add(ev)
		return
	}
	if ev, ok := parseUbntRoamLine(line, now); ok {
		s.ring.Add(ev)
	}
}
