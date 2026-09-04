package main

import (
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ClientLink is one association. A non-MLO client has exactly one; a Wi-Fi 7 client has one per
// link, each under its own VAP with its own locally administered MAC.
type ClientLink struct {
	MAC        string `json:"mac"`
	Vap        string `json:"vap"`
	Ssid       string `json:"ssid,omitempty"`
	Bssid      string `json:"bssid,omitempty"`
	Radio      string `json:"radio,omitempty"`
	Band       string `json:"band,omitempty"`
	Channel    int    `json:"channel,omitempty"`
	Bandwidth  int    `json:"bw,omitempty"`
	Active     bool   `json:"active"`
	Negotiated bool   `json:"negotiated_idle,omitempty"`
	Signal     *int   `json:"signal,omitempty"`
	Noise      *int   `json:"noise,omitempty"`
	SNR        *int   `json:"snr,omitempty"`
	MinSignal  *int   `json:"min_signal,omitempty"`
	MaxSignal  *int   `json:"max_signal,omitempty"`
	TxRateKbps int64  `json:"tx_rate_kbps,omitempty"`
	RxRateKbps int64  `json:"rx_rate_kbps,omitempty"`
	TxRateMov  int64  `json:"tx_rate_mov_kbps,omitempty"`
	RxRateMov  int64  `json:"rx_rate_mov_kbps,omitempty"`
	Nss        int    `json:"nss,omitempty"`
	TxNss      int    `json:"tx_nss,omitempty"`
	RxNss      int    `json:"rx_nss,omitempty"`
	Ccq        int    `json:"ccq,omitempty"`
	Mode       string `json:"mode,omitempty"`
	TxBytes    int64  `json:"tx_bytes,omitempty"`
	RxBytes    int64  `json:"rx_bytes,omitempty"`
	// BytesAt dates the counters when they came from the byte tier rather than the identity poll.
	// The server divides a counter delta by the gap between these, so an assumed interval would
	// misreport throughput whenever a poll ran late.
	BytesAt      *time.Time `json:"bytes_at,omitempty"`
	TxPackets    int64      `json:"tx_packets,omitempty"`
	RxPackets    int64      `json:"rx_packets,omitempty"`
	TxRetries    int64      `json:"tx_retries,omitempty"`
	TxCombined   int64      `json:"tx_combined_retries,omitempty"`
	TxRtsRetries int64      `json:"tx_rts_retries,omitempty"`
	TxAttempts   int64      `json:"wifi_tx_attempts,omitempty"`
	TxDropped    int64      `json:"wifi_tx_dropped,omitempty"`
	TxSuccess    int64      `json:"wifi_tx_success,omitempty"`
	TxLatency    *TxLatency `json:"wifi_tx_latency_mov,omitempty"`
	TxTcpStats   *TcpStats  `json:"tx_tcp_stats,omitempty"`
	RxTcpStats   *TcpStats  `json:"rx_tcp_stats,omitempty"`
	Satisfaction *int       `json:"satisfaction,omitempty"`
	Authorized   bool       `json:"authorized"`
	PowerSave    bool       `json:"power_save,omitempty"`
	Uptime       int64      `json:"uptime_seconds,omitempty"`
	IdleSeconds  int64      `json:"idle_seconds"`
	AssocSeconds int        `json:"assoc_seconds,omitempty"`
	// JoinRssi is the signal at authentication as stahtd reported it; absent for a link found by a
	// poll, whose association predates the agent. The BTM counters are this association's BSS
	// transition responses: answered, and answered with acceptance. All reset on a new assoc.
	JoinRssi     *int       `json:"join_rssi,omitempty"`
	BtmRequests  int        `json:"btm_requests,omitempty"`
	BtmAccepted  int        `json:"btm_accepted,omitempty"`
	Membership   string     `json:"membership_source"`
	HasIdentity  bool       `json:"-"`
	AssocEventAt *time.Time `json:"assoc_event_at,omitempty"`
	AssocSeq     uint64     `json:"assoc_event_seq,omitempty"`
	FastAt       *time.Time `json:"fast_collected_at,omitempty"`
	SlowAt       *time.Time `json:"slow_collected_at,omitempty"`
	CollectedAt  time.Time  `json:"collected_at"`
}

// ClientCapabilities is what the client can do, as opposed to what it is doing right now.
type ClientCapabilities struct {
	Is11ax    bool `json:"is_11ax"`
	Is11be    bool `json:"is_11be"`
	Is11ac    bool `json:"is_11ac"`
	Is11n     bool `json:"is_11n"`
	Is11r     bool `json:"is_11r"`
	IsMlo     bool `json:"is_mlo"`
	Nss       int  `json:"nss,omitempty"`
	BwMaxSupp int  `json:"bw_max_supp,omitempty"`
}

// ClientSources says which tiers contributed to a record, so an absent field can be told apart
// from a field the AP reported as zero.
type ClientSources struct {
	Event bool `json:"event"`
	Fast  bool `json:"fast"`
	Slow  bool `json:"slow"`
}

// Client is one device. The key is the MLD MAC when the client is MLO: keying on the link MAC
// invents one client per link for every Wi-Fi 7 device. Scalar fields report the ACTIVE link,
// because a single client's links measured 56 dB apart and an arbitrary pick reads a healthy
// client as dying.
type Client struct {
	Key                   string             `json:"key"`
	MAC                   string             `json:"mac"`
	MldMAC                string             `json:"mld_mac,omitempty"`
	IsMlo                 bool               `json:"is_mlo"`
	LinkCount             int                `json:"link_count"`
	Hostname              string             `json:"hostname,omitempty"`
	IP                    string             `json:"ip,omitempty"`
	IPv6                  []string           `json:"ipv6_addresses,omitempty"`
	Vap                   string             `json:"vap,omitempty"`
	Ssid                  string             `json:"ssid,omitempty"`
	Bssid                 string             `json:"bssid,omitempty"`
	Radio                 string             `json:"radio,omitempty"`
	Band                  string             `json:"band,omitempty"`
	Channel               int                `json:"channel,omitempty"`
	Bandwidth             int                `json:"bw,omitempty"`
	Signal                *int               `json:"signal,omitempty"`
	Noise                 *int               `json:"noise,omitempty"`
	SNR                   *int               `json:"snr,omitempty"`
	TxRateKbps            int64              `json:"tx_rate_kbps,omitempty"`
	RxRateKbps            int64              `json:"rx_rate_kbps,omitempty"`
	TxRateMov             int64              `json:"tx_rate_mov_kbps,omitempty"`
	RxRateMov             int64              `json:"rx_rate_mov_kbps,omitempty"`
	Satisfaction          *int               `json:"satisfaction,omitempty"`
	SatisfactionReal      *int               `json:"satisfaction_real,omitempty"`
	SatisfactionSubscores []int              `json:"satisfaction_subscores,omitempty"`
	Anomalies             int                `json:"anomalies,omitempty"`
	VlanID                int                `json:"vlan_id,omitempty"`
	AnonClientID          string             `json:"anon_client_id,omitempty"`
	Capabilities          ClientCapabilities `json:"capabilities"`
	Mlo                   *MloInfo           `json:"mlo,omitempty"`
	Authorized            bool               `json:"authorized"`
	FirstSeenAt           time.Time          `json:"first_seen_at"`
	LastSeenAt            time.Time          `json:"last_seen_at"`
	IdentityAt            *time.Time         `json:"identity_collected_at,omitempty"`
	Sources               ClientSources      `json:"sources"`
	Links                 []ClientLink       `json:"links"`
	CollectedAt           time.Time          `json:"collected_at"`
}

// memberState is one link's membership, which the event stream owns. Source records whether the
// association was pushed or found by a poll, because a poll-sourced member is one whose assoc
// event the agent was not running for.
type memberState struct {
	Vap       string
	MAC       string
	Source    string
	AssocAt   *time.Time
	AssocSeq  uint64
	FirstSeen time.Time
	LastSeen  time.Time
	// What this association has taught us; see ClientLink. Reset on assoc.
	JoinRssi    *int
	BtmRequests int
	BtmAccepted int
}

// pendingJoin holds a stahtd join RSSI that arrived before the link was a member, which the
// syslog tail and the control socket race for on every association.
type pendingJoin struct {
	rssi int
	at   time.Time
}

const pendingJoinTtl = 2 * time.Minute

type identityRecord struct {
	Hostname string
	IP       string
	IPv6     []string
	At       time.Time
}

// TierInfo is one collection tier's last outcome. Serving a degraded record beats failing the
// request, so the consumer is told which tiers were behind the answer.
type TierInfo struct {
	Available       bool       `json:"available"`
	IntervalSeconds float64    `json:"interval_seconds,omitempty"`
	LastCollectedAt *time.Time `json:"last_collected_at,omitempty"`
	LastError       string     `json:"last_error,omitempty"`
	Runs            uint64     `json:"runs"`
	Failures        uint64     `json:"failures"`
}

// TierStatus is the three-tier collection model's health, reported on every payload.
type TierStatus struct {
	Events TierInfo `json:"events"`
	Fast   TierInfo `json:"fast"`
	Slow   TierInfo `json:"slow"`
	Bytes  TierInfo `json:"bytes"`
}

// ApInfo identifies the AP a payload came from, so a collector fanning out over a fleet can tell
// the answers apart without tracking which address it asked.
type ApInfo struct {
	Hostname string `json:"hostname,omitempty"`
	Model    string `json:"model,omitempty"`
	MAC      string `json:"mac,omitempty"`
	Firmware string `json:"firmware,omitempty"`
}

// Table is the in-memory state every endpoint reads. A request never triggers a collection: N
// pollers would otherwise cost N times the collection.
type Table struct {
	mu      sync.RWMutex
	maxSize int
	ttl     time.Duration
	members map[string]*memberState
	fast    map[string]StaFast
	slow    map[string]StaSlow
	// slowAt dates t.slow, so a byte reading can be told apart from the identity poll's copy.
	slowAt time.Time
	// bytes is its own map because ApplySlow replaces t.slow wholesale on every pass. Keeping the
	// counters separate is what lets them refresh far faster than the identity poll that used to
	// be their only source.
	bytes    map[string]StaBytes
	identity map[string]identityRecord
	vaps     []VapState
	radios   []RadioState
	scans    []RadioScan
	scansAt  time.Time
	ap       ApInfo
	tiers    TierStatus

	// prevCounters survives ApplySlow, which replaces the radio table wholesale. Without it the
	// delta window would reset on every pass and the CCA wedge would never be visible.
	prevCounters   map[string]map[string]int64
	prevCountersAt time.Time

	// centers is the last `iw dev` answer by interface. Held here for the same reason as
	// prevCounters: ApplySlow replaces the radio table wholesale, and the center is re-applied
	// to the fresh table on every pass rather than being lost between slow-tier reads.
	centers map[string]iwChannel

	pendingJoin map[string]pendingJoin
}

func NewTable(maxSize int, ttl time.Duration) *Table {
	return &Table{
		maxSize:      maxSize,
		ttl:          ttl,
		members:      map[string]*memberState{},
		fast:         map[string]StaFast{},
		slow:         map[string]StaSlow{},
		bytes:        map[string]StaBytes{},
		identity:     map[string]identityRecord{},
		prevCounters: map[string]map[string]int64{},
		pendingJoin:  map[string]pendingJoin{},
	}
}

// adoptPendingLocked gives a member the join RSSI that arrived before it existed, if one did.
func (t *Table) adoptPendingLocked(key string, m *memberState, now time.Time) {
	p, ok := t.pendingJoin[key]
	if !ok {
		return
	}
	delete(t.pendingJoin, key)
	if now.Sub(p.at) <= pendingJoinTtl {
		rssi := p.rssi
		m.JoinRssi = &rssi
	}
}

// ApplyEvent folds a pushed membership fact into the table. The event stream is authoritative for
// membership: an assoc adds the link immediately and a disassoc removes it immediately, neither
// waiting for a poll to notice.
func (t *Table) ApplyEvent(e Event) {
	if e.MAC == "" || e.Vap == "" {
		return
	}
	// A wireless uplink associates and roams exactly like a client does, so the events have to be
	// filtered as well as the polls.
	if isFabricVap(e.Vap) {
		return
	}
	key := stationKey(e.Vap, e.MAC)

	t.mu.Lock()
	defer t.mu.Unlock()

	switch e.Type {
	case EventAssoc:
		at := e.CollectedAt
		if e.EventTime != nil {
			at = *e.EventTime
		}
		m, ok := t.members[key]
		if !ok {
			m = &memberState{Vap: e.Vap, MAC: e.MAC, FirstSeen: at}
			t.members[key] = m
		}
		m.Source = "event"
		m.AssocAt = &at
		m.AssocSeq = e.Seq
		m.LastSeen = e.CollectedAt
		// A new association learns afresh.
		m.JoinRssi, m.BtmRequests, m.BtmAccepted = nil, 0, 0
		t.adoptPendingLocked(key, m, e.CollectedAt)
		t.evictLocked()

	case EventDisassoc:
		delete(t.members, key)
		delete(t.fast, key)
		delete(t.slow, key)

	case EventBtmResponse:
		if m, ok := t.members[key]; ok {
			m.BtmRequests++
			if e.Detail == "0" {
				m.BtmAccepted++
			}
		}

	default:
		// stahtd's association record, which carries the RSSI at authentication. It can arrive
		// before hostapd's assoc, so a join for a link that is not yet a member waits for it.
		if e.Sta == nil || e.Sta.AuthRssi == nil || (e.Type != "sta_association" && e.Type != "sta_success") {
			return
		}
		rssi := *e.Sta.AuthRssi
		if m, ok := t.members[key]; ok {
			m.JoinRssi = &rssi
		} else {
			t.pendingJoin[key] = pendingJoin{rssi: rssi, at: e.CollectedAt}
		}
	}
}

// ApplyFast folds one fast sweep in. A station present in a poll but with no assoc event is added:
// the agent starts fresh on every AP boot and was not running for associations that predate it.
// OccupiedVaps names the VAPs the table holds a member for. A VAP stays occupied for as long as a
// member lingers, so it keeps its full cadence across the whole absentGrace window - which is
// exactly the window that detects a client that left without disassociating.
func (t *Table) OccupiedVaps() map[string]bool {
	t.mu.RLock()
	defer t.mu.RUnlock()
	out := make(map[string]bool, len(t.members))
	for _, m := range t.members {
		out[m.Vap] = true
	}
	return out
}

// covered names the VAPs the sweep actually reached, which is what bounds expiry to them.
func (t *Table) ApplyFast(stations map[string]StaFast, covered map[string]bool, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()

	for key, s := range stations {
		if isFabricVap(s.Vap) {
			continue
		}
		t.fast[key] = s
		m, ok := t.members[key]
		if !ok {
			m = &memberState{Vap: s.Vap, MAC: s.MAC, Source: "poll", FirstSeen: now}
			t.members[key] = m
			t.adoptPendingLocked(key, m, now)
		}
		m.LastSeen = now
	}
	t.expireLocked(covered, now)
	t.evictLocked()
}

// ApplySlow folds one mca-dump pass in and refreshes the identity cache. Identity is cached by
// client key across the slow interval because it changes rarely, and on an MLO client only the
// active link carries it at all.
func (t *Table) ApplySlow(snap McaSnapshot, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()

	t.vaps = snap.Vaps
	t.radios = snap.Radios
	t.scans, t.scansAt = snap.Scans, now
	t.applyCentersLocked()
	if snap.Hostname != "" {
		t.ap.Hostname = snap.Hostname
	}
	if snap.Model != "" {
		t.ap.Model = snap.Model
	}
	if snap.Version != "" {
		t.ap.Firmware = snap.Version
	}

	t.slowAt = now
	t.slow = make(map[string]StaSlow, len(snap.Stations))
	for _, s := range snap.Stations {
		if isFabricVap(s.Vap) {
			continue
		}
		key := stationKey(s.Vap, s.MAC)
		t.slow[key] = s

		m, ok := t.members[key]
		if !ok {
			m = &memberState{Vap: s.Vap, MAC: s.MAC, Source: "poll", FirstSeen: now}
			t.members[key] = m
			t.adoptPendingLocked(key, m, now)
		}
		m.LastSeen = now

		if s.Hostname == "" && s.IP == "" {
			continue
		}
		t.identity[clientKeyFor(s)] = identityRecord{
			Hostname: s.Hostname, IP: s.IP, IPv6: s.IPv6, At: now,
		}
	}
	covered := make(map[string]bool, len(snap.Vaps))
	for _, v := range snap.Vaps {
		covered[v.Name] = true
	}
	t.expireLocked(covered, now)
	t.evictLocked()
}

// ApplyBytes records per-station counters. It deliberately does NOT touch LastSeen: presence is
// decided by the tiers that enumerate a VAP, and a counter read for one MAC is no evidence about
// who is still associated.
func (t *Table) ApplyBytes(readings map[string]StaBytes, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()

	for key, b := range readings {
		if _, ok := t.members[key]; !ok {
			continue
		}
		t.bytes[key] = b
	}
	for key := range t.bytes {
		if _, ok := t.members[key]; !ok {
			delete(t.bytes, key)
		}
	}
}

// SetRadioCounters merges the radio-stats tools into the radio table and computes deltas against
// the previous pass, which is what a CCA wedge is read from. Call it after every ApplySlow, with an
// empty map when the tools are unavailable: mca-dump's own cu_* counters still want deltas.
func (t *Table) SetRadioCounters(counters map[string]map[string]int64, sources map[string][]string, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()

	// A radio the tools answered for but neither mca-dump table listed still gets a row. Dropping
	// it would hide a real radio; the flag says the row carries counters and nothing else.
	known := make(map[string]bool, len(t.radios))
	for _, r := range t.radios {
		known[r.Name] = true
	}
	for name := range counters {
		if !known[name] {
			t.radios = append(t.radios, RadioState{Name: name, CounterOnly: true, CollectedAt: now})
		}
	}

	elapsed := now.Sub(t.prevCountersAt).Seconds()
	for i := range t.radios {
		r := &t.radios[i]
		if found, ok := counters[r.Name]; ok {
			r.Counters = mergeCounters(r.Counters, found)
			r.CounterSources = append(r.CounterSources, sources[r.Name]...)
		}
		if len(r.Counters) == 0 {
			continue
		}
		if prev, ok := t.prevCounters[r.Name]; ok && !t.prevCountersAt.IsZero() {
			if d := counterDeltas(prev, r.Counters); d != nil {
				r.Deltas = d
				r.DeltaSeconds = elapsed
			}
		}
		snapshot := make(map[string]int64, len(r.Counters))
		for k, v := range r.Counters {
			snapshot[k] = v
		}
		t.prevCounters[r.Name] = snapshot
	}
	t.prevCountersAt = now
}

// SetRadioCenters records one `iw dev` pass and applies it to the radio table. An empty pass keeps
// the previous answer: iw failing once must not flap the field off and on.
func (t *Table) SetRadioCenters(centers map[string]iwChannel, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()
	if len(centers) == 0 {
		return
	}
	t.centers = centers
	t.applyCentersLocked()
}

func (t *Table) applyCentersLocked() {
	for i := range t.radios {
		t.radios[i].CenterMhz = centerForRadio(t.radios[i], t.vaps, t.centers)
	}
}

// CentersStale reports a serving radio that has a channel but no center: the held iw answer is
// from before its channel change, or nothing has been read yet.
func (t *Table) CentersStale() bool {
	t.mu.RLock()
	defer t.mu.RUnlock()
	for _, r := range t.radios {
		if !r.ScanRadio && !r.CounterOnly && r.Channel != 0 && r.CenterMhz == 0 {
			return true
		}
	}
	return false
}

func (t *Table) SetTiers(s TierStatus) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.tiers = s
}

