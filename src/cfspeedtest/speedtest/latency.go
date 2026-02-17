package speedtest

import (
	"context"
	"fmt"
	"io"
	"math"
	"net/http"
	"sort"
	"time"
)

// MeasureLatency performs 20 sequential zero-byte downloads to measure
// unloaded latency and jitter, using Server-Timing to isolate network RTT
// from server processing time.
func MeasureLatency(ctx context.Context, client *http.Client) (*LatencyResult, error) {
	url := baseURL + "/" + downloadPath + "0"
	var latencies []float64

	// Warmup request to establish TCP+TLS connection before timing begins.
	// Without this, the first sample includes handshake overhead (~80-100ms).
	if warmReq, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil); err == nil {
		if warmResp, err := client.Do(warmReq); err == nil {
			io.Copy(io.Discard, warmResp.Body)
			warmResp.Body.Close()
		}
	}

	for i := 0; i < 20; i++ {
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
		if err != nil {
			return nil, fmt.Errorf("create latency request: %w", err)
		}
		req.Header.Set("User-Agent", "cfspeedtest/1.0")

		start := time.Now()
		resp, err := client.Do(req)
		elapsed := time.Since(start).Seconds() * 1000 // ms
		if err != nil {
			return nil, fmt.Errorf("latency request %d: %w", i, err)
		}
		io.Copy(io.Discard, resp.Body)
		resp.Body.Close()

		serverMs := parseServerTiming(resp)
		latency := elapsed - serverMs
		if latency < 0 {
			latency = 0
		}
		latencies = append(latencies, latency)
	}

	sort.Float64s(latencies)

	// Median
	n := len(latencies)
	var median float64
	if n%2 == 0 {
		median = (latencies[n/2-1] + latencies[n/2]) / 2.0
	} else {
		median = latencies[n/2]
	}

	// Jitter: average of consecutive differences on sorted samples
	var jitter float64
	if n >= 2 {
		var sum float64
		for i := 1; i < n; i++ {
			sum += math.Abs(latencies[i] - latencies[i-1])
		}
		jitter = sum / float64(n-1)
	}

	return &LatencyResult{
		UnloadedMs: math.Round(median*10) / 10,
		JitterMs:   math.Round(jitter*10) / 10,
	}, nil
}

// ComputeLatencyStats computes median and jitter from a slice of latency samples.
// Used for loaded latency during throughput tests.
func ComputeLatencyStats(samples []float64) (median, jitter float64) {
	if len(samples) == 0 {
		return 0, 0
	}

	sorted := make([]float64, len(samples))
	copy(sorted, samples)
	sort.Float64s(sorted)

	n := len(sorted)
	if n%2 == 0 {
		median = (sorted[n/2-1] + sorted[n/2]) / 2.0
	} else {
		median = sorted[n/2]
	}

	if n >= 2 {
		var sum float64
		for i := 1; i < n; i++ {
			sum += math.Abs(sorted[i] - sorted[i-1])
		}
		jitter = sum / float64(n-1)
	}

	median = math.Round(median*10) / 10
	jitter = math.Round(jitter*10) / 10
	return
}
