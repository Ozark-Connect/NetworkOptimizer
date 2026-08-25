package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"reflect"
	"strings"
	"testing"
	"time"
)

// The MACs, hostnames, SSIDs, and IPs in testdata/mca-dump.json are generic; every RF value,
// counter, and structural relationship in it is the measured shape from a live U7-Pro-Wall on
// firmware 8.7.11, including its Wi-Fi 7 client.
const (
	fixtureMldMAC   = "aa:bb:cc:00:00:01"
	fixtureLink6e   = "02:aa:bb:00:00:11"
	fixtureLink5    = "02:aa:bb:00:00:12"
	fixtureLink24   = "02:aa:bb:00:00:13"
	fixtureTabletMA = "02:00:00:0c:1b:2b"
)

func loadMcaFixture(t *testing.T) McaSnapshot {
	t.Helper()
	data, err := os.ReadFile("testdata/mca-dump.json")
	if err != nil {
		t.Fatal(err)
	}
	snap, err := parseMcaFull(data, time.Now().UTC())
	if err != nil {
		t.Fatal(err)
	}
	return snap
}

func findStation(t *testing.T, snap McaSnapshot, mac string) StaSlow {
	t.Helper()
	for _, s := range snap.Stations {
		if s.MAC == mac {
			return s
		}
	}
	t.Fatalf("station %s not in fixture", mac)
	return StaSlow{}
}

func TestParseRateKbps(t *testing.T) {
	cases := map[string]int64{
		"1200M": 1200000,
		"6.5M":  6500,
		"2402M": 2402000,
		"1.2G":  1200000,
		"600K":  600,
		"0":     0,
		"":      0,
		"-":     0,
		"abc":   0,
	}
	for tok, want := range cases {
		if got := parseRateKbps(tok); got != want {
			t.Errorf("parseRateKbps(%q) = %d, want %d", tok, got, want)
		}
	}
}

func TestParseAssocTime(t *testing.T) {
	cases := map[string]int{
		"00:12:34": 754,
		"12:34":    754,
		"01:00:00": 3600,
		"garbage":  0,
		"":         0,
	}
	for tok, want := range cases {
		if got := parseAssocTime(tok); got != want {
			t.Errorf("parseAssocTime(%q) = %d, want %d", tok, got, want)
		}
	}
}

// A deliberately different column set from the measured one, which is the portability property
// that matters: the column set drifts between firmwares and mapping is by name, not position.
// The measured 8.7.11 header is exercised in captures_test.go against the real capture.
func TestParseWlanconfigStations(t *testing.T) {
	out := "ADDR               AID CHAN TXRATE RXRATE RSSI MINRSSI MAXRSSI IDLE  TXSEQ  RXSEQ  CAPS ACAPS ERP    STATE MAXRATE(DOT11) HTCAPS ASSOCTIME    IEs   MODE PSMODE RXNSS TXNSS\n" +
		"aa:bb:cc:dd:ee:ff    1  149  1200M  1080M   -54      -60     -48    0      0      0   EPS    0    0        0              0     A     00:12:34    WME  11BE     0     2     2\n" +
		"AA:BB:CC:DD:EE:FF    2   36   300M   270M   -71      -80     -65   12      0      0   EPS    0    0        0              0     A     01:00:00    WME  11AC     0     1     1\n"

	now := time.Now().UTC()
	stations := parseWlanconfigStations("wifi2ap10", out, now)
	if len(stations) != 2 {
		t.Fatalf("got %d stations, want 2", len(stations))
	}

	first := stations[0]
	if first.MAC != "aa:bb:cc:dd:ee:ff" || first.Vap != "wifi2ap10" {
		t.Errorf("unexpected identity: %+v", first)
	}
	if first.Channel != 149 || first.TxRateKbps != 1200000 || first.RxRateKbps != 1080000 {
		t.Errorf("rates and channel wrong: %+v", first)
	}
	if first.AssocSeconds != 754 || first.IdleSeconds != 0 {
		t.Errorf("timings wrong: %+v", first)
	}
	if first.Mode != "11BE" || first.TxNss != 2 || first.RxNss != 2 {
		t.Errorf("mode and NSS wrong: %+v", first)
	}
	// The RSSI columns read dBm on measured firmware, so they land on signal, not SNR.
	if first.Signal == nil || *first.Signal != -54 {
		t.Errorf("signal = %v, want -54", first.Signal)
	}
	if first.SNR != nil {
		t.Errorf("a negative RSSI column must not be reported as SNR, got %v", first.SNR)
	}
	if first.MinSignal == nil || *first.MinSignal != -60 || first.MaxSignal == nil || *first.MaxSignal != -48 {
		t.Errorf("min/max signal wrong: %+v", first)
	}
	// An uppercase MAC from one tool must key the same as a lowercase one from another.
	if stations[1].MAC != "aa:bb:cc:dd:ee:ff" {
		t.Errorf("MAC not normalized: %q", stations[1].MAC)
	}

	if got := parseWlanconfigStations("wifi0ap0", "wlanconfig: ioctl: No such device\n", now); len(got) != 0 {
		t.Errorf("an error message must yield no stations, got %v", got)
	}
}

// A positive RSSI column is SNR above the noise floor and must never be served as a dBm signal.
func TestWlanconfigPositiveRssiIsSnr(t *testing.T) {
	out := "ADDR               AID CHAN TXRATE RXRATE RSSI IDLE MODE\n" +
		"aa:bb:cc:dd:ee:ff    1  149  1200M  1080M   38    0 11BE\n"
	stations := parseWlanconfigStations("wifi2ap10", out, time.Now().UTC())
	if len(stations) != 1 {
		t.Fatalf("got %d stations, want 1", len(stations))
	}
	if stations[0].Signal != nil {
		t.Errorf("a positive RSSI column must not become a dBm signal, got %v", *stations[0].Signal)
	}
	if stations[0].SNR == nil || *stations[0].SNR != 38 {
		t.Errorf("snr = %v, want 38", stations[0].SNR)
	}
}

