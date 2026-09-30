package main

import (
	"reflect"
	"testing"
	"time"
)

// mediatekMca is shaped on a U6-Lite (MT7621, fw 6.7.58): MediaTek ra*/rai* names, the mesh uplink
// on rai3 named like any VAP with only its SSID carrying "vwire-", and a station with rssi and noise
// but no signal key.
const mediatekMca = `{
  "version": "6.7.58.15850",
  "model": "U6-Lite",
  "radio_table": [{"name": "ra0", "radio": "ng"}, {"name": "rai0", "radio": "na"}],
  "vap_table": [
    {"name": "ra1", "essid": "Home", "radio": "ng", "radio_name": "ra0", "channel": 6, "bw": 20, "up": true,
     "sta_table": [{"mac": "AA:BB:CC:DD:EE:01", "rssi": 36, "noise": -96, "tx_bytes": 10, "rx_bytes": 20}]},
    {"name": "rai1", "essid": "Home", "radio": "na", "radio_name": "rai0", "channel": 44, "bw": 80, "up": true,
     "sta_table": []},
    {"name": "rai3", "essid": "vwire-0123456789abcdef", "radio": "na", "radio_name": "rai0", "channel": 44, "bw": 80, "up": true,
     "sta_table": [{"mac": "AA:BB:CC:DD:EE:99", "rssi": 50, "noise": -96, "tx_bytes": 99, "rx_bytes": 99}]}
  ]
}`

func mediatekSnapshot(t *testing.T) McaSnapshot {
	t.Helper()
	snap, err := parseMcaFull([]byte(mediatekMca), time.Now().UTC())
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	return snap
}

func TestFabricBySsid_finds_a_MediaTek_uplink_named_like_any_VAP(t *testing.T) {
	got := fabricVapsBySsid([]VapState{{Name: "ra1", Essid: "Home"}, {Name: "rai3", Essid: "vwire-0123"}})
	if !reflect.DeepEqual(got, map[string]bool{"rai3": true}) {
		t.Errorf("fabricVapsBySsid = %v, want rai3 only", got)
	}
}

func TestFabricBySsid_stays_off_when_the_uplink_is_named_vwire(t *testing.T) {
	// U7 shape: the name already identifies the uplink, so a user SSID starting "vwire-" is left alone.
	got := fabricVapsBySsid([]VapState{{Name: "vwireap10", Essid: "vwire-0123"}, {Name: "wifi1ap5", Essid: "vwire-guest"}})
	if got != nil {
		t.Errorf("fabricVapsBySsid = %v, want nil when any VAP is named vwire*", got)
	}
}

func TestProbeSummary_names_the_SSID_uplink_so_discovery_can_drop_it(t *testing.T) {
	summary, err := parseMcaDump([]byte(mediatekMca))
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if !summary.FabricVaps["rai3"] || len(summary.FabricVaps) != 1 {
		t.Errorf("FabricVaps = %v, want rai3", summary.FabricVaps)
	}
	if got := withoutVaps([]string{"ra0", "ra1", "rai0", "rai1", "rai3"}, summary.FabricVaps); !reflect.DeepEqual(got, []string{"ra0", "ra1", "rai0", "rai1"}) {
		t.Errorf("withoutVaps = %v, want rai3 dropped", got)
	}
	if !reflect.DeepEqual(summary.RadioNames, []string{"ra0", "rai0"}) {
		t.Errorf("RadioNames = %v, want the MediaTek radios from radio_table", summary.RadioNames)
	}
}

func TestMediaTekUplinkPeer_is_not_a_client_and_not_a_control_target(t *testing.T) {
	table := NewTable(defaultMaxTrackedClients, 120*time.Second)
	now := time.Now().UTC()
	table.ApplySlow(mediatekSnapshot(t), now)
	// stahtd logs the peer's association on the uplink VAP too.
	table.ApplyEvent(Event{Seq: 1, Type: EventAssoc, Vap: "rai3", MAC: "aa:bb:cc:dd:ee:99", CollectedAt: now})

	clients := table.Clients(now)
	if len(clients) != 1 {
		t.Fatalf("got %d clients, want 1: the uplink peer on rai3 is fabric", len(clients))
	}
	if got := table.ControlVapNames(); !reflect.DeepEqual(got, []string{"ra1", "rai1"}) {
		t.Errorf("ControlVapNames = %v, want the uplink excluded", got)
	}
}

