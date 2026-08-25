package main

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// This file is the agent's only mutating surface. Everything else reads. A BSS Transition request
// is a request: the client decides, and hostapd exposes only the disassoc-imminent variant, so a
// client that declines is disassociated when the timer expires and reassociates on its own.

const (
	ubusCallTimeout = 5 * time.Second

	// defaultBtmDurationTbtt is the disassociation timer in beacon intervals, roughly 10 seconds at
	// a 100 TU beacon. Long enough for a client to move of its own accord before it is pushed.
	defaultBtmDurationTbtt = 100
)

// NeighborReport is one AP's own 802.11k neighbor report element, which is exactly the shape the
// BTM candidate list wants. Collected from every AP so the server can build a candidate set.
type NeighborReport struct {
	Vap   string `json:"vap"`
	Bssid string `json:"bssid"`
	Ssid  string `json:"ssid"`
	// Element is the hex neighbor report element passed straight through to a BTM candidate list.
	Element string `json:"element"`
}

// RoamRequest asks one associated client to move. Candidates are neighbor report elements, and
// omitting this AP's own BSSIDs is what makes staying put a refusal rather than a valid choice.
type RoamRequest struct {
	Mac        string   `json:"mac"`
	Candidates []string `json:"candidates"`
	// DurationTbtt is the disassociation timer in beacon intervals; 0 takes the default.
	DurationTbtt int `json:"duration_tbtt,omitempty"`
	// Abridged tells the client the candidate list is the preferred set rather than a hint.
	Abridged bool `json:"abridged,omitempty"`
}

// RoamResult reports what was asked and of whom. It cannot report whether the client complied:
// that arrives later as a disassociation and an association elsewhere, through the event stream.
type RoamResult struct {
	Mac        string    `json:"mac"`
	Vap        string    `json:"vap"`
	Candidates int       `json:"candidates"`
	SentAt     time.Time `json:"sent_at"`
}

// ubusCall invokes one ubus method with a JSON argument and returns raw stdout.
func ubusCall(ctx context.Context, object, method, args string) (string, error) {
	if args == "" {
		return runCommand(ctx, ubusCallTimeout, "ubus", "call", object, method)
	}
	return runCommand(ctx, ubusCallTimeout, "ubus", "call", object, method, args)
}

// neighborReports collects each VAP's own neighbor report element.
func neighborReports(ctx context.Context, vaps []string) []NeighborReport {
	reports := make([]NeighborReport, 0, len(vaps))

	for _, vap := range vaps {
		out, err := ubusCall(ctx, "hostapd."+vap, "rrm_nr_get_own", "")
		if err != nil || strings.TrimSpace(out) == "" {
			continue
		}

		var payload struct {
			Value []string `json:"value"`
		}
		if err := json.Unmarshal([]byte(out), &payload); err != nil || len(payload.Value) < 3 {
			continue
		}

		reports = append(reports, NeighborReport{
			Vap:     vap,
			Bssid:   strings.ToLower(payload.Value[0]),
			Ssid:    payload.Value[1],
			Element: payload.Value[2],
		})
	}

	return reports
}

// sendRoam asks the client to transition, on whichever VAP currently holds it.
func sendRoam(ctx context.Context, table *Table, vaps []string, req RoamRequest) (*RoamResult, error) {
	// These are refusals, not failures. Returned as plain errors they became 500s, which the server
	// showed as "the access point refused the request (500)" - a client that roamed between the
	// caller picking this access point and the request arriving is the common case, and it is a 404.
	mac := strings.ToLower(strings.TrimSpace(req.Mac))
	if mac == "" {
		return nil, badRequest("no client MAC given")
	}
	if len(req.Candidates) == 0 {
		return nil, badRequest("no candidates given: a BTM request with an empty list tells the client nothing")
	}

	vap := table.VapForClient(mac)
	if vap == "" {
		// Fall back to asking hostapd directly: the table is a snapshot and a client that just
		// arrived may not be in it yet.
		vap = vapHoldingClient(ctx, vaps, mac)
	}
	if vap == "" {
		return nil, notFound("client %s is not associated to this access point", mac)
	}

	duration := req.DurationTbtt
	if duration <= 0 {
		duration = defaultBtmDurationTbtt
	}

	args := map[string]any{
		"addr":      mac,
		"duration":  duration,
		"abridged":  req.Abridged,
		"neighbors": req.Candidates,
	}
	encoded, err := json.Marshal(args)
	if err != nil {
		return nil, err
	}

	if _, err := ubusCall(ctx, "hostapd."+vap, "wnm_disassoc_imminent", string(encoded)); err != nil {
		return nil, fmt.Errorf("BTM request failed on %s: %w", vap, err)
	}

	return &RoamResult{
		Mac:        mac,
		Vap:        vap,
		Candidates: len(req.Candidates),
		SentAt:     time.Now().UTC(),
	}, nil
}

// vapHoldingClient asks each VAP whether it currently holds the client.
func vapHoldingClient(ctx context.Context, vaps []string, mac string) string {
	for _, vap := range vaps {
		out, err := ubusCall(ctx, "hostapd."+vap, "get_clients", "")
		if err != nil {
			continue
		}
		if strings.Contains(strings.ToLower(out), mac) {
			return vap
		}
	}
	return ""
}
