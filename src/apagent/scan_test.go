package main

import (
	"testing"
	"time"
)

func findScan(t *testing.T, snap McaSnapshot, name string) RadioScan {
	t.Helper()
	for _, s := range snap.Scans {
		if s.Name == name {
			return s
		}
	}
	t.Fatalf("no scan for radio %s", name)
	return RadioScan{}
}

func TestMcaScanTablesParse(t *testing.T) {
	snap := loadMcaFixture(t)

	wifi1 := findScan(t, snap, "wifi1")
	if wifi1.Band != "5" || wifi1.ScanRadio {
		t.Errorf("wifi1 scan = band %q scanRadio %v, want 5/false", wifi1.Band, wifi1.ScanRadio)
	}
	// The 400 s old neighbor is dropped; the fresh one keeps its band token and a normalized BSSID.
	if len(wifi1.Scan) != 1 {
		t.Fatalf("scan entries = %d, want 1 (the stale one dropped)", len(wifi1.Scan))
	}
	e := wifi1.Scan[0]
	if e.Bssid != "02:00:00:aa:00:01" || e.Band != "5" || e.Channel != 44 || e.Width != 80 || e.CenterMhz != 5210 || e.Signal != -71 || e.AgeSeconds != 1 {
		t.Errorf("scan entry wrong: %+v", e)
	}

	if len(wifi1.Spectrum) != 2 {
		t.Fatalf("spectrum entries = %d, want 2", len(wifi1.Spectrum))
	}
	s := wifi1.Spectrum[1]
	if s.Channel != 44 || s.Utilization != 31 || s.Interference != -58 || s.OtherBssCount != 4 || s.CenterMhz != 5220 {
		t.Errorf("spectrum entry wrong: %+v", s)
	}
	if wifi1.SpectrumAt == nil || wifi1.SpectrumAt.Unix() != 1787604800 {
		t.Errorf("spectrum_table_time = %v, want 1787604800", wifi1.SpectrumAt)
	}

	// A radio with no tables serves empty lists, never null.
	wifi0 := findScan(t, snap, "wifi0")
	if wifi0.Scan == nil || wifi0.Spectrum == nil || len(wifi0.Scan) != 0 || len(wifi0.Spectrum) != 0 {
		t.Errorf("a radio without tables must serve empty lists: %+v", wifi0)
	}
	wifi3 := findScan(t, snap, "wifi3")
	if !wifi3.ScanRadio {
		t.Error("the dedicated scan radio must be marked")
	}
}

func TestTableServesScansWithTheirReadTime(t *testing.T) {
	table := NewTable(64, time.Minute)
	now := time.Now().UTC()
	table.ApplySlow(loadMcaFixture(t), now)

	scans, at := table.Scans()
	if at != now || len(scans) != 4 {
		t.Errorf("scans = %d at %v, want 4 at %v", len(scans), at, now)
	}
}
