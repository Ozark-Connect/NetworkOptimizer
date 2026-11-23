package main

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestParseLinkLine(t *testing.T) {
	now := time.Now()
	cases := []struct {
		line  string
		typ   string
		iface string
		speed string
	}{
		{"Nov 13 19:23:01 MudRoom kernel: [ 1234.5] nss-dp 3a001000.dp1 eth0: PHY Link is down", EventLinkDown, "eth0", ""},
		{"Nov 13 19:23:04 MudRoom kernel: [ 1237.5] nss-dp 3a001000.dp1 eth0: PHY Link up speed: 2500", EventLinkUp, "eth0", "2500"},
		{"kernel: mtk_soc_eth eth1: Link is Up - 1Gbps/Full - flow control rx/tx", EventLinkUp, "eth1", "1000"},
		{"kernel: eth0: Link is Down", EventLinkDown, "eth0", ""},
	}
	for _, c := range cases {
		ev, ok := parseLinkLine(c.line, now)
		if !ok || ev.Type != c.typ || ev.Iface != c.iface || ev.Detail != c.speed {
			t.Fatalf("%q: got ok=%v %+v", c.line, ok, ev)
		}
	}
	for _, line := range []string{"Phy link never came up", "wifi0: STA aa:bb link up", "random line"} {
		if _, ok := parseLinkLine(line, now); ok {
			t.Fatalf("%q should not parse", line)
		}
	}
}

func TestParseCrcCount(t *testing.T) {
	out := "NIC statistics:\n     rx_packets: 100\n     rx_crc_err: 13\n"
	if v, ok := parseCrcCount(out); !ok || v != 13 {
		t.Fatalf("got %v %v", v, ok)
	}
	if _, ok := parseCrcCount("no counters"); ok {
		t.Fatal("expected none")
	}
}

func TestUplinkCountersOnlyOnChange(t *testing.T) {
	dir := t.TempDir()
	eth := filepath.Join(dir, "eth0")
	if err := os.MkdirAll(eth, 0o755); err != nil {
		t.Fatal(err)
	}
	write := func(v string) { _ = os.WriteFile(filepath.Join(eth, "carrier_changes"), []byte(v+"\n"), 0o644) }
	ring := NewEventRing(16)
	s := NewUplinkCounterSource(ring)
	s.sysNet = dir
	s.ethtool = func(ctx context.Context, iface string) (string, error) { return "rx_crc_err: 2\n", nil }

	write("61")
	s.pass(context.Background(), time.Now())
	s.pass(context.Background(), time.Now())
	write("63")
	s.pass(context.Background(), time.Now())

	events := ring.snapshot()
	if len(events) != 2 {
		t.Fatalf("want 2 readings, got %d", len(events))
	}
	if *events[1].Counters.CarrierChanges != 63 || *events[1].Counters.RxCrcErrors != 2 {
		t.Fatalf("bad reading %+v", events[1].Counters)
	}
}

func TestMeshUplinkCounters_SumTheChildSideLinks(t *testing.T) {
	// Values from a U7 Outdoor mesh child on an MLO backhaul (6 GHz vwiresta4, 5 GHz vwiresta7).
	stations := []StaSlow{
		{Vap: "vwiresta4", TxRetries: 23838233, WifiTxAttempts: 495144212, WifiTxDropped: 0},
		{Vap: "vwiresta7", TxRetries: 44664827, WifiTxAttempts: 603784006, WifiTxDropped: 2},
		{Vap: "wifi1ap3", TxRetries: 99, WifiTxAttempts: 999},
		{Vap: "vwireap10", TxRetries: 5, WifiTxAttempts: 50},
	}
	c, ok := meshUplinkCounters(stations)
	if !ok || *c.MeshTxRetries != 68503060 || *c.MeshTxAttempts != 1098928218 || *c.MeshTxDropped != 2 {
		t.Fatalf("got %+v %v", c, ok)
	}
	if _, ok := meshUplinkCounters([]StaSlow{{Vap: "wifi0ap1"}, {Vap: "vwireap10"}}); ok {
		t.Fatal("a wired AP (or a mesh parent) has no mesh uplink")
	}
}