func TestU7WithoutMesh_keeps_every_VAP_as_a_control_target(t *testing.T) {
	// A non-mesh U7 carries no vwire- SSID, so the fallback finds nothing and control sees what
	// Vaps() always gave it.
	table := NewTable(defaultMaxTrackedClients, 120*time.Second)
	table.ApplySlow(McaSnapshot{Vaps: []VapState{{Name: "wifi0ap0", Essid: "Home"}, {Name: "wifi1ap5", Essid: "Home"}}}, time.Now().UTC())
	if got := table.ControlVapNames(); !reflect.DeepEqual(got, []string{"wifi0ap0", "wifi1ap5"}) {
		t.Errorf("ControlVapNames = %v, want every VAP", got)
	}
}

func TestMissingSignal_is_derived_from_rssi_and_noise(t *testing.T) {
	snap := mediatekSnapshot(t)
	var s *StaSlow
	for i := range snap.Stations {
		if snap.Stations[i].MAC == "aa:bb:cc:dd:ee:01" {
			s = &snap.Stations[i]
		}
	}
	if s == nil {
		t.Fatal("station not parsed")
	}
	if s.Signal == nil || *s.Signal != -60 {
		t.Errorf("signal = %v, want -60 (rssi 36 + noise -96)", s.Signal)
	}
}

func TestSignalOrDerived_never_overrides_a_reported_signal_or_guesses(t *testing.T) {
	i := func(v int) *int { return &v }
	cases := []struct {
		name                string
		signal, rssi, noise *int
		want                *int
	}{
		{"reported wins", i(-57), i(10), i(-92), i(-57)},
		{"derived", nil, i(35), i(-92), i(-57)},
		{"no noise", nil, i(35), nil, nil},
		{"rssi already dBm", nil, i(-60), i(-92), nil},
		{"noise not negative", nil, i(35), i(0), nil},
	}
	for _, c := range cases {
		got := signalOrDerived(c.signal, c.rssi, c.noise)
		if (got == nil) != (c.want == nil) || (got != nil && *got != *c.want) {
			t.Errorf("%s: got %v, want %v", c.name, got, c.want)
		}
	}
}

func TestAdaptiveBytesInterval_holds_mca_dump_to_a_tenth_of_wall_time(t *testing.T) {
	configured := 10 * time.Second
	cases := map[time.Duration]time.Duration{
		50 * time.Millisecond:  time.Second, // floor
		300 * time.Millisecond: 3 * time.Second,
		900 * time.Millisecond: 9 * time.Second,
		3 * time.Second:        configured, // never slower than configured
	}
	for cost, want := range cases {
		if got := adaptiveBytesInterval(cost, configured); got != want {
			t.Errorf("adaptiveBytesInterval(%v) = %v, want %v", cost, got, want)
		}
	}
}

func TestSmoothMcaCost_backs_off_at_once_and_recovers_slowly(t *testing.T) {
	if got := smoothMcaCost(0, 300*time.Millisecond); got != 300*time.Millisecond {
		t.Errorf("first sample = %v, want it taken as is", got)
	}
	if got := smoothMcaCost(300*time.Millisecond, 900*time.Millisecond); got != 900*time.Millisecond {
		t.Errorf("slower dump = %v, want an immediate back-off to 900ms", got)
	}
	if got := smoothMcaCost(900*time.Millisecond, 300*time.Millisecond); got != 720*time.Millisecond {
		t.Errorf("faster dump = %v, want a partial recovery to 720ms", got)
	}
}