func (t *Table) SetApMAC(mac string) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.ap.MAC = mac
}

// expireLocked drops links a covered VAP has stopped listing. A poll that reached the VAP and did
// not find the client IS evidence the client left, so the window here only needs to outlast a
// missed read, not a whole disassoc timeout: the fast tier runs at 1 Hz, making absentGrace several
// consecutive absences.
//
// Never widen this back to a disassoc-timeout scale. A client that turns Wi-Fi off sends nothing,
// so it leaves only by this path, and every second it lingers the collector keeps republishing it
// as live: it stays drawn on the maps, and if it comes back on another AP the stale entry pins it
// to the old one. Roaming hides the bug, since a BTM roam does send a disassoc.
//
// Only VAPs a poll actually reached are expired: an unreachable tool is not evidence a client left.
func (t *Table) expireLocked(covered map[string]bool, now time.Time) {
	for key, m := range t.members {
		if !covered[m.Vap] || now.Sub(m.LastSeen) <= absentGrace {
			continue
		}
		delete(t.members, key)
		delete(t.fast, key)
		delete(t.slow, key)
	}
	for key, p := range t.pendingJoin {
		if now.Sub(p.at) > pendingJoinTtl {
			delete(t.pendingJoin, key)
		}
	}
	t.pruneIdentityLocked()
}

