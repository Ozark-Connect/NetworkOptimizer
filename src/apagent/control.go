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
		go guardDeparture(context.WithoutCancel(ctx), table, vaps, vap, mac, req, duration)
	}

	return &RoamResult{
		Mac:        mac,
		Vap:        vap,
		Candidates: len(req.Candidates),
		SentAt:     time.Now().UTC(),
	}, nil
}

// guardDeparture applies the bounce guard once the client has actually left: a ban on a plain RSN
// network, a re-steer on one running fast transition. A client that ignores the request keeps its
// association: the guard exists to stop a bounce back, never to force a departure, so a device
// with nowhere else to go is never stranded.
func guardDeparture(ctx context.Context, table *Table, vaps []string, holding, mac string, req RoamRequest, duration int) {
	voluntary := time.Duration(duration) * beaconInterval

	left, elapsed := awaitDeparture(ctx, holding, mac)
	if !left {
		return
	}
	// Only a client that left before the disassociation timer chose to. One the access point
	// pushed off could use none of the candidates, and guarding against its return is how a
	// device ends up on no SSID at all.
	if elapsed > voluntary {
		slog.Info("no bounce guard, client was disassociated rather than moving",
			"mac", mac, "after", elapsed.Round(time.Millisecond).String())
		return
	}

	ft, err := ftEnabledOnVap(ctx, holding)
	if err != nil {
		// Not knowing must never ban: on an FT network the ban is the worse harm, and the
		// re-steer is safe on any network.
		slog.Warn("could not read the VAP's key_mgmt, guarding by re-steer",
			"mac", mac, "vap", holding, "error", err)
		ft = true
	}
	if ft {
		resteerOnBounce(ctx, table, vaps, holding, mac, req, duration, voluntary)
		return
	}
	banAcrossSsid(ctx, table, vaps, holding, mac, req.BanMs)
}

// awaitDeparture watches the VAP for the client to leave, reporting whether it did within the
// window and how long it took.
func awaitDeparture(ctx context.Context, vap, mac string) (bool, time.Duration) {
	started := time.Now()
	deadline := started.Add(departureWindow)
	for time.Now().Before(deadline) {
		select {
		case <-ctx.Done():
			return false, 0
		case <-time.After(departurePoll):
		}
		if vapHoldingClient(ctx, []string{vap}, mac) == "" {
			return true, time.Since(started)
		}
	}
	return false, 0
}

// maxResteers caps how many times a bounced client is asked again. A couple of evictions in quick
// succession get a client to deprioritize this AP on its own; past that it has made its choice.
const maxResteers = 2

// resteerOnBounce is the FT network's bounce guard. A hostapd ban answers every auth - fast
// transition included - with status 17 and wipes the client's PMKSA, which clients read as a
// hostile AP: observed teaching an iPhone to sit out minutes on a weak AP rather than retry, and
// feeding stahtd's auth-flood limiter into a stuck state. So a client that fast-transitions
// straight back is asked to move again with the same request instead - every transition stays
// clean, and the repeat eviction earns the deprioritization the ban used to force.
func resteerOnBounce(ctx context.Context, table *Table, vaps []string, holding, mac string, req RoamRequest, duration int, voluntary time.Duration) {
	// The same window the ban would have covered, restarted per eviction. A bounce can land on
	// any band of the SSID the client was steered off, so the whole set is watched.
	window := time.Duration(req.BanMs) * time.Millisecond
	ssidVaps := vapsSharingSsid(table, vaps, holding)

	// The first request keeps the current AP last so a client with nowhere to go can stay. A
	// re-steered client just proved it can move and came back, and leaving its own AP on the menu
	// lets a band hop satisfy the request: observed as a 5/6 GHz ping-pong that never left the AP.
	candidates := stripOwnCandidates(req.Candidates, ownNeighborhood(ctx, table, vaps))
	if len(candidates) == 0 {
		slog.Info("no bounce guard, every candidate is on this access point", "mac", mac)
		return
	}

	for attempt := 1; ; attempt++ {
		returned := awaitReturn(ctx, ssidVaps, mac, window)
		if returned == "" {
			slog.Info("bounce guard clear, client stayed away", "mac", mac, "resteers", attempt-1)
			return
		}
		if attempt > maxResteers {
			slog.Info("client keeps returning, leaving it", "mac", mac, "vap", returned)
			return
		}

		addr := mac
		if link := table.LinkAddrForVap(mac, returned); link != "" {
			addr = link
		}
		args, err := json.Marshal(map[string]any{
			"addr": addr, "duration": duration, "abridged": req.Abridged, "neighbors": candidates,
		})
		if err != nil {
			return
		}
		if _, err := ubusCall(ctx, "hostapd."+returned, "wnm_disassoc_imminent", string(args)); err != nil {
			slog.Warn("re-steer failed", "mac", mac, "vap", returned, "error", err)
			return
		}
		slog.Info("re-steering after bounce", "mac", mac, "vap", returned, "attempt", attempt)

		left, elapsed := awaitDeparture(ctx, returned, mac)
		if !left {
			slog.Info("client kept its association after re-steer", "mac", mac, "vap", returned)
			return
		}
		if elapsed > voluntary {
			slog.Info("no further guard, client was disassociated rather than moving", "mac", mac)
			return
		}
	}
}

