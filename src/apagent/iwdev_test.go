package main

import (
	"testing"
	"time"
)

// testdata/iw-dev.txt is the real `iw dev` of ap-tiny-home (U7-Pro-XGS-B, iw 6.17). Only the MACs
// and SSIDs are replaced; every channel line is what the AP printed.
func TestParseIwDevRealCapture(t *testing.T) {
	found := parseIwDev(loadCapture(t, "iw-dev.txt"))

	// 320 MHz on primary 165: the block center is 6745 MHz (channel 159), which is the 129-189
	// block and not the 161-221 block the primary alone would also allow.
	if got := found["wifi2"]; got != (iwChannel{PrimaryMHz: 6775, WidthMHz: 320, CenterMHz: 6745}) {
		t.Errorf("wifi2 = %+v, want primary 6775 / width 320 / center 6745", got)
	}
	// Every VAP on the radio prints the same line, so a VAP is a valid fallback source.
	if got := found["wifi2ap10"]; got != found["wifi2"] {
		t.Errorf("wifi2ap10 = %+v, want the radio's own line %+v", got, found["wifi2"])
	}
	if got := found["wifi1"]; got != (iwChannel{PrimaryMHz: 5500, WidthMHz: 160, CenterMHz: 5570}) {
		t.Errorf("wifi1 = %+v, want primary 5500 / width 160 / center 5570", got)
	}
	if got := found["wifi0"]; got != (iwChannel{PrimaryMHz: 2462, WidthMHz: 20, CenterMHz: 2462}) {
		t.Errorf("wifi0 = %+v, want a 20 MHz line whose center is its primary", got)
	}
	// mld-wifi0 has no channel line at all.
	if _, ok := found["mld-wifi0"]; ok {
		t.Error("mld-wifi0 carries no channel line and must not be reported")
	}
	// The scan radio does print one; it is dropped at attribution, not at parse.
	if _, ok := found["scan0"]; !ok {
		t.Error("scan0 prints a channel line and the parser should keep it")
	}
}

func TestParseIwDevTolerates(t *testing.T) {
	if got := parseIwDev(""); len(got) != 0 {
		t.Errorf("empty output produced %v", got)
	}
	// A channel line before any Interface header belongs to nothing.
	if got := parseIwDev("channel 1 (2412 MHz), width: 20 MHz, center1: 2412 MHz\n"); len(got) != 0 {
		t.Errorf("an orphan channel line was attributed: %v", got)
	}
	// A line with a shape drift keeps the interface absent rather than reporting zeros.
	if got := parseIwDev("Interface wifi9\n\tchannel 36 (5180 MHz), width: 80 MHz\n"); len(got) != 0 {
		t.Errorf("a line without center1 was reported: %v", got)
	}
}

// The radio table is attributed by radio netdev first, then any up VAP on the radio, and never for
// the scan radio.
func TestCenterForRadioAttribution(t *testing.T) {
	centers := parseIwDev(loadCapture(t, "iw-dev.txt"))
	vaps := []VapState{
		{Name: "wifi2ap10", RadioName: "wifi2", Up: true},
		{Name: "wifi1ap5", RadioName: "wifi1", Up: true},
		{Name: "scan0", RadioName: "wifi3", Up: true},
	}

	if got := centerForRadio(RadioState{Name: "wifi2"}, vaps, centers); got != 6745 {
		t.Errorf("wifi2 center = %d, want 6745 from its own netdev", got)
	}
	// Drop the radio's own line to prove the VAP fallback.
	delete(centers, "wifi1")
	if got := centerForRadio(RadioState{Name: "wifi1"}, vaps, centers); got != 5570 {
		t.Errorf("wifi1 center = %d, want 5570 from wifi1ap5", got)
	}
	if got := centerForRadio(RadioState{Name: "wifi3", ScanRadio: true}, vaps, centers); got != 0 {
		t.Errorf("scan radio center = %d, want 0: it hops", got)
	}
	if got := centerForRadio(RadioState{Name: "wifi7"}, vaps, centers); got != 0 {
		t.Errorf("unknown radio center = %d, want 0", got)
	}
	if got := centerForRadio(RadioState{Name: "wifi2"}, vaps, nil); got != 0 {
		t.Errorf("no iw output must leave the center unset, got %d", got)
	}
}

// After a channel change the held iw answer is a pass stale. Its primary no longer matches
// mca-dump's channel, and pairing the new primary with the old block would be worse than nothing.
func TestCenterForRadioDropsAStalePrimary(t *testing.T) {
	centers := parseIwDev(loadCapture(t, "iw-dev.txt"))

	if got := centerForRadio(RadioState{Name: "wifi2", Channel: 165}, nil, centers); got != 6745 {
		t.Errorf("matching primary: center = %d, want 6745", got)
	}
	if got := centerForRadio(RadioState{Name: "wifi2", Channel: 101}, nil, centers); got != 0 {
		t.Errorf("radio moved to 101 but iw still says 165: center = %d, want 0", got)
	}
	if got := centerForRadio(RadioState{Name: "wifi1", Channel: 100}, nil, centers); got != 5570 {
		t.Errorf("5 GHz primary 5500 MHz is channel 100: center = %d, want 5570", got)
	}
}

func TestChannelFromMHz(t *testing.T) {
	for mhz, want := range map[int]int{2412: 1, 2462: 11, 2484: 14, 5180: 36, 5500: 100, 5955: 1, 6295: 69, 6775: 165, 0: 0} {
		if got := channelFromMHz(mhz); got != want {
			t.Errorf("channelFromMHz(%d) = %d, want %d", mhz, got, want)
		}
	}
}

// The center survives the slow tier replacing the radio table, and is served on /radios.
func TestRadioCentersSurviveApplySlow(t *testing.T) {
	table, snap := newFixtureTable(t)
	now := time.Now().UTC()

	// The fixture's wifi2 is on primary 85 (6375 MHz); at 320 MHz its lower block is 65-125,
	// center 95 (6425 MHz).
	table.SetRadioCenters(map[string]iwChannel{"wifi2": {PrimaryMHz: 6375, WidthMHz: 320, CenterMHz: 6425}}, now)
	if got := radioNamed(t, table.Radios(), "wifi2").CenterMhz; got != 6425 {
		t.Fatalf("center after SetRadioCenters = %d, want 6425", got)
	}

	table.ApplySlow(snap, now.Add(10*time.Second))
	if got := radioNamed(t, table.Radios(), "wifi2").CenterMhz; got != 6425 {
		t.Errorf("center after ApplySlow = %d, want 6425: the next mca-dump pass must not clear it", got)
	}
	if got := radioNamed(t, table.Radios(), "wifi0").CenterMhz; got != 0 {
		t.Errorf("a radio iw did not answer for reads %d, want 0", got)
	}

	// An empty pass (iw missing) leaves the last known center in place rather than flapping it.
	table.SetRadioCenters(nil, now.Add(30*time.Second))
	if got := radioNamed(t, table.Radios(), "wifi2").CenterMhz; got != 6425 {
		t.Errorf("center after an empty pass = %d, want 6425", got)
	}
}

func radioNamed(t *testing.T, radios []RadioState, name string) RadioState {
	t.Helper()
	for _, r := range radios {
		if r.Name == name {
			return r
		}
	}
	t.Fatalf("radio %s not in %d radios", name, len(radios))
	return RadioState{}
}
