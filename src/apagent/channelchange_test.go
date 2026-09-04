package main

import (
	"testing"
	"time"
)

func TestChannelChangesReportsAMoveWithBothSides(t *testing.T) {
	now := time.Now().UTC()
	prev := []RadioState{{Name: "wifi2", Band: "6", Channel: 101, Bandwidth: 160, CenterMhz: 6505}}
	cur := []RadioState{{Name: "wifi2", Band: "6", Channel: 69, Bandwidth: 160, CenterMhz: 6345}}

	events := channelChanges(prev, cur, now)

	if len(events) != 1 {
		t.Fatalf("got %d events, want 1", len(events))
	}
	e := events[0]
	if e.Type != EventChannelChange || e.Radio != "wifi2" || e.Channel == nil {
		t.Fatalf("event shape wrong: %+v", e)
	}
	want := ChannelChange{Band: "6", FromChannel: 101, FromBw: 160, FromCenterMhz: 6505, ToChannel: 69, ToBw: 160, ToCenterMhz: 6345}
	if *e.Channel != want {
		t.Errorf("change = %+v, want %+v", *e.Channel, want)
	}
	if !e.CollectedAt.Equal(now) {
		t.Errorf("collected_at = %v, want %v", e.CollectedAt, now)
	}
}

func TestChannelChangesWidthOnlyAndBlockOnlyCount(t *testing.T) {
	now := time.Now().UTC()
	prev := []RadioState{{Name: "wifi2", Channel: 69, Bandwidth: 320, CenterMhz: 6265}}

	if got := channelChanges(prev, []RadioState{{Name: "wifi2", Channel: 69, Bandwidth: 160, CenterMhz: 6345}}, now); len(got) != 1 {
		t.Errorf("a width change on the same primary must report, got %d", len(got))
	}
	if got := channelChanges(prev, []RadioState{{Name: "wifi2", Channel: 69, Bandwidth: 320, CenterMhz: 6425}}, now); len(got) != 1 {
		t.Errorf("a block change on the same primary and width must report, got %d", len(got))
	}
}

func TestChannelChangesIgnoresWhatIsNotAMove(t *testing.T) {
	now := time.Now().UTC()
	prev := []RadioState{
		{Name: "wifi2", Channel: 69, Bandwidth: 160, CenterMhz: 6345},
		{Name: "wifi3", Channel: 1, Bandwidth: 20, ScanRadio: true},
	}
	cur := []RadioState{
		// Same channel, center not yet known after a restart: not a move.
		{Name: "wifi2", Channel: 69, Bandwidth: 160, CenterMhz: 0},
		// The scan radio hopped.
		{Name: "wifi3", Channel: 36, Bandwidth: 20, ScanRadio: true},
		// A radio that was not in the previous table.
		{Name: "wifi1", Channel: 100, Bandwidth: 160},
		// A counter-only row.
		{Name: "wifi7", Channel: 5, CounterOnly: true},
	}

	if got := channelChanges(prev, cur, now); len(got) != 0 {
		t.Errorf("expected no events, got %+v", got)
	}
	// A radio that had no VAP up before (channel 0) has nothing to have moved from.
	if got := channelChanges([]RadioState{{Name: "wifi2"}}, []RadioState{{Name: "wifi2", Channel: 69}}, now); len(got) != 0 {
		t.Errorf("a radio coming up is not a move, got %+v", got)
	}
}
