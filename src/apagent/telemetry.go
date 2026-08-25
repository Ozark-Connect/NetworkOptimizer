package main

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// ClientLink is one association. A non-MLO client has exactly one; a Wi-Fi 7 client has one per
// link, each under its own VAP with its own locally administered MAC.
type ClientLink struct {
	MAC          string     `json:"mac"`
	Vap          string     `json:"vap"`
	Ssid         string     `json:"ssid,omitempty"`
	Bssid        string     `json:"bssid,omitempty"`
	Radio        string     `json:"radio,omitempty"`
	Band         string     `json:"band,omitempty"`
	Channel      int        `json:"channel,omitempty"`
	Bandwidth    int        `json:"bw,omitempty"`
	Active       bool       `json:"active"`
	Negotiated   bool       `json:"negotiated_idle,omitempty"`
	Signal       *int       `json:"signal,omitempty"`
	Noise        *int       `json:"noise,omitempty"`
	SNR          *int       `json:"snr,omitempty"`
	MinSignal    *int       `json:"min_signal,omitempty"`
	MaxSignal    *int       `json:"max_signal,omitempty"`
	TxRateKbps   int64      `json:"tx_rate_kbps,omitempty"`
	RxRateKbps   int64      `json:"rx_rate_kbps,omitempty"`
	TxRateMov    int64      `json:"tx_rate_mov_kbps,omitempty"`
	RxRateMov    int64      `json:"rx_rate_mov_kbps,omitempty"`
	Nss          int        `json:"nss,omitempty"`
	TxNss        int        `json:"tx_nss,omitempty"`
	RxNss        int        `json:"rx_nss,omitempty"`
	Ccq          int        `json:"ccq,omitempty"`
	Mode         string     `json:"mode,omitempty"`
	TxBytes      int64      `json:"tx_bytes,omitempty"`
	RxBytes      int64      `json:"rx_bytes,omitempty"`
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
}

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
	mu       sync.RWMutex
	maxSize  int
	ttl      time.Duration
	members  map[string]*memberState
	fast     map[string]StaFast
	slow     map[string]StaSlow
	identity map[string]identityRecord
	vaps     []VapState
	radios   []RadioState
	ap       ApInfo
	tiers    TierStatus

	// prevCounters survives ApplySlow, which replaces the radio table wholesale. Without it the
	// delta window would reset on every pass and the CCA wedge would never be visible.
	prevCounters   map[string]map[string]int64
	prevCountersAt time.Time
}

func NewTable(maxSize int, ttl time.Duration) *Table {
	return &Table{
		maxSize:      maxSize,
		ttl:          ttl,
		members:      map[string]*memberState{},
		fast:         map[string]StaFast{},
		slow:         map[string]StaSlow{},
		identity:     map[string]identityRecord{},
		prevCounters: map[string]map[string]int64{},
	}
}

// ApplyEvent folds a pushed membership fact into the table. The event stream is authoritative for
// membership: an assoc adds the link immediately and a disassoc removes it immediately, neither
// waiting for a poll to notice.
func (t *Table) ApplyEvent(e Event) {
	if e.MAC == "" || e.Vap == "" {
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
		t.evictLocked()

	case EventDisassoc:
		delete(t.members, key)
		delete(t.fast, key)
		delete(t.slow, key)
	}
}

// ApplyFast folds one fast sweep in. A station present in a poll but with no assoc event is added:
// the agent starts fresh on every AP boot and was not running for associations that predate it.
// covered names the VAPs the sweep actually reached, which is what bounds expiry to them.
func (t *Table) ApplyFast(stations map[string]StaFast, covered map[string]bool, now time.Time) {
	t.mu.Lock()
	defer t.mu.Unlock()

	for key, s := range stations {
		t.fast[key] = s
		m, ok := t.members[key]
		if !ok {
			m = &memberState{Vap: s.Vap, MAC: s.MAC, Source: "poll", FirstSeen: now}
			t.members[key] = m
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
	if snap.Hostname != "" {
		t.ap.Hostname = snap.Hostname
	}
	if snap.Model != "" {
		t.ap.Model = snap.Model
	}
	if snap.Version != "" {
		t.ap.Firmware = snap.Version
	}

	t.slow = make(map[string]StaSlow, len(snap.Stations))
	for _, s := range snap.Stations {
		key := stationKey(s.Vap, s.MAC)
		t.slow[key] = s

		m, ok := t.members[key]
		if !ok {
			m = &memberState{Vap: s.Vap, MAC: s.MAC, Source: "poll", FirstSeen: now}
			t.members[key] = m
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

// expireLocked drops links that no tier has seen for the TTL. A disassoc the agent missed is the
// case this exists for, and it is what keeps the table bounded on a busy AP. Only VAPs a poll
// actually reached are expired: an unreachable tool is not evidence a client left.
func (t *Table) expireLocked(covered map[string]bool, now time.Time) {
	for key, m := range t.members {
		if !covered[m.Vap] || now.Sub(m.LastSeen) <= t.ttl {
			continue
		}
		delete(t.members, key)
		delete(t.fast, key)
		delete(t.slow, key)
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

		if f, ok := t.fast[key]; ok {
			applyFastToLink(&link, f)
			record.Sources.Fast = true
		}
		if s, ok := t.slow[key]; ok {
			applySlowToLink(&link, s)
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
