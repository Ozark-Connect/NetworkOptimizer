package uwn

import (
	"testing"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

func TestSummarizeConnectLoss_CountsRetransmitOutliersAndFailures(t *testing.T) {
	samples := make([]float64, 0, 100)
	for i := 0; i < 97; i++ {
		samples = append(samples, 13+float64(i%3)*0.5)
	}
	// Two connects paid the 1 s SYN retransmit.
	samples = append(samples, 1013.2, 1041.7)

	r := summarizeConnectLoss("192.0.2.1:80", 100, samples, 1)

	if r.Retried != 2 || r.Failed != 1 {
		t.Fatalf("retried=%d failed=%d, want 2 and 1", r.Retried, r.Failed)
	}
	if r.LossPct != 3 {
		t.Fatalf("loss_pct=%v, want 3", r.LossPct)
	}
	if r.ThresholdMs < 900 || r.ThresholdMs > 920 {
		t.Fatalf("threshold_ms=%v, want median + 900", r.ThresholdMs)
	}
}

func TestSummarizeConnectLoss_BufferbloatBelowThresholdIsNotLoss(t *testing.T) {
	samples := []float64{40, 45, 50, 60, 120, 300, 600, 850}

	r := summarizeConnectLoss("192.0.2.1:80", len(samples), samples, 0)

	if r.Retried != 0 || r.LossPct != 0 {
		t.Fatalf("retried=%d loss_pct=%v, want no loss", r.Retried, r.LossPct)
	}
}

func TestIdleLossWindow(t *testing.T) {
	now := time.Unix(1_000_000, 0)
	cases := []struct {
		name    string
		startAt int64
		want    time.Duration
	}{
		{"no synchronized start", 0, 10 * time.Second},
		{"start far enough out", now.Unix() + 30, 10 * time.Second},
		{"start trims the window", now.Unix() + 9, 5 * time.Second},
		{"start too close", now.Unix() + 5, 0},
	}
	for _, c := range cases {
		if got := IdleLossWindow(10*time.Second, c.startAt, now); got != c.want {
			t.Errorf("%s: got %v, want %v", c.name, got, c.want)
		}
	}
}

func TestCorrectLoadedLatency(t *testing.T) {
	idle := &speedtest.LossResult{MedianMs: 17}
	phase := func(loadedMs, synMs float64) *speedtest.ThroughputResult {
		return &speedtest.ThroughputResult{LoadedLatencyMs: loadedMs, Loss: &speedtest.LossResult{MedianMs: synMs}}
	}

	// Server-side queueing on /ping: 13 + (21 - 17) = 17 replaces 40.
	p := phase(40, 21)
	if !CorrectLoadedLatency(13, idle, p) || p.LoadedLatencyMs != 17 {
		t.Fatalf("got %v, want corrected to 17", p.LoadedLatencyMs)
	}

	// /ping at or under the SYN bound stands as measured.
	p = phase(15, 21)
	if CorrectLoadedLatency(13, idle, p) || p.LoadedLatencyMs != 15 {
		t.Fatalf("got %v, want 15 untouched", p.LoadedLatencyMs)
	}

	// No idle baseline, no latency correction.
	p = phase(40, 21)
	if CorrectLoadedLatency(13, nil, p) || p.LoadedLatencyMs != 40 {
		t.Fatalf("got %v, want 40 untouched", p.LoadedLatencyMs)
	}

	// /ping jitter above the SYN jitter is capped; at or below it stands.
	p = phase(15, 21)
	p.LoadedJitterMs, p.Loss.JitterMs = 15.1, 1.2
	if !CorrectLoadedLatency(13, idle, p) || p.LoadedJitterMs != 1.2 {
		t.Fatalf("jitter got %v, want capped to 1.2", p.LoadedJitterMs)
	}
	p = phase(15, 21)
	p.LoadedJitterMs, p.Loss.JitterMs = 0.4, 1.2
	if CorrectLoadedLatency(13, idle, p) || p.LoadedJitterMs != 0.4 {
		t.Fatalf("jitter got %v, want 0.4 untouched", p.LoadedJitterMs)
	}
}

func TestConnectJitter_SendOrderSkippingRetransmits(t *testing.T) {
	// 10, 12, [1012 retransmit], 11: diffs |12-10| + |11-12| over 2 = 1.5
	if got := connectJitter([]float64{10, 12, 1012, 11}, 910); got != 1.5 {
		t.Fatalf("got %v, want 1.5", got)
	}
}

func TestSummarizeConnectLoss_AllFailedIsTotalLoss(t *testing.T) {
	r := summarizeConnectLoss("192.0.2.1:80", 50, nil, 50)

	if r.LossPct != 100 || r.Failed != 50 {
		t.Fatalf("loss_pct=%v failed=%d, want 100 and 50", r.LossPct, r.Failed)
	}
}