// evictLocked enforces the hard cap, dropping the least recently seen first. An AP holds a few
// dozen clients, so reaching the cap means something is wrong rather than busy.
func (t *Table) evictLocked() {
	if len(t.members) <= t.maxSize {
		return
	}
	keys := make([]string, 0, len(t.members))
	for k := range t.members {
		keys = append(keys, k)
	}
	sort.Slice(keys, func(i, j int) bool {
		return t.members[keys[i]].LastSeen.Before(t.members[keys[j]].LastSeen)
	})
	for _, k := range keys[:len(t.members)-t.maxSize] {
		delete(t.members, k)
		delete(t.fast, k)
		delete(t.slow, k)
	}
	t.pruneIdentityLocked()
}

// pruneIdentityLocked keeps the identity cache to clients that are still associated.
func (t *Table) pruneIdentityLocked() {
	live := make(map[string]bool, len(t.members))
	for key, m := range t.members {
		live[m.MAC] = true
		if s, ok := t.slow[key]; ok {
			live[clientKeyFor(s)] = true
		}
	}
	for key := range t.identity {
		if !live[key] {
			delete(t.identity, key)
		}
	}
}

// clientKeyFor is the client identity rule: the MLD MAC when the AP reports one, the link MAC
// otherwise. Keying on the link MAC alone invents one client per link for every Wi-Fi 7 device.
func clientKeyFor(s StaSlow) string {
	if s.MldMAC != nil && *s.MldMAC != "" {
		return *s.MldMAC
	}
	return s.MAC
}

