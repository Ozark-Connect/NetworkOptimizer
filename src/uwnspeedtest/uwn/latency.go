package uwn

import (
	"context"
	"fmt"
	"io"
	"math"
	"net/http"
	"sort"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

// MeasureLatency performs sequential pings to the server's /ping endpoint
// to measure unloaded latency and jitter. Tolerates individual failures.
func MeasureLatency(ctx context.Context, client *http.Client, server Server, token string) (*speedtest.LatencyResult, error) {
	pingURL := server.URL + "/ping"
	var latencies []float64

	// Warmup request to establish TCP connection before timing begins.
	warmCtx, warmCancel := context.WithTimeout(ctx, pingTimeout)
	if warmReq, err := http.NewRequestWithContext(warmCtx, http.MethodGet, pingURL, nil); err == nil {
		warmReq.Header.Set("User-Agent", userAgent)
		warmReq.Header.Set("x-test-token", token)
		if warmResp, err := client.Do(warmReq); err == nil {
			io.Copy(io.Discard, warmResp.Body)
			warmResp.Body.Close()
		}
	}
	warmCancel()

	for i := 0; i < 20; i++ {
		pingCtx, cancel := context.WithTimeout(ctx, pingTimeout)
		req, err := http.NewRequestWithContext(pingCtx, http.MethodGet, pingURL, nil)
		if err != nil {
			cancel()
			continue
		}
		req.Header.Set("User-Agent", userAgent)
		req.Header.Set("x-test-token", token)

		start := time.Now()
		resp, err := client.Do(req)
		elapsed := time.Since(start).Seconds() * 1000 // ms
		cancel()
		if err != nil {
			continue // skip failed pings instead of aborting
		}
		io.Copy(io.Discard, resp.Body)
		resp.Body.Close()

		if elapsed > 0 {
			latencies = append(latencies, elapsed)
		}
	}

	if len(latencies) == 0 {
		return nil, fmt.Errorf("all latency pings to %s failed", server.URL)
	}

	sort.Float64s(latencies)

	n := len(latencies)
	var median float64
	if n%2 == 0 {
		median = (latencies[n/2-1] + latencies[n/2]) / 2.0
	} else {
		median = latencies[n/2]
	}

	var jitter float64
	if n >= 2 {
		var sum float64
		for i := 1; i < n; i++ {
			sum += math.Abs(latencies[i] - latencies[i-1])
		}
		jitter = sum / float64(n-1)
	}

	return &speedtest.LatencyResult{
		UnloadedMs: math.Round(median*10) / 10,
		JitterMs:   math.Round(jitter*10) / 10,
	}, nil
}
