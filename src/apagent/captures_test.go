package main

import (
	"os"
	"strings"
	"testing"
	"time"
)

// The three files under testdata are the real output of the AP's own tools, captured on
// ap-tiny-home (U7-Pro-XGS-B, firmware 8.7.11.19419). Only the client MAC in the wlanconfig
// capture is replaced; athstats and apstats carry no identifying data at all. Every assertion
// below is a value the AP actually printed.
func loadCapture(t *testing.T, name string) string {
	t.Helper()
	data, err := os.ReadFile("testdata/" + name)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func TestParseWlanconfigRealCapture(t *testing.T) {
	out := loadCapture(t, "wlanconfig-wifi2ap10.txt")

	header, ok := parseWlanconfigHeader(out)
	if !ok {
		t.Fatal("the measured header must validate")
	}
	if len(header) != 25 {
		t.Fatalf("header has %d columns, the measured header has 25", len(header))
	}
	// The column set is not what the earlier spec listed: XCAPS replaces ACAPS, ERP is gone,
	// DHCP / TIME_TO_IP / VHTCAPS are new, and PSMODE moved to last.
	for i, want := range map[int]string{12: "XCAPS", 15: "DHCP", 16: "TIME_TO_IP", 18: "VHTCAPS", 24: "PSMODE"} {
		if header[i] != want {
			t.Errorf("column %d is %q, want %q", i, header[i], want)
		}
	}

	stations := parseWlanconfigStations("wifi2ap10", out, time.Now().UTC())
	if len(stations) != 1 {
		t.Fatalf("got %d stations, want 1", len(stations))
	}
	s := stations[0]

	if s.MAC != "02:aa:bb:00:00:11" || s.Vap != "wifi2ap10" {
		t.Errorf("identity wrong: %+v", s)
	}
	if s.Channel != 101 {
		t.Errorf("channel = %d, want 101", s.Channel)
	}
	// TXRATE and RXRATE carry a megabit suffix, so they need normalizing to kbps rather than
	// reading as raw values.
	if s.TxRateKbps != 2161000 || s.RxRateKbps != 1814000 {
		t.Errorf("rates = %d / %d kbps, want 2161000 and 1814000 (from 2161M / 1814M)",
			s.TxRateKbps, s.RxRateKbps)
	}
	if s.IdleSeconds != 17 {
		t.Errorf("idle = %d, want 17", s.IdleSeconds)
	}
	if s.AssocSeconds != 21247 {
		t.Errorf("assoc = %d seconds, want 21247 (05:54:07)", s.AssocSeconds)
	}
	// The AP's own detail block says the RSSI column is combined over chains in dBm and reports
	// SNR separately, so this column is dBm and belongs on signal.
	if s.Signal == nil || *s.Signal != -57 {
		t.Errorf("signal = %v, want -57 dBm", s.Signal)
	}
	if s.SNR != nil {
		t.Errorf("the RSSI column is dBm here and must not be reported as SNR, got %v", *s.SNR)
	}
	if s.MinSignal == nil || *s.MinSignal != -77 || s.MaxSignal == nil || *s.MaxSignal != -50 {
		t.Errorf("min/max signal wrong: %+v", s)
	}
}

// The header splits into 25 fields but the data row splits into 26, because IEs holds "RSN WME".
// A naive index map shifts every column after it, which silently corrupts MODE, RXNSS, TXNSS,
// and PSMODE rather than failing.
func TestWlanconfigRaggedIEsColumnDoesNotShiftColumns(t *testing.T) {
	out := loadCapture(t, "wlanconfig-wifi2ap10.txt")
	lines := strings.Split(out, "\n")
	header, _ := parseWlanconfigHeader(out)
	fields := strings.Fields(lines[1])

	if len(header) != 25 || len(fields) != 26 {
		t.Fatalf("the trap requires a 25-column header and a 26-field row, got %d and %d",
			len(header), len(fields))
	}

	s := parseWlanconfigStations("wifi2ap10", out, time.Now().UTC())[0]
	if s.Mode != "IEEE80211_MODE_11AXA_HE160" {
		t.Errorf("mode = %q, want IEEE80211_MODE_11AXA_HE160", s.Mode)
	}
	if s.RxNss != 2 || s.TxNss != 2 {
		t.Errorf("NSS = rx %d tx %d, want 2 and 2", s.RxNss, s.TxNss)
	}
	if s.PsMode != "1" {
		t.Errorf("psmode = %q, want 1", s.PsMode)
	}

	// Prove the naive mapping would have been wrong rather than merely assuming it.
	naive := map[string]string{}
	for i, name := range header {
		if i < len(fields) {
			naive[name] = fields[i]
		}
	}
	if naive["MODE"] == s.Mode {
		t.Error("index mapping happens to agree here, so this test is no longer proving anything")
	}
	if naive["IEs"] != "RSN" || naive["MODE"] != "WME" {
		t.Errorf("the naive shift is not what was expected: IEs=%q MODE=%q", naive["IEs"], naive["MODE"])
	}
}

// wlanconfig wraps the capability flags onto a continuation line and then prints a per-station
// detail block. Neither is a station.
func TestWlanconfigSkipsContinuationAndDetailLines(t *testing.T) {
	out := loadCapture(t, "wlanconfig-wifi2ap10.txt")
	if lines := strings.Count(out, "\n"); lines < 15 {
		t.Fatalf("the capture should carry the detail block, only %d lines", lines)
	}
	if !strings.Contains(out, "Supported Rates(Mbps)") || !strings.Contains(out, "LM NR BRT") {
		t.Fatal("the capture is missing the continuation or detail lines this test exists for")
	}
	if got := len(parseWlanconfigStations("wifi2ap10", out, time.Now().UTC())); got != 1 {
		t.Errorf("got %d stations from one client's output, want 1", got)
	}
}

func TestParseApstatsRealCapture(t *testing.T) {
	out := loadCapture(t, "apstats-r-wifi2.txt")

	if got := apstatsRadio(out); got != "wifi2" {
		t.Errorf("apstatsRadio = %q, want wifi2", got)
	}

	counters := parseRadioCounters(out)
	// The lithium cycle counters carry a prefix on every line and a parenthetical on one label;
	// both are stripped so the keys are the names the wedge detector reads.
	want := map[string]int64{
		"chan_nf":      -92,
		"tx_frame_cnt": 2021750386,
		"rx_frame_cnt": 0,
		"rx_clear_cnt": 2964923986,
		"cycle_cnt":    3788682907,
		"phy_err_cnt":  0,
		"chan_tx_pwr":  42,
	}
	for k, v := range want {
		got, ok := counters[k]
		if !ok {
			t.Errorf("counter %q missing", k)
			continue
		}
		if got != v {
			t.Errorf("counter %q = %d, want %d", k, got, v)
		}
	}

	// A value the tool prints as text is not a counter and must not read as zero.
	for _, absent := range []string{"channel_utilization", "throughput", "per_over_configured_period"} {
		if v, ok := counters[absent]; ok {
			t.Errorf("a disabled value became counter %q = %d", absent, v)
		}
	}
	if counters["total_per"] != 0 {
		t.Errorf("a real zero must still be a counter, total_per = %d", counters["total_per"])
	}
	if counters["tx_failures"] != 8419 || counters["rx_rssi"] != 38 {
		t.Errorf("top-level counters wrong: tx_failures %d rx_rssi %d", counters["tx_failures"], counters["rx_rssi"])
	}
}

// apstats prints "Best effort" under both a Tx and an Rx heading. Without section scoping the
// second silently replaces the first, which is data corruption rather than a visible failure.
func TestApstatsPerAcSectionsDoNotCollide(t *testing.T) {
	counters := parseRadioCounters(loadCapture(t, "apstats-r-wifi2.txt"))

	tx, txOK := counters["tx_data_packets_per_ac.best_effort"]
	rx, rxOK := counters["rx_data_packets_per_ac.best_effort"]
	if !txOK || !rxOK {
		t.Fatalf("per-AC counters are not section scoped: tx %v rx %v", txOK, rxOK)
	}
	if tx != 2244122 || rx != 1381561 {
		t.Errorf("per-AC values = tx %d rx %d, want 2244122 and 1381561", tx, rx)
	}

	// A top-level counter printed after an indented block belongs to no section.
	if _, wrong := counters["rx_data_packets_per_ac.tx_beacon_frames"]; wrong {
		t.Error("a top-level counter was filed under the section above it")
	}
	if counters["tx_beacon_frames"] != 207505 {
		t.Errorf("tx_beacon_frames = %d, want 207505 at top level", counters["tx_beacon_frames"])
	}
	// An indented member whose own label ends in a colon still parses on the equals sign.
	if counters["co_located_rnr_stats.created_vap"] != 1 {
		t.Errorf("a label carrying its own colon did not parse: %v", counters["co_located_rnr_stats.created_vap"])
	}
}

func TestParseAthstatsRealCapture(t *testing.T) {
	counters := parseRadioCounters(loadCapture(t, "athstats-wifi2.txt"))

	if len(counters) < 300 {
		t.Fatalf("got %d counters, the measured output carries around 340", len(counters))
	}
	// pdev_resets is the counter that gave ten hours of warning before a radio wedged.
	if counters["htt_tx_pdev_stats_cmn_tlv.pdev_resets"] != 74144 {
		t.Errorf("pdev_resets = %d, want 74144", counters["htt_tx_pdev_stats_cmn_tlv.pdev_resets"])
	}
	if counters["htt_tx_pdev_stats_cmn_tlv.tx_timeout"] != 6450 {
		t.Errorf("tx_timeout = %d, want 6450", counters["htt_tx_pdev_stats_cmn_tlv.tx_timeout"])
	}
	// The rate tables use an equals sign while the rest of the file uses a colon and a tab, and
	// both rate sections carry an OFDM 6 Mbps counter.
	if counters["tx_rate_info.ofdm_6_mbps"] != 952 || counters["rx_rate_info.ofdm_6_mbps"] != 2 {
		t.Errorf("rate tables collided or did not parse: tx %d rx %d",
			counters["tx_rate_info.ofdm_6_mbps"], counters["rx_rate_info.ofdm_6_mbps"])
	}
	// The mac id counter appears in two HTT sections; section scoping keeps them apart.
	if counters["htt_tx_pdev_stats_cmn_tlv.mac_id_word"] != 1 ||
		counters["htt_stats_rx_pdev_fw_stats_tag.mac_id_word"] != 1 {
		t.Error("the two HTT mac id counters are not both present")
	}
	// A u32 -1 sentinel is served as read rather than being reinterpreted.
	if counters["packets_dropped_on_rx.rxdma_errors_replenished"] != u32Sentinel {
		t.Errorf("the u32 sentinel was rewritten: %d",
			counters["packets_dropped_on_rx.rxdma_errors_replenished"])
	}
}

// A health counter must be findable without knowing which tool and section produced it.
func TestHealthCountersArePromotedToBareKeys(t *testing.T) {
	counters := parseRadioCounters(loadCapture(t, "athstats-wifi2.txt"))
	if _, bare := counters["pdev_resets"]; bare {
		t.Fatal("the parser must not promote on its own; collection does it after merging")
	}

	promoteHealthCounters(counters)
	if counters["pdev_resets"] != 74144 {
		t.Errorf("pdev_resets = %d after promotion, want 74144", counters["pdev_resets"])
	}
	if counters["htt_tx_pdev_stats_cmn_tlv.pdev_resets"] != 74144 {
		t.Error("promotion must add a key, not move one")
	}
	// A counter that is not a health counter stays where the tool filed it.
	if _, promoted := counters["tx_timeout"]; promoted {
		t.Error("promotion must be limited to the health counter set")
	}
}

// Two labels in one section that normalize alike must not overwrite each other.
func TestCounterCollisionInOneSectionIsDisambiguated(t *testing.T) {
	out := strings.Join([]string{
		"Tx ingress stats",
		"\tDMA map error                :\t7",
		"\tDma map error                :\t9",
	}, "\n")

	counters := parseRadioCounters(out)
	if counters["tx_ingress_stats.dma_map_error"] != 7 {
		t.Errorf("first counter = %d, want 7", counters["tx_ingress_stats.dma_map_error"])
	}
	if counters["tx_ingress_stats.dma_map_error_2"] != 9 {
		t.Errorf("second counter = %d, want 9 under a suffixed key",
			counters["tx_ingress_stats.dma_map_error_2"])
	}
}

// -i selects the interface, so output naming a different radio must not be attributed.
func TestApstatsRadioMismatchIsDetectable(t *testing.T) {
	if got := apstatsRadio("Radio Level Stats: wifi0\nTx Data Packets = 1\n"); got != "wifi0" {
		t.Errorf("apstatsRadio = %q, want wifi0", got)
	}
	// Bare -R is AP level: it answers without cycle counters rather than failing, which is why
	// the invocation carries a level flag.
	apLevel := "apstats: No application recognized options. Using defaults: AP level, non-recursive\n" +
		"Tx Data Packets = 5\n"
	if got := apstatsRadio(apLevel); got != "" {
		t.Errorf("AP-level output must not name a radio, got %q", got)
	}
	if _, ok := parseRadioCounters(apLevel)["cycle_cnt"]; ok {
		t.Error("AP-level output carries no cycle counters, so none may be invented")
	}
}

// A saturated u32 is served as read but never differenced: the delta would be noise.
func TestU32SentinelIsNotDifferenced(t *testing.T) {
	prev := map[string]int64{"a": u32Sentinel, "b": 10, "cycle_cnt": 100}
	cur := map[string]int64{"a": 5, "b": u32Sentinel, "cycle_cnt": 400}

	deltas := counterDeltas(prev, cur)
	for _, k := range []string{"a", "b"} {
		if _, ok := deltas[k]; ok {
			t.Errorf("counter %q at the u32 sentinel must not produce a delta", k)
		}
	}
	if deltas["cycle_cnt"] != 300 {
		t.Errorf("cycle_cnt delta = %d, want 300", deltas["cycle_cnt"])
	}
}

// Deltas stay bounded by omitting counters that did not move, except the wedge set, where a zero
// delta is the fault signature rather than an absence of news.
func TestDeltasOmitUnchangedCountersButKeepTheWedgeSet(t *testing.T) {
	prev := map[string]int64{"idle_counter": 5, "tx_frame_cnt": 40, "rx_clear_cnt": 100, "tx_data_bytes": 1}
	cur := map[string]int64{"idle_counter": 5, "tx_frame_cnt": 40, "rx_clear_cnt": 250, "tx_data_bytes": 9}

	deltas := counterDeltas(prev, cur)
	if _, ok := deltas["idle_counter"]; ok {
		t.Error("an unchanged ordinary counter must be omitted")
	}
	if v, ok := deltas["tx_frame_cnt"]; !ok || v != 0 {
		t.Errorf("tx_frame_cnt = %d present=%v, want a reported zero", v, ok)
	}
	if deltas["rx_clear_cnt"] != 150 || deltas["tx_data_bytes"] != 8 {
		t.Errorf("moving counters wrong: %v", deltas)
	}
}

// The scan radio must reach radio discovery, or no health counters are ever collected for it.
func TestRadioDiscoveryIncludesTheScanRadio(t *testing.T) {
	data, err := os.ReadFile("testdata/mca-dump.json")
	if err != nil {
		t.Fatal(err)
	}
	summary, err := parseMcaDump(data)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"wifi0", "wifi1", "wifi2", "wifi3"} {
		if !contains(summary.RadioNames, want) {
			t.Errorf("radio %s missing from discovery: %v", want, summary.RadioNames)
		}
	}
	if summary.RadioCount != 3 {
		t.Errorf("radio_table count = %d, want 3; the scan radio is counted separately", summary.RadioCount)
	}

	// The VAP names its parent radio, which is a third source: a radio missing from both tables
	// is still found.
	fromVapOnly := `{"radio_table":[{"name":"wifi0"}],"vap_table":[{"radio_name":"wifi9"}]}`
	summary, err = parseMcaDump([]byte(fromVapOnly))
	if err != nil {
		t.Fatal(err)
	}
	if !contains(summary.RadioNames, "wifi9") {
		t.Errorf("a radio named only by a VAP was dropped: %v", summary.RadioNames)
	}
}

// A radio the tools answered for but neither table listed still gets a row rather than vanishing.
func TestCounterOnlyRadioIsNotDropped(t *testing.T) {
	table, _ := newFixtureTable(t)
	now := time.Now().UTC()

	table.SetRadioCounters(
		map[string]map[string]int64{"wifi7": {"pdev_resets": 4}},
		map[string][]string{"wifi7": {"athstats"}},
		now,
	)

	radios := table.Radios()
	var found *RadioState
	for i := range radios {
		if radios[i].Name == "wifi7" {
			found = &radios[i]
		}
	}
	if found == nil {
		t.Fatal("a radio known only to the counter tools was dropped")
	}
	if !found.CounterOnly {
		t.Error("a counter-only radio must say so rather than looking like a full record")
	}
	if found.Counters["pdev_resets"] != 4 {
		t.Errorf("counters = %v", found.Counters)
	}
}