// Clients builds the merged view from the table. Nothing here collects; it is a read of state the
// tiers already wrote.
func (t *Table) Clients(now time.Time) []Client {
	t.mu.RLock()
	defer t.mu.RUnlock()

	vapByName := make(map[string]VapState, len(t.vaps))
	for _, v := range t.vaps {
		vapByName[v.Name] = v
	}

	grouped := map[string][]ClientLink{}
	meta := map[string]*Client{}

	for key, m := range t.members {
		link := ClientLink{
			MAC:         m.MAC,
			Vap:         m.Vap,
			Membership:  m.Source,
			AssocSeq:    m.AssocSeq,
			JoinRssi:    m.JoinRssi,
			BtmRequests: m.BtmRequests,
			BtmAccepted: m.BtmAccepted,
			CollectedAt: now,
		}
		if m.AssocAt != nil {
			at := *m.AssocAt
			link.AssocEventAt = &at
		}
		if v, ok := vapByName[m.Vap]; ok {
			link.Ssid, link.Bssid, link.Radio, link.Band = v.Essid, v.Bssid, v.Radio, v.Band
			link.Channel, link.Bandwidth = v.Channel, v.Bandwidth
		}

		clientKey := m.MAC
		record := &Client{FirstSeenAt: m.FirstSeen, LastSeenAt: m.LastSeen}
		chwidth := -1

		if f, ok := t.fast[key]; ok {
			applyFastToLink(&link, f)
			record.Sources.Fast = true
		}
		if s, ok := t.slow[key]; ok {
			applySlowToLink(&link, s)
			chwidth = s.ChWidth
			clientKey = clientKeyFor(s)
			record.Sources.Slow = true
			record.MldMAC = ptrString(s.MldMAC)
			record.IsMlo = s.IsMlo
			record.Mlo = s.Mlo
			record.VlanID = s.VlanID
			record.AnonClientID = s.AnonClientID
			record.Anomalies = s.Anomalies
			record.SatisfactionSubscores = s.SatisfactionSubscores
			record.SatisfactionReal = s.SatisfactionReal
			record.Capabilities = ClientCapabilities{
				Is11ax: s.Is11ax, Is11be: s.Is11be, Is11ac: s.Is11ac, Is11n: s.Is11n,
				Is11r: s.Is11r, IsMlo: s.IsMlo, Nss: s.Nss, BwMaxSupp: s.BwMaxSupp,
			}
		}
		link.Bandwidth = negotiatedWidth(link.Mode, chwidth, link.Bandwidth)

		// Counters are always dated by when they were actually read, whichever tier supplied
		// them. Leaving the identity poll's copy undated made the server date counters up to a
		// whole slow interval old as if they were current, so the delta was divided by the wrong
		// interval: one oversized spike, then zeroes until they moved again.
		if _, ok := t.slow[key]; ok && !t.slowAt.IsZero() {
			at := t.slowAt
			link.BytesAt = &at
		}
		if b, ok := t.bytes[key]; ok && b.At.After(t.slowAt) {
			// Same counters and same direction - apstats and mca-dump report a station
			// identically - so this only ever makes them newer.
			at := b.At
			link.TxBytes, link.RxBytes = b.TxBytes, b.RxBytes
			link.BytesAt = &at
		}

		if m.Source == "event" {
			record.Sources.Event = true
		}

		grouped[clientKey] = append(grouped[clientKey], link)
		if existing, ok := meta[clientKey]; ok {
			mergeClientMeta(existing, record)
		} else {
			meta[clientKey] = record
		}
	}

	clients := make([]Client, 0, len(grouped))
	for key, links := range grouped {
		c := *meta[key]
		c.Key = key
		c.CollectedAt = now
		sortLinks(links)
		markActiveLink(links)
		c.Links = links
		c.LinkCount = len(links)

		active := links[0]
		for _, l := range links {
			c.Authorized = c.Authorized || l.Authorized
			if l.Active {
				active = l
			}
		}
		applyActiveLinkToClient(&c, active)

		if id, ok := t.identity[key]; ok {
			c.Hostname, c.IP, c.IPv6 = id.Hostname, id.IP, id.IPv6
			at := id.At
			c.IdentityAt = &at
		}
		if c.MldMAC != "" {
			c.MAC = c.MldMAC
		} else {
			c.MAC = active.MAC
		}
		clients = append(clients, c)
	}

	sort.Slice(clients, func(i, j int) bool { return clients[i].Key < clients[j].Key })
	return clients
}