// Columns are mapped by header name, so a reordered or shortened header must still parse.
func TestParseWlanconfigStationsMapsByName(t *testing.T) {
	out := "ADDR               MODE TXRATE RSSI CHAN\n" +
		"aa:bb:cc:dd:ee:ff  11AX  866M   -60  36\n"
	stations := parseWlanconfigStations("wifi1ap5", out, time.Now().UTC())
	if len(stations) != 1 {
		t.Fatalf("got %d stations, want 1", len(stations))
	}
	s := stations[0]
	if s.Mode != "11AX" || s.TxRateKbps != 866000 || s.Channel != 36 {
		t.Errorf("columns not mapped by name: %+v", s)
	}
	if s.Signal == nil || *s.Signal != -60 {
		t.Errorf("signal = %v, want -60", s.Signal)
	}
}

func TestParseMcaFullShape(t *testing.T) {
	snap := loadMcaFixture(t)

	// Four radios, not three: the dedicated scan radio is in scan_radio_table rather than
	// radio_table, and reading only radio_table drops it.
	if len(snap.Radios) != 4 || len(snap.Vaps) != 8 || len(snap.Stations) != 7 {
		t.Fatalf("got %d radios, %d VAPs, %d stations; want 4, 8, 7",
			len(snap.Radios), len(snap.Vaps), len(snap.Stations))
	}
	scan := 0
	for _, r := range snap.Radios {
		if r.ScanRadio {
			scan++
			if r.Name != "wifi3" || r.Radio != "scan" {
				t.Errorf("unexpected scan radio: %+v", r)
			}
		}
	}
	if scan != 1 {
		t.Errorf("%d radios marked as the scan radio, want 1", scan)
	}
	if snap.Hostname != "test-ap" || snap.Version == "" {
		t.Errorf("AP identity not parsed: %+v", snap)
	}

	bands := map[string]string{}
	for _, v := range snap.Vaps {
		bands[v.Name] = v.Band
	}
	for vap, want := range map[string]string{"wifi0ap0": "2.4", "wifi1ap2": "5", "wifi2ap5": "6"} {
		if bands[vap] != want {
			t.Errorf("%s band = %q, want %q", vap, bands[vap], want)
		}
	}

	if _, err := parseMcaFull([]byte(`{"vap_table":[]}`), time.Now()); err == nil {
		t.Error("a document with no radio_table must fail the shape check")
	}
	if _, err := parseMcaFull([]byte("not json"), time.Now()); err == nil {
		t.Error("non-JSON must fail the shape check")
	}
}

// signal is dBm and rssi is SNR above the noise floor. Both are plausible integers, so a wrong
// choice would produce believable and wrong output rather than an error.
func TestMcaSignalIsNotRssi(t *testing.T) {
	snap := loadMcaFixture(t)
	s := findStation(t, snap, fixtureLink6e)

	if s.Signal == nil || *s.Signal != -61 {
		t.Errorf("signal = %v, want -61 dBm", s.Signal)
	}
	if s.RSSI == nil || *s.RSSI != 33 {
		t.Errorf("rssi = %v, want 33 (SNR above the noise floor)", s.RSSI)
	}
	if s.Noise == nil || *s.Noise != -94 {
		t.Errorf("noise = %v, want -94", s.Noise)
	}
}

// Rates are kbps: tx_rate 2402000 is 2402 Mbps, not 2402 kbps and not bps.
func TestMcaRatesAreKbps(t *testing.T) {
	snap := loadMcaFixture(t)
	s := findStation(t, snap, fixtureLink6e)

	if s.TxRate != 2402000 || s.RxRate != 1921600 {
		t.Errorf("rates = tx %d rx %d, want 2402000 and 1921600 kbps", s.TxRate, s.RxRate)
	}
	if s.RxRateMov != 1617300 {
		t.Errorf("rx_rate_mov = %d, want 1617300 (the moving average, not the instantaneous rate)", s.RxRateMov)
	}
}

func TestMcaCarriesQualityFields(t *testing.T) {
	snap := loadMcaFixture(t)
	s := findStation(t, snap, fixtureLink6e)

	if s.TxRetries != 11 || s.TxCombinedRetries != 11 {
		t.Errorf("retries = %d / %d, want 11", s.TxRetries, s.TxCombinedRetries)
	}
	if s.WifiTxLatencyMov == nil || s.WifiTxLatencyMov.Max != 3000 {
		t.Errorf("tx latency not parsed: %+v", s.WifiTxLatencyMov)
	}
	if s.TxTcpStats == nil || s.TxTcpStats.LatAvg != -1 {
		t.Errorf("tcp stats not parsed: %+v", s.TxTcpStats)
	}
	if len(s.SatisfactionSubscores) != 16 {
		t.Errorf("satisfaction_subscores = %d entries, want 16", len(s.SatisfactionSubscores))
	}
	if !s.Is11be || !s.Is11ax || !s.IsMlo || s.Nss != 2 {
		t.Errorf("capability fields wrong: %+v", s)
	}
}

// A non-MLO client carries mld_mac as JSON null, which must not become an empty-string key.
func TestMcaNonMloHasNoMldMac(t *testing.T) {
	snap := loadMcaFixture(t)
	s := findStation(t, snap, fixtureTabletMA)

	if s.MldMAC != nil {
		t.Errorf("mld_mac = %q, want nil on a non-MLO client", *s.MldMAC)
	}
	if s.IsMlo {
		t.Error("a non-MLO client must not read as MLO")
	}
	if clientKeyFor(s) != fixtureTabletMA {
		t.Errorf("key = %q, want the link MAC when there is no MLD", clientKeyFor(s))
	}
}

func TestMcaRadioAthstatsCounters(t *testing.T) {
	snap := loadMcaFixture(t)
	for _, r := range snap.Radios {
		if r.ScanRadio {
			// The scan radio carries no athstats block, which must read as absent rather than zero.
			if len(r.Counters) != 0 || r.NoiseFloor != nil {
				t.Errorf("scan radio %s must not invent counters: %+v", r.Name, r.Counters)
			}
			continue
		}
		for _, want := range []string{"cu_total", "cu_interf", "cu_self_tx", "cu_self_rx"} {
			if _, ok := r.Counters[want]; !ok {
				t.Errorf("radio %s is missing counter %q", r.Name, want)
			}
		}
		// athstats mixes a name string in with the counters; only the numeric members are kept.
		if _, ok := r.Counters["name"]; ok {
			t.Errorf("radio %s kept a non-numeric athstats member", r.Name)
		}
		if r.NoiseFloor == nil {
			t.Errorf("radio %s has no noise floor", r.Name)
		}
	}
}

