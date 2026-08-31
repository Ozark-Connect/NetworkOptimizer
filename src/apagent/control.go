package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
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

	// How long to watch for the client to leave, and how often to look. The window covers the BTM
	// timer with room to spare.
	departureWindow = 12 * time.Second
	departurePoll   = 500 * time.Millisecond

	// One beacon interval at 100 TU. Turns the BTM duration into wall time.
	beaconInterval = 102400 * time.Microsecond

	// 802.11 reason 1, unspecified. The client is leaving by request, not for a protocol fault.
	banReasonUnspecified = 1
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
	// BanMs blocks reassociation to this AP for a moment once the client has left, so it cannot
	// bounce straight back. 0 disables it. Only ever applied AFTER departure - see banOnDeparture.
	BanMs int `json:"ban_ms,omitempty"`
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

	// hostapd keys its station table per link, so an MLO client is not addressable there by its MLD
	// MAC: ubus answers "Not found" and the whole request fails. Substituted only when a different
	// link address is actually known for this VAP, so a non-MLO request is unchanged.
	addr := mac
	if link := table.LinkAddrForVap(mac, vap); link != "" {
		addr = link
	}

	args := map[string]any{
		"addr":      addr,
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

	if req.BanMs > 0 {
		// Past this the access point has disassociated the client itself, so leaving is no longer
		// evidence that it chose to.
		voluntary := time.Duration(duration) * beaconInterval
		go banOnDeparture(context.WithoutCancel(ctx), table, vaps, vap, mac, req.BanMs, voluntary)
	}

	return &RoamResult{
		Mac:        mac,
		Vap:        vap,
		Candidates: len(req.Candidates),
		SentAt:     time.Now().UTC(),
	}, nil
}

// banOnDeparture blocks the client from rejoining this AP for a moment, but only once it has
// actually left. A client that ignores the request keeps its association: the ban exists to stop a
// bounce back, never to force a departure, so a device with nowhere else to go is never stranded.
//
// hostapd scopes a ban to one VAP, so every VAP carrying the same SSID has to be told.
//
// A network running 802.11r is never banned. A hostapd ban answers every auth - fast transition
// included - with status 17 and wipes the client's PMKSA, which clients read as a hostile AP:
// observed teaching an iPhone to sit out minutes on a weak AP rather than retry, and feeding
// stahtd's auth-flood limiter into a stuck state. Losing the bounce guard there is the lesser cost.
func banOnDeparture(ctx context.Context, table *Table, vaps []string, holding, mac string, banMs int, voluntary time.Duration) {
	if ft, err := ftEnabledOnVap(ctx, holding); err != nil {
		slog.Warn("not banning, could not read the VAP's key_mgmt", "mac", mac, "vap", holding, "error", err)
		return
	} else if ft {
		slog.Info("not banning, the network runs 802.11r fast transition", "mac", mac, "vap", holding)
		return
	}

	started := time.Now()
	deadline := started.Add(departureWindow)
	for time.Now().Before(deadline) {
		select {
		case <-ctx.Done():
			return
		case <-time.After(departurePoll):
		}
		if vapHoldingClient(ctx, []string{holding}, mac) != "" {
			continue
		}

		// Only a client that left before the disassociation timer chose to. One the access point
		// pushed off could use none of the candidates, and banning it is how a device ends up on
		// no SSID at all.
		if elapsed := time.Since(started); elapsed > voluntary {
			slog.Info("not banning, client was disassociated rather than moving",
				"mac", mac, "after", elapsed.Round(time.Millisecond).String())
			return
		}

		banAcrossSsid(ctx, table, vaps, holding, mac, banMs)
		return
	}
}

// banAcrossSsid bans the client on every VAP sharing the holding VAP's SSID.
func banAcrossSsid(ctx context.Context, table *Table, vaps []string, holding, mac string, banMs int) {
	ssid := ""
	for _, v := range table.Vaps() {
		if v.Name == holding {
			ssid = v.Essid
			break
		}
	}

	targets := map[string]bool{holding: true}
	if ssid != "" {
		for _, v := range table.Vaps() {
			if v.Essid == ssid {
				targets[v.Name] = true
			}
		}
	}

	args, err := json.Marshal(map[string]any{
		"addr": mac, "reason": banReasonUnspecified, "deauth": false, "ban_time": banMs,
	})
	if err != nil {
		return
	}

	banned := make([]string, 0, len(targets))
	for _, vap := range vaps {
		if !targets[vap] {
			continue
		}
		if _, err := ubusCall(ctx, "hostapd."+vap, "del_client", string(args)); err != nil {
			slog.Warn("ban failed", "mac", mac, "vap", vap, "error", err)
			continue
		}
		banned = append(banned, vap)
	}
	slog.Info("banned after departure", "mac", mac, "ssid", ssid, "vaps", banned, "ban_ms", banMs)
}

// ftEnabledOnVap reports whether the VAP's network uses 802.11r fast transition. Read at ban time
// rather than cached: it follows the SSID's security settings, which can change under a running
// agent on any provision.
func ftEnabledOnVap(ctx context.Context, vap string) (bool, error) {
	out, err := runCommand(ctx, ubusCallTimeout, "hostapd_cli", "-i", vap, "get_config")
	if err != nil {
		return false, err
	}
	return ftInKeyMgmt(out), nil
}

// ftInKeyMgmt reports whether a hostapd get_config answer lists an FT AKM (FT-SAE, FT-PSK, ...)
// in its key_mgmt line.
func ftInKeyMgmt(config string) bool {
	for _, line := range strings.Split(config, "\n") {
		rest, found := strings.CutPrefix(strings.TrimSpace(line), "key_mgmt=")
		if !found {
			continue
		}
		for _, akm := range strings.Fields(rest) {
			if strings.HasPrefix(akm, "FT-") {
				return true
			}
		}
	}
	return false
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