func applyFastToLink(link *ClientLink, f StaFast) {
	if link.Channel == 0 {
		link.Channel = f.Channel
	}
	link.TxRateKbps, link.RxRateKbps = f.TxRateKbps, f.RxRateKbps
	link.Signal, link.MinSignal, link.MaxSignal = f.Signal, f.MinSignal, f.MaxSignal
	link.SNR = f.SNR
	link.IdleSeconds = int64(f.IdleSeconds)
	link.AssocSeconds = f.AssocSeconds
	link.Mode, link.TxNss, link.RxNss = f.Mode, f.TxNss, f.RxNss
	at := f.CollectedAt
	link.FastAt = &at
}

// applySlowToLink fills what only mca-dump reports. RF belongs to the fast tier: wlanconfig samples
// signal and rates at 1 Hz, and letting the 30s tier overwrite them served a value that changed
// twice a minute, which is slower than the console path this replaces. Slow only supplies RF for a
// link the fast tier has not reported.
func applySlowToLink(link *ClientLink, s StaSlow) {
	stale := link.FastAt == nil
	if s.Signal != nil && stale {
		link.Signal = s.Signal
	}
	link.Noise = s.Noise
	if s.RSSI != nil && stale {
		link.SNR = s.RSSI
	}
	if s.TxRate > 0 && stale {
		link.TxRateKbps = s.TxRate
	}
	if s.RxRate > 0 && stale {
		link.RxRateKbps = s.RxRate
	}
	link.TxRateMov, link.RxRateMov = s.TxRateMov, s.RxRateMov
	link.Nss, link.Ccq = s.Nss, s.Ccq
	link.TxBytes, link.RxBytes = s.TxBytes, s.RxBytes
	link.TxPackets, link.RxPackets = s.TxPackets, s.RxPackets
	link.TxRetries, link.TxCombined, link.TxRtsRetries = s.TxRetries, s.TxCombinedRetries, s.TxRtsRetries
	link.TxAttempts, link.TxDropped, link.TxSuccess = s.WifiTxAttempts, s.WifiTxDropped, s.WifiTxSuccess
	link.TxLatency, link.TxTcpStats, link.RxTcpStats = s.WifiTxLatencyMov, s.TxTcpStats, s.RxTcpStats
	link.Satisfaction = s.Satisfaction
	link.Authorized, link.PowerSave = s.Authorized, s.PowerSave
	link.Uptime = s.Uptime
	if s.IdleTime > 0 {
		link.IdleSeconds = s.IdleTime
	}
	// A link whose idle time equals its uptime has never passed traffic: negotiated, not in use.
	link.Negotiated = s.Uptime > 0 && s.IdleTime >= s.Uptime && s.TxBytes == 0
	link.HasIdentity = s.Hostname != "" || s.IP != ""
	at := s.SnapshotAt
	link.SlowAt = &at
}