// Channel and bandwidth are not on radio_table; they come from a VAP that is up on the radio.
func TestRadioChannelComesFromVap(t *testing.T) {
	snap := loadMcaFixture(t)
	// The scan radio takes its channel from the scan VAP that is up on it, like any other.
	want := map[string]int{"wifi0": 1, "wifi1": 128, "wifi2": 85, "wifi3": 81}
	for _, r := range snap.Radios {
		if r.Channel != want[r.Name] {
			t.Errorf("radio %s channel = %d, want %d", r.Name, r.Channel, want[r.Name])
		}
	}
}

func newFixtureTable(t *testing.T) (*Table, McaSnapshot) {
	t.Helper()
	snap := loadMcaFixture(t)
	table := NewTable(defaultMaxTrackedClients, defaultClientTTLSeconds*time.Second)
	table.ApplySlow(snap, time.Now().UTC())
	return table, snap
}

func findClient(t *testing.T, clients []Client, key string) Client {
	t.Helper()
	for _, c := range clients {
		if c.Key == key {
			return c
		}
	}
	t.Fatalf("client %s not found in %d clients", key, len(clients))
	return Client{}
}

// One Wi-Fi 7 device is one client. Keying on the link MAC would invent three.
func TestMloLinksMergeIntoOneClient(t *testing.T) {
	table, _ := newFixtureTable(t)
	clients := table.Clients(time.Now().UTC())

	if len(clients) != 5 {
		t.Fatalf("got %d clients from 7 stations, want 5 (three of them are one MLO device)", len(clients))
	}
	phone := findClient(t, clients, fixtureMldMAC)
	if phone.LinkCount != 3 {
		t.Errorf("link count = %d, want 3", phone.LinkCount)
	}
	if phone.MAC != fixtureMldMAC || phone.MldMAC != fixtureMldMAC || !phone.IsMlo {
		t.Errorf("MLO identity wrong: %+v", phone)
	}

	bands := map[string]bool{}
	for _, l := range phone.Links {
		bands[l.Band] = true
		if l.MAC == fixtureMldMAC {
			t.Error("a link must keep its own locally administered MAC, not the MLD MAC")
		}
	}
	for _, want := range []string{"2.4", "5", "6"} {
		if !bands[want] {
			t.Errorf("no link on the %s GHz band: %+v", want, phone.Links)
		}
	}
}

// The links span 56 dB. Taking an arbitrary one reads a healthy client as dying.
func TestScalarsReportTheActiveLink(t *testing.T) {
	table, _ := newFixtureTable(t)
	phone := findClient(t, table.Clients(time.Now().UTC()), fixtureMldMAC)

	if phone.Signal == nil || *phone.Signal != -61 {
		t.Fatalf("signal = %v, want -61 (the active 6 GHz link, not -95 or -39)", phone.Signal)
	}
	if phone.Band != "6" || phone.Vap != "wifi2ap5" || phone.Channel != 85 || phone.Bandwidth != 160 {
		t.Errorf("scalars are not the active link's: band %q vap %q ch %d bw %d",
			phone.Band, phone.Vap, phone.Channel, phone.Bandwidth)
	}
	if phone.TxRateKbps != 2402000 {
		t.Errorf("tx rate = %d, want the active link's 2402000 kbps", phone.TxRateKbps)
	}

	active := 0
	for _, l := range phone.Links {
		if l.Active {
			active++
			if l.MAC != fixtureLink6e {
				t.Errorf("active link is %s, want the 6 GHz link %s", l.MAC, fixtureLink6e)
			}
		}
	}
	if active != 1 {
		t.Errorf("%d links marked active, want exactly 1", active)
	}
}

// Only the active link carries hostname and ip; identity resolves once on the MLD.
func TestIdentityResolvesOnTheMld(t *testing.T) {
	table, _ := newFixtureTable(t)
	phone := findClient(t, table.Clients(time.Now().UTC()), fixtureMldMAC)

	if phone.Hostname != "TestPhone" || phone.IP != "192.0.2.10" {
		t.Errorf("identity = %q / %q, want the active link's", phone.Hostname, phone.IP)
	}
	if phone.IdentityAt == nil {
		t.Error("identity must carry the time it was collected")
	}
	if len(phone.IPv6) != 2 {
		t.Errorf("ipv6 addresses = %d, want 2", len(phone.IPv6))
	}
}

// Both idle links have idletime == uptime, which is a negotiated link that has never carried
// traffic rather than a client in trouble.
func TestIdleLinksAreMarkedNegotiated(t *testing.T) {
	table, _ := newFixtureTable(t)
	phone := findClient(t, table.Clients(time.Now().UTC()), fixtureMldMAC)

	negotiated := 0
	for _, l := range phone.Links {
		if l.Negotiated {
			negotiated++
			if l.Active {
				t.Errorf("a negotiated-idle link must not be the active one: %+v", l)
			}
		}
	}
	if negotiated != 2 {
		t.Errorf("%d negotiated-idle links, want 2", negotiated)
	}
}

// mlo.num_links reported 2 while three entries existed, so the link count comes from the entries.
func TestMloNumLinksIsNotTheLinkCount(t *testing.T) {
	table, _ := newFixtureTable(t)
	phone := findClient(t, table.Clients(time.Now().UTC()), fixtureMldMAC)

	if phone.Mlo == nil {
		t.Fatal("the MLO block must be carried through")
	}
	if phone.Mlo.NumLinks == phone.LinkCount {
		t.Skip("this AP now agrees with itself, so the trap cannot be asserted from this fixture")
	}
	if phone.LinkCount != 3 || phone.Mlo.NumLinks != 2 {
		t.Errorf("link count %d and num_links %d, want 3 and 2", phone.LinkCount, phone.Mlo.NumLinks)
	}
}

