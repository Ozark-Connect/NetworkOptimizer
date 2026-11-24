package main

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// stubLocalToUTC treats stamps as UTC-6 wall clock, as busybox date does under CST6CDT in winter.
func stubLocalToUTC(t *testing.T) {
	orig := localToUTC
	localToUTC = func(_ context.Context, stamps []string) map[string]time.Time {
		out := map[string]time.Time{}
		for _, s := range stamps {
			if v, err := time.Parse("2006-01-02 15:04:05", s); err == nil {
				out[s] = v.Add(6 * time.Hour)
			}
		}
		return out
	}
	t.Cleanup(func() { localToUTC = orig })
}

func TestSyslogWallClock(t *testing.T) {
	got, ok := syslogWallClock("Sun Nov 22 21:58:51 2026 kern.info kernel: nss-dp 3a500000.dp2 eth0: PHY Link is down")
	if !ok || got != "2026-11-22 21:58:51" {
		t.Fatalf("got %q %v", got, ok)
	}
	// busybox space-pads a single-digit day.
	got, ok = syslogWallClock("Sun Nov  1 01:24:56 2026 user.info uplink-monitor[6155]: x")
	if !ok || got != "2026-11-01 01:24:56" {
		t.Fatalf("got %q %v", got, ok)
	}
	if _, ok := syslogWallClock("garbage"); ok {
		t.Fatal("garbage parsed")
	}
}

func TestReplayLinkLines_LastFourteenDaysWithLineTimes(t *testing.T) {
	stubLocalToUTC(t)
	log := "Sun Nov  1 10:00:00 2026 kern.info kernel: nss-dp 3a500000.dp2 eth0: PHY Link is down\n" +
		"Sun Nov 22 21:58:51 2026 kern.info kernel: nss-dp 3a500000.dp2 eth0: PHY Link is down\n" +
		"Sun Nov 22 21:58:52 2026 daemon.info stahtd: unrelated client line\n" +
		"Sun Nov 22 21:58:54 2026 kern.info kernel: nss-dp 3a500000.dp2 eth0: PHY Link up speed: 2500\n"
	path := filepath.Join(t.TempDir(), "messages")
	if err := os.WriteFile(path, []byte(log), 0o644); err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 11, 23, 7, 0, 0, 0, time.UTC)
	evs := replayLinkLines(context.Background(), path, now)
	if len(evs) != 2 {
		t.Fatalf("want the 2 recent link lines, got %d", len(evs))
	}
	if evs[0].Type != EventLinkDown || !evs[0].EventTime.Equal(time.Date(2026, 11, 23, 3, 58, 51, 0, time.UTC)) {
		t.Fatalf("first: %+v at %v", evs[0], evs[0].EventTime)
	}
	if evs[1].Type != EventLinkUp || evs[1].Detail != "2500" {
		t.Fatalf("second: %+v", evs[1])
	}
}

func TestSyslogConsume_StampsLinkEventsWithTheLineTime(t *testing.T) {
	stubLocalToUTC(t)
	ring := NewEventRing(8)
	s := NewSyslogSource("", ring)
	s.consume("Sun Nov 22 21:58:51 2026 kern.info kernel: nss-dp 3a500000.dp2 eth0: PHY Link is down")
	evs, _ := ring.Since(0)
	if len(evs) != 1 || evs[0].EventTime == nil || !evs[0].EventTime.Equal(time.Date(2026, 11, 23, 3, 58, 51, 0, time.UTC)) {
		t.Fatalf("live event not stamped with its line time: %+v", evs)
	}
}