// negotiatedWidth is the width the client is actually using, never the radio's. The VAP's bw is
// the radio's operating width, and serving it per station showed a 160 MHz phone as 320. The
// fast tier's phy-mode suffix is preferred (1 Hz, and it names the width outright), then the slow
// tier's width index; the radio's width is the fallback and the ceiling. The ceiling is what gets a
// UniFi "240 MHz" radio right: the driver has no 240 mode, so a client on one reports EHT320.
func negotiatedWidth(mode string, chwidth int, radioWidth int) int {
	w := widthFromMode(mode)
	if w == 0 {
		w = widthFromIndex(chwidth)
	}
	if w == 0 {
		return radioWidth
	}
	if radioWidth > 0 && w > radioWidth {
		return radioWidth
	}
	return w
}

// modeWidthRe matches the width at the end of a driver phy-mode token: HT40PLUS, VHT80_80,
// HE160, EHT320. A legacy token (11A, 11B, 11G) has no width and yields 0.
var modeWidthRe = regexp.MustCompile(`(20|40|80|160|320)(_80)?(?:PLUS|MINUS)?$`)

func widthFromMode(mode string) int {
	m := modeWidthRe.FindStringSubmatch(strings.ToUpper(strings.TrimSpace(mode)))
	if m == nil {
		return 0
	}
	w, _ := strconv.Atoi(m[1])
	if m[2] != "" {
		return 160 // 80+80 is two 80 MHz segments
	}
	return w
}

// widthFromIndex decodes sta_table's chwidth, the Qualcomm width enum, to MHz. Measured against
// live clients and their rate ceilings: 0=20, 1=40, 2=80, 3=160, 5=320. Index 4 is 80+80, which
// no EHT radio runs; it and anything unknown yield 0 so the caller falls back.
func widthFromIndex(idx int) int {
	switch idx {
	case 0:
		return 20
	case 1:
		return 40
	case 2:
		return 80
	case 3:
		return 160
	case 5:
		return 320
	}
	return 0
}

func sortLinks(links []ClientLink) {
	sort.Slice(links, func(i, j int) bool {
		if links[i].Vap != links[j].Vap {
			return links[i].Vap < links[j].Vap
		}
		return links[i].MAC < links[j].MAC
	})
}