// Band, channel, SSID, and BSSID live on the VAP, so a client record has to be joined to it.
func TestBandAndChannelComeFromTheVap(t *testing.T) {
	table, _ := newFixtureTable(t)
	tablet := findClient(t, table.Clients(time.Now().UTC()), fixtureTabletMA)

	if tablet.Band != "5" || tablet.Channel != 128 || tablet.Bandwidth != 160 {
		t.Errorf("VAP context missing: band %q ch %d bw %d", tablet.Band, tablet.Channel, tablet.Bandwidth)
	}
	if tablet.Ssid != "TestNet" || tablet.Bssid == "" {
		t.Errorf("SSID and BSSID must come from the VAP: %q / %q", tablet.Ssid, tablet.Bssid)
	}
	if tablet.LinkCount != 1 {
		t.Errorf("a non-MLO client has %d links, want 1", tablet.LinkCount)
	}
}

func TestEventsAreAuthoritativeForMembership(t *testing.T) {
	table := NewTable(defaultMaxTrackedClients, time.Minute)
	now := time.Now().UTC()

	table.ApplyEvent(Event{Seq: 1, Type: EventAssoc, Vap: "wifi2ap5", MAC: fixtureLink6e, CollectedAt: now})
	clients := table.Clients(now)
	if len(clients) != 1 {
		t.Fatalf("an assoc event alone must produce a client, got %d", len(clients))
	}
	if clients[0].Links[0].Membership != "event" {
		t.Errorf("membership source = %q, want event", clients[0].Links[0].Membership)
	}
	if clients[0].Links[0].AssocEventAt == nil {
		t.Error("an event-sourced link must carry the assoc time")
	}
	if !clients[0].Sources.Event || clients[0].Sources.Fast || clients[0].Sources.Slow {
		t.Errorf("sources wrong: %+v", clients[0].Sources)
	}

	// A disassoc removes the client immediately rather than waiting for a poll to notice.
	table.ApplyEvent(Event{Seq: 2, Type: EventDisassoc, Vap: "wifi2ap5", MAC: fixtureLink6e, CollectedAt: now})
	if got := table.Clients(now); len(got) != 0 {
		t.Errorf("a disassoc must drop the client immediately, still have %d", len(got))
	}
}

// The agent starts fresh on every AP boot, so a client that associated before it started has no
// assoc event and must still appear.
func TestPollSeedsMembershipTheAgentMissed(t *testing.T) {
	table := NewTable(defaultMaxTrackedClients, time.Minute)
	now := time.Now().UTC()

	stations := map[string]StaFast{
		stationKey("wifi1ap5", fixtureTabletMA): {MAC: fixtureTabletMA, Vap: "wifi1ap5", CollectedAt: now},
	}
	table.ApplyFast(stations, map[string]bool{"wifi1ap5": true}, now)

	clients := table.Clients(now)
	if len(clients) != 1 || clients[0].Links[0].Membership != "poll" {
		t.Fatalf("a poll-only station must be tracked as poll-sourced, got %+v", clients)
	}
}

func TestExpiryOnlyAppliesToVapsAPollReached(t *testing.T) {
	table := NewTable(defaultMaxTrackedClients, 30*time.Second)
	start := time.Now().UTC()

	table.ApplyEvent(Event{Seq: 1, Type: EventAssoc, Vap: "wifi0ap0", MAC: fixtureLink24, CollectedAt: start})
	table.ApplyEvent(Event{Seq: 2, Type: EventAssoc, Vap: "wifi2ap5", MAC: fixtureLink6e, CollectedAt: start})

	// A sweep that only reached wifi0ap0 and found nothing there must expire that VAP's member and
	// leave the one on the VAP it could not read alone.
	later := start.Add(2 * time.Minute)
	table.ApplyFast(map[string]StaFast{}, map[string]bool{"wifi0ap0": true}, later)

	clients := table.Clients(later)
	if len(clients) != 1 {
		t.Fatalf("got %d clients, want 1", len(clients))
	}
	if clients[0].Links[0].Vap != "wifi2ap5" {
		t.Errorf("the surviving client is on %s, want wifi2ap5", clients[0].Links[0].Vap)
	}
}

func TestTableEvictsPastItsCap(t *testing.T) {
	table := NewTable(4, time.Hour)
	base := time.Now().UTC()

	for i := 0; i < 10; i++ {
		mac := macForIndex(i)
		table.ApplyEvent(Event{
			Seq: uint64(i + 1), Type: EventAssoc, Vap: "wifi0ap0", MAC: mac,
			CollectedAt: base.Add(time.Duration(i) * time.Second),
		})
	}
	if got := table.Size(); got != 4 {
		t.Errorf("table holds %d links, want the cap of 4", got)
	}
	// Eviction drops the least recently seen, so the newest associations survive.
	for _, c := range table.Clients(base.Add(time.Minute)) {
		if c.Key < macForIndex(6) {
			t.Errorf("evicted the wrong end: %s survived", c.Key)
		}
	}
}

func macForIndex(i int) string {
	const hex = "0123456789abcdef"
	return "02:00:00:00:00:" + string([]byte{hex[i/16], hex[i%16]})
}

// Without mca-dump there is no identity and no MLD MAC, and the fast tier's RF data must still
// serve rather than the endpoint failing.
func TestDegradesWithoutTheSlowTier(t *testing.T) {
	table := NewTable(defaultMaxTrackedClients, time.Minute)
	now := time.Now().UTC()
	signal := -58

	stations := map[string]StaFast{
		stationKey("wifi2ap5", fixtureLink6e): {
			MAC: fixtureLink6e, Vap: "wifi2ap5", Channel: 85, TxRateKbps: 2402000,
			Signal: &signal, Mode: "11BE", CollectedAt: now,
		},
	}
	table.ApplyFast(stations, map[string]bool{"wifi2ap5": true}, now)

	clients := table.Clients(now)
	if len(clients) != 1 {
		t.Fatalf("got %d clients, want 1", len(clients))
	}
	c := clients[0]
	if c.Signal == nil || *c.Signal != -58 || c.TxRateKbps != 2402000 {
		t.Errorf("fast-tier RF must survive without the slow tier: %+v", c)
	}
	if c.Hostname != "" || c.IP != "" || c.MldMAC != "" {
		t.Errorf("identity must be absent rather than invented: %+v", c)
	}
	if c.Sources.Slow || !c.Sources.Fast {
		t.Errorf("sources wrong: %+v", c.Sources)
	}
	// Without mca-dump the MLD MAC is unknowable, so the link MAC is the key and the record says so.
	if c.Key != fixtureLink6e {
		t.Errorf("key = %q, want the link MAC when there is no slow tier", c.Key)
	}
}

