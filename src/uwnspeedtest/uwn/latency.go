package uwn

import (
	"context"
	"fmt"
	"math"
	"net"
	"net/url"
	"sort"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

// MeasureLatency measures TCP connect time (SYN/ACK round trip) to the server.
// This gives true network RTT without HTTP overhead. Binds to the specified
// interface if provided.
func MeasureLatency(ctx context.Context, server Server, ifaceName string) (*speedtest.LatencyResult, error) {
	u, err := url.Parse(server.URL)
	if err != nil {
		return nil, fmt.Errorf("parse server URL: %w", err)
	}
	host := u.Host
	if _, _, err := net.SplitHostPort(host); err != nil {
		port := u.Port()
		if port == "" {
			port = "80"
		}
		host = net.JoinHostPort(u.Hostname(), port)
	}

	dialer := &net.Dialer{Timeout: pingTimeout}
	if ifaceName != "" {
		localAddr, err := speedtest.ResolveInterfaceAddr(ifaceName)
		if err != nil {
			return nil, fmt.Errorf("resolve interface for latency: %w", err)
		}
		dialer.LocalAddr = localAddr
	}

	var latencies []float64

	// Warmup
	warmCtx, warmCancel := context.WithTimeout(ctx, pingTimeout)
	if conn, err := dialer.DialContext(warmCtx, "tcp", host); err == nil {
		conn.Close()
	}
	warmCancel()

	for i := 0; i < 20; i++ {
		pingCtx, cancel := context.WithTimeout(ctx, pingTimeout)
		start := time.Now()
		conn, err := dialer.DialContext(pingCtx, "tcp", host)
		elapsed := time.Since(start).Seconds() * 1000
		cancel()
		if err != nil {
			continue
		}
		conn.Close()

		if elapsed > 0 {
			latencies = append(latencies, elapsed)
		}
	}

	if len(latencies) == 0 {
		return nil, fmt.Errorf("all TCP connect attempts to %s failed", host)
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