// markActiveLink picks the link the scalar fields report. Identity settles it where the AP gives
// identity, because only the active link of an MLO client carries hostname and ip; otherwise the
// link that has actually moved traffic wins, then the strongest signal.
func markActiveLink(links []ClientLink) {
	if len(links) == 0 {
		return
	}
	identity, identityCount, best := -1, 0, 0
	for i := range links {
		if links[i].HasIdentity {
			identity, identityCount = i, identityCount+1
		}
		if betterActive(links[i], links[best]) {
			best = i
		}
	}
	if identityCount == 1 {
		best = identity
	}
	links[best].Active = true
}

func betterActive(a, b ClientLink) bool {
	if a.Negotiated != b.Negotiated {
		return !a.Negotiated
	}
	// Idle before byte totals. A client that hops VAPs leaves its old station entry behind for
	// ~30 s carrying the whole session's byte count, so cumulative bytes elect the dead link and
	// flip back as the live one catches up - seen as the band flapping every poll. The link heard
	// from most recently is the one the client is on.
	if a.IdleSeconds != b.IdleSeconds {
		return a.IdleSeconds < b.IdleSeconds
	}
	if at, bt := a.TxBytes+a.RxBytes, b.TxBytes+b.RxBytes; at != bt {
		return at > bt
	}
	if a.Signal != nil && b.Signal != nil && *a.Signal != *b.Signal {
		return *a.Signal > *b.Signal
	}
	return a.Signal != nil && b.Signal == nil
}

func applyActiveLinkToClient(c *Client, l ClientLink) {
	c.Vap, c.Ssid, c.Bssid, c.Radio, c.Band = l.Vap, l.Ssid, l.Bssid, l.Radio, l.Band
	c.Channel, c.Bandwidth = l.Channel, l.Bandwidth
	c.Signal, c.Noise, c.SNR = l.Signal, l.Noise, l.SNR
	c.TxRateKbps, c.RxRateKbps = l.TxRateKbps, l.RxRateKbps
	c.TxRateMov, c.RxRateMov = l.TxRateMov, l.RxRateMov
	c.Satisfaction = l.Satisfaction
	c.Authorized = c.Authorized || l.Authorized
}

func mergeClientMeta(dst, src *Client) {
	if src.FirstSeenAt.Before(dst.FirstSeenAt) || dst.FirstSeenAt.IsZero() {
		dst.FirstSeenAt = src.FirstSeenAt
	}
	if src.LastSeenAt.After(dst.LastSeenAt) {
		dst.LastSeenAt = src.LastSeenAt
	}
	dst.Sources.Event = dst.Sources.Event || src.Sources.Event
	dst.Sources.Fast = dst.Sources.Fast || src.Sources.Fast
	dst.Sources.Slow = dst.Sources.Slow || src.Sources.Slow
	dst.IsMlo = dst.IsMlo || src.IsMlo
	if dst.MldMAC == "" {
		dst.MldMAC = src.MldMAC
	}
	if dst.Mlo == nil {
		dst.Mlo = src.Mlo
	}
	if dst.VlanID == 0 {
		dst.VlanID = src.VlanID
	}
	if dst.AnonClientID == "" {
		dst.AnonClientID = src.AnonClientID
	}
	if len(dst.SatisfactionSubscores) == 0 {
		dst.SatisfactionSubscores = src.SatisfactionSubscores
	}
	if dst.SatisfactionReal == nil {
		dst.SatisfactionReal = src.SatisfactionReal
	}
	if src.Anomalies > dst.Anomalies {
		dst.Anomalies = src.Anomalies
	}
	dst.Capabilities = mergeCapabilities(dst.Capabilities, src.Capabilities)
}

func mergeCapabilities(a, b ClientCapabilities) ClientCapabilities {
	return ClientCapabilities{
		Is11ax:    a.Is11ax || b.Is11ax,
		Is11be:    a.Is11be || b.Is11be,
		Is11ac:    a.Is11ac || b.Is11ac,
		Is11n:     a.Is11n || b.Is11n,
		Is11r:     a.Is11r || b.Is11r,
		IsMlo:     a.IsMlo || b.IsMlo,
		Nss:       max(a.Nss, b.Nss),
		BwMaxSupp: max(a.BwMaxSupp, b.BwMaxSupp),
	}
}