// Without wlanconfig the slow tier alone must still serve a full record.
func TestDegradesWithoutTheFastTier(t *testing.T) {
	table, _ := newFixtureTable(t)
	phone := findClient(t, table.Clients(time.Now().UTC()), fixtureMldMAC)

	if phone.Signal == nil || *phone.Signal != -61 || phone.Hostname != "TestPhone" {
		t.Errorf("the slow tier alone must serve a full record: %+v", phone)
	}
	for _, l := range phone.Links {
		if l.FastAt != nil {
			t.Error("no fast-tier timestamp should be claimed when the tier never ran")
		}
		if l.SlowAt == nil {
			t.Error("a slow-tier link must carry its collection time")
		}
	}
}

func TestParseHostapdEvent(t *testing.T) {
	now := time.Now().UTC()

	e, ok := parseHostapdEvent("wifi2ap10", "<3>AP-STA-CONNECTED aa:bb:cc:dd:ee:ff", now)
	if !ok || e.Type != EventAssoc || e.MAC != "aa:bb:cc:dd:ee:ff" || e.Vap != "wifi2ap10" {
		t.Errorf("assoc not parsed: %+v ok=%v", e, ok)
	}
	if e.EventTime != nil {
		t.Error("the control socket carries no event time of its own, so none must be claimed")
	}
	if e.CollectedAt != now {
		t.Error("every event must carry its observation time")
	}

	e, ok = parseHostapdEvent("wifi1ap5", "AP-STA-DISCONNECTED aa:bb:cc:dd:ee:ff", now)
	if !ok || e.Type != EventDisassoc || e.MAC != "aa:bb:cc:dd:ee:ff" {
		t.Errorf("disassoc not parsed: %+v ok=%v", e, ok)
	}

	e, ok = parseHostapdEvent("wifi2ap10",
		"<3>STA aa:bb:cc:dd:ee:ff WPA: UBNT_ROAM: STA=aa:bb:cc:dd:ee:ff associated_ap=aa:bb:cc:00:11:22, broadcasting roam=1", now)
	if !ok || e.Type != EventRoamBroadcast || e.PeerBssid != "aa:bb:cc:00:11:22" {
		t.Errorf("roam broadcast not parsed: %+v ok=%v", e, ok)
	}

	e, ok = parseHostapdEvent("wifi2ap10",
		"STA aa:bb:cc:dd:ee:ff WPA: UBNT_ROAM received: STA roamed to peer AP aa:bb:cc:00:11:22", now)
	if !ok || e.Type != EventRoamToPeer {
		t.Errorf("peer roam not parsed: %+v ok=%v", e, ok)
	}
	if e.MAC != "aa:bb:cc:dd:ee:ff" || e.PeerBssid != "aa:bb:cc:00:11:22" {
		t.Errorf("peer roam addresses wrong: mac %q peer %q", e.MAC, e.PeerBssid)
	}

	for _, line := range []string{"OK", "PONG", "", "<3>CTRL-EVENT-SCAN-STARTED", "AP-STA-CONNECTED not-a-mac"} {
		if _, ok := parseHostapdEvent("wifi0ap0", line, now); ok {
			t.Errorf("line %q must not become an event", line)
		}
	}
	for _, reply := range []string{"OK", "PONG", "FAIL"} {
		if !isControlReply(reply) {
			t.Errorf("%q must read as a control reply", reply)
		}
	}
}

func TestEventRingReplay(t *testing.T) {
	ring := NewEventRing(4)
	now := time.Now().UTC()

	for i := 0; i < 3; i++ {
		ring.Add(Event{Type: EventAssoc, Vap: "wifi0ap0", MAC: macForIndex(i), CollectedAt: now})
	}
	events, truncated := ring.Since(1)
	if len(events) != 2 || truncated {
		t.Fatalf("Since(1) = %d events truncated=%v, want 2 and false", len(events), truncated)
	}
	if events[0].Seq != 2 || events[1].Seq != 3 {
		t.Errorf("sequence numbers wrong: %+v", events)
	}

	all, _ := ring.Since(0)
	if len(all) != 3 {
		t.Errorf("Since(0) = %d, want the whole window", len(all))
	}
	if got, _ := ring.Since(3); len(got) != 0 {
		t.Errorf("Since(newest) = %d, want nothing new", len(got))
	}
}

// A collector that was away longer than the window must be told, rather than believing nothing
// happened while it was gone.
func TestEventRingReportsTruncation(t *testing.T) {
	ring := NewEventRing(4)
	now := time.Now().UTC()

	for i := 0; i < 10; i++ {
		ring.Add(Event{Type: EventAssoc, Vap: "wifi0ap0", MAC: macForIndex(i), CollectedAt: now})
	}
	stats := ring.Stats()
	if stats.Retained != 4 || stats.Capacity != 4 || stats.Received != 10 || stats.Dropped != 6 {
		t.Fatalf("ring stats = %+v", stats)
	}
	if stats.OldestSeq != 7 || stats.NewestSeq != 10 {
		t.Errorf("retained window = %d to %d, want 7 to 10", stats.OldestSeq, stats.NewestSeq)
	}

	events, truncated := ring.Since(2)
	if !truncated {
		t.Error("asking for a window that has been overwritten must report truncation")
	}
	if len(events) != 4 {
		t.Errorf("got %d events, want the 4 still retained", len(events))
	}
	if _, truncated := ring.Since(6); truncated {
		t.Error("asking from the edge of the retained window is not truncation")
	}
}

func TestEventRingSinceTime(t *testing.T) {
	ring := NewEventRing(8)
	base := time.Now().UTC()

	for i := 0; i < 5; i++ {
		ring.Add(Event{Type: EventAssoc, Vap: "wifi0ap0", MAC: macForIndex(i),
			CollectedAt: base.Add(time.Duration(i) * time.Second)})
	}
	events, _ := ring.SinceTime(base.Add(3 * time.Second))
	if len(events) != 2 {
		t.Errorf("SinceTime = %d events, want 2", len(events))
	}
}