// ownMarks describes this access point for candidate filtering: its VAPs' own neighbor elements
// (shadow entries included - the server's candidate lists are built from these very strings) and
// its BSSIDs as bare hex, the prefix a neighbor element opens with.
type ownMarks struct {
	elements map[string]bool
	bssidHex map[string]bool
}

func ownNeighborhood(ctx context.Context, table *Table, vaps []string) ownMarks {
	marks := ownMarks{elements: map[string]bool{}, bssidHex: map[string]bool{}}
	for _, r := range neighborReports(ctx, vaps) {
		marks.elements[strings.ToLower(r.Element)] = true
		marks.bssidHex[strings.ReplaceAll(r.Bssid, ":", "")] = true
	}
	for _, v := range table.Vaps() {
		hex := strings.ToLower(strings.ReplaceAll(v.Bssid, ":", ""))
		if len(hex) == 12 {
			marks.bssidHex[hex] = true
		}
	}
	return marks
}

// stripOwnCandidates drops candidates that lead back to this access point, matched by exact
// element or by the BSSID the element opens with.
func stripOwnCandidates(candidates []string, own ownMarks) []string {
	kept := make([]string, 0, len(candidates))
	for _, c := range candidates {
		el := strings.ToLower(c)
		if own.elements[el] {
			continue
		}
		if len(el) >= 12 && own.bssidHex[el[:12]] {
			continue
		}
		kept = append(kept, c)
	}
	return kept
}

// awaitReturn watches the SSID's VAPs for the client to reappear, returning the VAP holding it,
// or "" when it stayed away for the whole window.
func awaitReturn(ctx context.Context, vaps []string, mac string, window time.Duration) string {
	deadline := time.Now().Add(window)
	for time.Now().Before(deadline) {
		select {
		case <-ctx.Done():
			return ""
		case <-time.After(departurePoll):
		}
		if vap := vapHoldingClient(ctx, vaps, mac); vap != "" {
			return vap
		}
	}
	return ""
}

// banAcrossSsid bans the client on every VAP sharing the holding VAP's SSID. hostapd scopes a ban
// to one VAP, so the whole set has to be told.
func banAcrossSsid(ctx context.Context, table *Table, vaps []string, holding, mac string, banMs int) {
	args, err := json.Marshal(map[string]any{
		"addr": mac, "reason": banReasonUnspecified, "deauth": false, "ban_time": banMs,
	})
	if err != nil {
		return
	}

	targets := vapsSharingSsid(table, vaps, holding)
	banned := make([]string, 0, len(targets))
	for _, vap := range targets {
		if _, err := ubusCall(ctx, "hostapd."+vap, "del_client", string(args)); err != nil {
			slog.Warn("ban failed", "mac", mac, "vap", vap, "error", err)
			continue
		}
		banned = append(banned, vap)
	}
	slog.Info("banned after departure", "mac", mac, "vaps", banned, "ban_ms", banMs)
}

// vapsSharingSsid is the holding VAP plus every VAP carrying its SSID - the scope both bounce
// guards operate on, since a steered client can land on any band of the network it left.
func vapsSharingSsid(table *Table, vaps []string, holding string) []string {
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

	out := make([]string, 0, len(targets))
	for _, vap := range vaps {
		if targets[vap] {
			out = append(out, vap)
		}
	}
	return out
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