func ptrString(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

// Vaps returns the VAP table.
func (t *Table) Vaps() []VapState {
	t.mu.RLock()
	defer t.mu.RUnlock()
	return append([]VapState(nil), t.vaps...)
}

// Radios returns the radio table with its merged counters. The counter maps are copied rather than
// shared: the slow tier writes into them in place, and a handler encoding a shared map would race.
func (t *Table) Radios() []RadioState {
	t.mu.RLock()
	defer t.mu.RUnlock()

	out := make([]RadioState, 0, len(t.radios))
	for _, r := range t.radios {
		r.Counters = copyCounters(r.Counters)
		r.Deltas = copyCounters(r.Deltas)
		r.CounterSources = append([]string(nil), r.CounterSources...)
		out = append(out, r)
	}
	return out
}

func copyCounters(src map[string]int64) map[string]int64 {
	if src == nil {
		return nil
	}
	out := make(map[string]int64, len(src))
	for k, v := range src {
		out[k] = v
	}
	return out
}

// Scans returns what each radio hears, and when mca-dump was read: the entries' ages count from
// there, not from the request.
func (t *Table) Scans() ([]RadioScan, time.Time) {
	t.mu.RLock()
	defer t.mu.RUnlock()

	out := make([]RadioScan, 0, len(t.scans))
	for _, s := range t.scans {
		s.Scan = append([]ScanEntry{}, s.Scan...)
		s.Spectrum = append([]SpectrumEntry{}, s.Spectrum...)
		out = append(out, s)
	}
	return out, t.scansAt
}

func (t *Table) Ap() ApInfo {
	t.mu.RLock()
	defer t.mu.RUnlock()
	return t.ap
}

func (t *Table) Tiers() TierStatus {
	t.mu.RLock()
	defer t.mu.RUnlock()
	return t.tiers
}

// Size is the number of links held, which is what /health reports as the table's memory shape.
func (t *Table) Size() int {
	t.mu.RLock()
	defer t.mu.RUnlock()
	return len(t.members)
}

// ClientFilter is the /clients query. An empty field does not filter.
type ClientFilter struct {
	Band       string
	Ap         string
	Vap        string
	Ssid       string
	Authorized *bool
}

func (f ClientFilter) empty() bool {
	return f.Band == "" && f.Ap == "" && f.Vap == "" && f.Ssid == "" && f.Authorized == nil
}

// Applied is the echo of the filters a payload was built with, so a caller can tell an empty
// result from a filter that matched nothing it expected to.
func (f ClientFilter) Applied() map[string]string {
	if f.empty() {
		return nil
	}
	out := map[string]string{}
	if f.Band != "" {
		out["band"] = f.Band
	}
	if f.Ap != "" {
		out["ap"] = f.Ap
	}
	if f.Vap != "" {
		out["vap"] = f.Vap
	}
	if f.Ssid != "" {
		out["ssid"] = f.Ssid
	}
	if f.Authorized != nil {
		out["authorized"] = boolText(*f.Authorized)
	}
	return out
}

func boolText(b bool) string {
	if b {
		return "true"
	}
	return "false"
}

// normalizeBand accepts either mca-dump's radio token or the band a person would type.
func normalizeBand(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "ng", "2.4", "2.4ghz", "2g", "24":
		return "2.4"
	case "na", "5", "5ghz", "5g":
		return "5"
	case "6e", "6", "6ghz", "6g":
		return "6"
	default:
		return strings.ToLower(strings.TrimSpace(s))
	}
}

// matchClient filters on ANY link rather than the active one: association is a per-link fact, so a
// VAP or band query must not hide a link the AP is actually holding.
func matchClient(c Client, f ClientFilter, ap ApInfo) bool {
	if f.Ap != "" && !matchesAp(ap, f.Ap) {
		return false
	}
	if f.Authorized != nil && c.Authorized != *f.Authorized {
		return false
	}
	if f.Band == "" && f.Vap == "" && f.Ssid == "" {
		return true
	}
	band := normalizeBand(f.Band)
	for _, l := range c.Links {
		if f.Band != "" && normalizeBand(l.Band) != band && normalizeBand(l.Radio) != band {
			continue
		}
		if f.Vap != "" && !strings.EqualFold(l.Vap, f.Vap) {
			continue
		}
		if f.Ssid != "" && !strings.EqualFold(l.Ssid, f.Ssid) {
			continue
		}
		return true
	}
	return false
}

func matchesAp(ap ApInfo, want string) bool {
	return strings.EqualFold(ap.Hostname, want) ||
		strings.EqualFold(ap.MAC, want) ||
		strings.EqualFold(ap.Model, want)
}

// FindClient resolves either the MLD MAC or any single link MAC, because a caller holding a
// per-link address from a packet capture has no way to know it is not the client's identity.
func FindClient(clients []Client, mac string) (Client, bool) {
	want := normalizeMAC(mac)
	for _, c := range clients {
		if normalizeMAC(c.Key) == want || normalizeMAC(c.MAC) == want {
			return c, true
		}
	}
	for _, c := range clients {
		for _, l := range c.Links {
			if normalizeMAC(l.MAC) == want {
				return c, true
			}
		}
	}
	return Client{}, false
}

// LinkAddrForVap reports the address hostapd on vap knows this client by. hostapd keys its station
// table per link, so an MLO client is not addressable there by its MLD MAC. Empty when there is
// nothing to substitute - not MLO, link unknown, or the address given is already the link one - so
// a caller can leave what it had untouched.
func (t *Table) LinkAddrForVap(mac, vap string) string {
	mac = strings.ToLower(strings.TrimSpace(mac))
	vap = strings.TrimSpace(vap)
	if mac == "" || vap == "" {
		return ""
	}

	for _, c := range t.Clients(time.Now()) {
		if !strings.EqualFold(c.MAC, mac) && !strings.EqualFold(c.MldMAC, mac) && !strings.EqualFold(c.Key, mac) {
			continue
		}
		for _, l := range c.Links {
			if strings.EqualFold(l.Vap, vap) && l.MAC != "" && !strings.EqualFold(l.MAC, mac) {
				return normalizeMAC(l.MAC)
			}
		}
	}
	return ""
}

// VapForClient reports which VAP currently holds a client, by link MAC or MLD MAC. Empty when the
// table has not seen it: an MLO client is keyed on its MLD, so either address resolves.
func (t *Table) VapForClient(mac string) string {
	mac = strings.ToLower(strings.TrimSpace(mac))
	if mac == "" {
		return ""
	}

	for _, c := range t.Clients(time.Now()) {
		if strings.EqualFold(c.MAC, mac) || strings.EqualFold(c.MldMAC, mac) || strings.EqualFold(c.Key, mac) {
			if c.Vap != "" {
				return c.Vap
			}
		}
		for _, l := range c.Links {
			if strings.EqualFold(l.MAC, mac) && l.Vap != "" {
				return l.Vap
			}
		}
	}
	return ""
}