func TestCounterDeltas(t *testing.T) {
	prev := map[string]int64{"cycle_cnt": 100, "pdev_resets": 2, "tx_frame_cnt": 40}
	cur := map[string]int64{"cycle_cnt": 350, "pdev_resets": 1, "tx_frame_cnt": 40}

	deltas := counterDeltas(prev, cur)
	if deltas["cycle_cnt"] != 250 {
		t.Errorf("cycle_cnt delta = %d, want 250", deltas["cycle_cnt"])
	}
	// A radio reset zeroes its counters, so a counter that went backwards yields no delta rather
	// than a huge negative one.
	if _, ok := deltas["pdev_resets"]; ok {
		t.Error("a counter that went backwards must not produce a delta")
	}
	// A zero tx delta is the CCA wedge signature, so it has to be reported rather than dropped.
	if v, ok := deltas["tx_frame_cnt"]; !ok || v != 0 {
		t.Errorf("tx_frame_cnt delta = %d present=%v, want 0 and present", v, ok)
	}
	if counterDeltas(nil, cur) != nil {
		t.Error("with no previous sample there is no delta")
	}
}

func TestRadioCountersUnionAndDeltas(t *testing.T) {
	table, snap := newFixtureTable(t)
	first := time.Now().UTC()

	table.SetRadioCounters(
		map[string]map[string]int64{"wifi2": {"pdev_resets": 0, "cycle_cnt": 1000}},
		map[string][]string{"wifi2": {"athstats"}},
		first,
	)
	radios := table.Radios()
	var wifi2 RadioState
	for _, r := range radios {
		if r.Name == "wifi2" {
			wifi2 = r
		}
	}
	// mca-dump's cu_* counters and the tool's own counters are a union, not a replacement.
	if wifi2.Counters["cu_total"] != 2 || wifi2.Counters["pdev_resets"] != 0 || wifi2.Counters["cycle_cnt"] != 1000 {
		t.Errorf("counters are not a union: %v", wifi2.Counters)
	}
	if !reflect.DeepEqual(wifi2.CounterSources, []string{"mca-dump", "athstats"}) {
		t.Errorf("counter sources = %v", wifi2.CounterSources)
	}

	// A second slow pass replaces the radio table, so the delta baseline has to survive it.
	second := first.Add(30 * time.Second)
	table.ApplySlow(snap, second)
	table.SetRadioCounters(
		map[string]map[string]int64{"wifi2": {"pdev_resets": 3, "cycle_cnt": 1500}},
		map[string][]string{"wifi2": {"athstats"}},
		second,
	)
	for _, r := range table.Radios() {
		if r.Name != "wifi2" {
			continue
		}
		if r.Deltas["pdev_resets"] != 3 || r.Deltas["cycle_cnt"] != 500 {
			t.Errorf("deltas = %v", r.Deltas)
		}
		if r.DeltaSeconds != 30 {
			t.Errorf("delta window = %v seconds, want 30", r.DeltaSeconds)
		}
	}
}

func TestNormalizeBand(t *testing.T) {
	cases := map[string]string{
		"ng": "2.4", "2.4": "2.4", "2.4GHz": "2.4",
		"na": "5", "5": "5", "5ghz": "5",
		"6e": "6", "6": "6", "6GHz": "6",
	}
	for in, want := range cases {
		if got := normalizeBand(in); got != want {
			t.Errorf("normalizeBand(%q) = %q, want %q", in, got, want)
		}
	}
}

func newTelemetryServer(t *testing.T) *httptest.Server {
	t.Helper()
	state := NewState(time.Now().UTC(), PlatformInfo{Machine: "armv7l", GOARCH: "arm"})
	state.SetProbes(ProbeSet{
		Results:  []ProbeResult{{Name: ProbeHostapdCtrl, Fatal: true, Available: true, CheckedAt: time.Now().UTC()}},
		Vaps:     []string{"wifi2ap5"},
		ProbedAt: time.Now().UTC(),
	})

	table, _ := newFixtureTable(t)
	table.SetApMAC("02:00:00:01:11:21")
	table.SetTiers(TierStatus{
		Events: TierInfo{Available: true},
		Fast:   TierInfo{Available: false, LastError: "wlanconfig: not found"},
		Slow:   TierInfo{Available: true, IntervalSeconds: 30, Runs: 1},
	})
	ring := NewEventRing(16)
	ring.Add(Event{Type: EventAssoc, Vap: "wifi2ap5", MAC: fixtureLink6e, CollectedAt: time.Now().UTC()})
	state.AttachTelemetry(table, ring, nil)

	srv := httptest.NewServer(authMiddleware(state, testToken, newMux(state)))
	t.Cleanup(srv.Close)
	return srv
}

func decodeJSON(t *testing.T, resp *http.Response, into any) {
	t.Helper()
	if err := json.NewDecoder(resp.Body).Decode(into); err != nil {
		t.Fatal(err)
	}
}

func TestClientsEndpoint(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	resp := doRequest(t, srv, http.MethodGet, "/clients", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/clients got %d", resp.StatusCode)
	}
	var payload ClientsPayload
	decodeJSON(t, resp, &payload)
	if payload.Count != 5 || len(payload.Clients) != 5 {
		t.Errorf("got %d clients, want 5", payload.Count)
	}
	if payload.CollectedAt.IsZero() {
		t.Error("every payload must carry collected_at")
	}
	if payload.Ap.Hostname != "test-ap" {
		t.Errorf("payload must identify the AP it came from: %+v", payload.Ap)
	}
	// Tier status is what tells a consumer which tiers were behind the answer, so a degraded tier
	// has to reach the payload rather than the request failing.
	if !payload.Sources.Slow.Available || payload.Sources.Slow.Runs != 1 {
		t.Errorf("slow tier status not reported: %+v", payload.Sources.Slow)
	}
	if payload.Sources.Fast.Available || payload.Sources.Fast.LastError == "" {
		t.Errorf("an unavailable tier must say so and why: %+v", payload.Sources.Fast)
	}
}

func TestClientsEndpointFilters(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	cases := []struct {
		query string
		want  int
	}{
		{"?band=6e", 1},
		{"?band=6", 1},
		{"?band=2.4", 4},
		{"?band=ng", 4},
		{"?vap=wifi2ap5", 1},
		{"?ssid=TestNet", 3},
		{"?ssid=TestNet-IoT", 2},
		{"?authorized=true", 5},
		{"?authorized=false", 0},
		{"?ap=test-ap", 5},
		{"?ap=some-other-ap", 0},
		{"?band=6&ssid=TestNet", 1},
	}
	for _, c := range cases {
		resp := doRequest(t, srv, http.MethodGet, "/clients"+c.query, auth)
		if resp.StatusCode != http.StatusOK {
			t.Errorf("/clients%s got %d", c.query, resp.StatusCode)
			continue
		}
		var payload ClientsPayload
		decodeJSON(t, resp, &payload)
		if payload.Count != c.want {
			t.Errorf("/clients%s = %d clients, want %d", c.query, payload.Count, c.want)
		}
		if len(payload.Filters) == 0 {
			t.Errorf("/clients%s must echo the filters it applied", c.query)
		}
	}

	// An unknown filter is refused: serving every client to a caller that asked for one band is
	// the worse failure.
	resp := doRequest(t, srv, http.MethodGet, "/clients?radio=6e", auth)
	if resp.StatusCode != http.StatusBadRequest {
		t.Errorf("an unknown filter got %d, want 400", resp.StatusCode)
	}
	if resp := doRequest(t, srv, http.MethodGet, "/clients?authorized=maybe", auth); resp.StatusCode != http.StatusBadRequest {
		t.Errorf("a non-boolean authorized got %d, want 400", resp.StatusCode)
	}
}

// A caller holding a per-link MAC has no way to know it is not the client's identity, so both
// resolve to the same record.
func TestClientEndpointResolvesLinkOrMldMac(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	for _, mac := range []string{fixtureMldMAC, fixtureLink6e, fixtureLink5, fixtureLink24, strings.ToUpper(fixtureLink6e)} {
		resp := doRequest(t, srv, http.MethodGet, "/clients/"+mac, auth)
		if resp.StatusCode != http.StatusOK {
			t.Fatalf("/clients/%s got %d", mac, resp.StatusCode)
		}
		var payload ClientPayload
		decodeJSON(t, resp, &payload)
		if payload.Client.Key != fixtureMldMAC {
			t.Errorf("/clients/%s resolved to %s, want the MLD %s", mac, payload.Client.Key, fixtureMldMAC)
		}
		if payload.Client.LinkCount != 3 {
			t.Errorf("/clients/%s returned %d links, want 3", mac, payload.Client.LinkCount)
		}
	}

	if resp := doRequest(t, srv, http.MethodGet, "/clients/00:00:00:00:00:99", auth); resp.StatusCode != http.StatusNotFound {
		t.Errorf("an unknown MAC got %d, want 404", resp.StatusCode)
	}
	// The router rejects an entity path with no id before the handler sees it, which is the right
	// answer for a path that names no resource.
	if resp := doRequest(t, srv, http.MethodGet, "/clients/", auth); resp.StatusCode != http.StatusNotFound {
		t.Errorf("an empty MAC got %d, want 404", resp.StatusCode)
	}
}

func TestVapsAndRadiosEndpoints(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	resp := doRequest(t, srv, http.MethodGet, "/vaps", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/vaps got %d", resp.StatusCode)
	}
	var vaps VapsPayload
	decodeJSON(t, resp, &vaps)
	if vaps.Count != 8 {
		t.Errorf("/vaps returned %d, want 8", vaps.Count)
	}
	for _, v := range vaps.Vaps {
		if v.Name == "wifi2ap5" {
			if v.Channel != 85 || v.Bandwidth != 160 || v.Essid != "TestNet" || v.Bssid == "" || v.NumSta != 1 {
				t.Errorf("VAP record incomplete: %+v", v)
			}
		}
	}

	resp = doRequest(t, srv, http.MethodGet, "/radios", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/radios got %d", resp.StatusCode)
	}
	var radios RadiosPayload
	decodeJSON(t, resp, &radios)
	if radios.Count != 4 {
		t.Fatalf("/radios returned %d, want 4 (three serving plus the scan radio)", radios.Count)
	}
	for _, r := range radios.Radios {
		if r.ScanRadio {
			continue
		}
		if len(r.Counters) == 0 || r.Counters["cu_total"] < 0 {
			t.Errorf("radio %s has no counters", r.Name)
		}
		if r.Band == "" {
			t.Errorf("radio %s has no band", r.Name)
		}
	}
}

func TestEventsEndpoint(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	resp := doRequest(t, srv, http.MethodGet, "/events", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/events got %d", resp.StatusCode)
	}
	var payload EventsPayload
	decodeJSON(t, resp, &payload)
	if payload.Count != 1 || payload.Events[0].Type != EventAssoc {
		t.Fatalf("unexpected events: %+v", payload)
	}
	if payload.AgentStartedAt.IsZero() {
		t.Error("the reply must carry the agent start time so a collector can spot a restart")
	}
	if payload.Ring.Capacity != 16 {
		t.Errorf("ring capacity = %d, want 16", payload.Ring.Capacity)
	}

	resp = doRequest(t, srv, http.MethodGet, "/events?since=1", auth)
	decodeJSON(t, resp, &payload)
	if payload.Count != 0 || payload.Truncated {
		t.Errorf("since the newest sequence must return nothing: %+v", payload)
	}

	since := time.Now().UTC().Add(-time.Hour).Format(time.RFC3339)
	resp = doRequest(t, srv, http.MethodGet, "/events?since="+since, auth)
	decodeJSON(t, resp, &payload)
	if payload.Count != 1 {
		t.Errorf("an RFC3339 since returned %d events, want 1", payload.Count)
	}

	if resp := doRequest(t, srv, http.MethodGet, "/events?since=yesterday", auth); resp.StatusCode != http.StatusBadRequest {
		t.Errorf("an unparsable since got %d, want 400", resp.StatusCode)
	}
}

func TestTelemetryEndpointsRequireAuth(t *testing.T) {
	srv := newTelemetryServer(t)
	for _, path := range []string{"/clients", "/clients/" + fixtureMldMAC, "/vaps", "/radios", "/events"} {
		if resp := doRequest(t, srv, http.MethodGet, path, ""); resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("%s unauthenticated got %d, want 401", path, resp.StatusCode)
		}
	}
}

// A request reads the in-memory table and never triggers a collection: N pollers would otherwise
// cost N times the collection.
func TestRequestsDoNotCollect(t *testing.T) {
	srv := newTelemetryServer(t)
	auth := "Bearer " + testToken

	var before ClientsPayload
	decodeJSON(t, doRequest(t, srv, http.MethodGet, "/clients", auth), &before)

	for i := 0; i < 20; i++ {
		doRequest(t, srv, http.MethodGet, "/clients", auth)
		doRequest(t, srv, http.MethodGet, "/radios", auth)
	}

	var after ClientsPayload
	decodeJSON(t, doRequest(t, srv, http.MethodGet, "/clients", auth), &after)
	if after.Sources.Slow.Runs != before.Sources.Slow.Runs || after.Sources.Fast.Runs != before.Sources.Fast.Runs {
		t.Errorf("a request ran a collection: fast %d -> %d, slow %d -> %d",
			before.Sources.Fast.Runs, after.Sources.Fast.Runs,
			before.Sources.Slow.Runs, after.Sources.Slow.Runs)
	}
}

func TestConfigRefusesAnOutOfRangeCadence(t *testing.T) {
	base := Config{Token: "0123456789abcdef", Port: defaultPort, ListenInterface: "br0"}

	tooFast := base
	tooFast.FastIntervalMs = 10
	tooFast.SlowIntervalSeconds = defaultSlowIntervalSeconds
	tooFast.ClientTTLSeconds = defaultClientTTLSeconds
	if err := validateConfig(&tooFast); err == nil {
		t.Error("a 10 ms fast interval must be refused rather than clamped")
	}

	tooShortTTL := base
	tooShortTTL.FastIntervalMs = defaultFastIntervalMs
	tooShortTTL.SlowIntervalSeconds = 60
	tooShortTTL.ClientTTLSeconds = 30
	if err := validateConfig(&tooShortTTL); err == nil {
		t.Error("a TTL below the slow interval would expire clients between passes")
	}

	ok := base
	applyDefaults(&ok)
	if err := validateConfig(&ok); err != nil {
		t.Errorf("the defaults must validate: %v", err)
	}
}

// A provision cycle renames VAPs under a running agent, so the listener set is reconciled rather
// than fixed at startup. There is no hostapd here, so every listener is in its reconnect backoff:
// what this asserts is the bookkeeping and that a cancel stops them all.
func TestEventSourceReconcilesListeners(t *testing.T) {
	ring := NewEventRing(8)
	source := NewEventSource(t.TempDir(), ring, nil)

	ctx, cancel := context.WithCancel(context.Background())
	source.Reconcile(ctx, []string{"wifi0ap0", "wifi1ap5", "wifi2ap10"})
	if got := source.listenerCount(); got != 3 {
		t.Fatalf("%d listeners, want 3", got)
	}

	source.Reconcile(ctx, []string{"wifi0ap0", "wifi2ap11"})
	if got := source.listenerCount(); got != 2 {
		t.Errorf("%d listeners after a VAP set change, want 2", got)
	}
	// Nothing answered, so nothing may claim to be attached.
	if got := source.Attached(); len(got) != 0 {
		t.Errorf("attached = %v, want none without a hostapd", got)
	}

	cancel()
	source.Wait()
	if got := source.listenerCount(); got != 2 {
		t.Errorf("cancel must stop the goroutines without rewriting the set, got %d", got)
	}
}

// An event shape that appears on new firmware must be visible in the diagnostics rather than
// silently discarded, and the tally must stay bounded whatever arrives.
func TestEventSourceTalliesUnknownLinesWithinABound(t *testing.T) {
	source := NewEventSource(t.TempDir(), NewEventRing(8), nil)

	source.noteUnknown("<3>CTRL-EVENT-SCAN-STARTED ")
	source.noteUnknown("<3>CTRL-EVENT-SCAN-STARTED ")
	for i := 0; i < maxUnknownEventKinds*3; i++ {
		source.noteUnknown("KIND-" + macForIndex(i%256) + " payload")
	}

	kinds := source.UnknownKinds()
	if kinds["CTRL-EVENT-SCAN-STARTED"] != 2 {
		t.Errorf("unknown kind tally = %d, want 2", kinds["CTRL-EVENT-SCAN-STARTED"])
	}
	if len(kinds) > maxUnknownEventKinds {
		t.Errorf("unknown kind map grew to %d, cap is %d", len(kinds), maxUnknownEventKinds)
	}
	if source.Ignored() == 0 {
		t.Error("ignored lines must be counted")
	}
}

func TestFastTierOwnsRFAgainstTheSlowTier(t *testing.T) {
	// The 30s mca-dump pass used to overwrite the 1 Hz wlanconfig reading, so the served signal
	// changed twice a minute. Do not "reconcile" the tiers by letting slow win again.
	link := &ClientLink{}
	fastSignal, slowSignal := -54, -70

	applyFastToLink(link, StaFast{Signal: &fastSignal, TxRateKbps: 2161000, CollectedAt: time.Now()})
	applySlowToLink(link, StaSlow{Signal: &slowSignal, TxRate: 100000})

	if link.Signal == nil || *link.Signal != fastSignal {
		t.Errorf("signal = %v, want the fast tier's %d", link.Signal, fastSignal)
	}
	if link.TxRateKbps != 2161000 {
		t.Errorf("tx rate = %d, want the fast tier's 2161000", link.TxRateKbps)
	}
}

func TestSlowTierSuppliesRFWhenFastHasNotReported(t *testing.T) {
	link := &ClientLink{}
	slowSignal := -70

	applySlowToLink(link, StaSlow{Signal: &slowSignal, TxRate: 100000})

	if link.Signal == nil || *link.Signal != slowSignal {
		t.Errorf("signal = %v, want the slow tier's %d when fast is absent", link.Signal, slowSignal)
	}
}
