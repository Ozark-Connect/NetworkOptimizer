package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"net/url"
	"os"
	"strings"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
	"github.com/Ozark-Connect/NetworkOptimizer/src/uwnspeedtest/uwn"
)

var version = "dev"

func main() {
	streams := flag.Int("streams", 8, "Concurrent connections")
	duration := flag.Int("duration", 6, "Seconds per phase")
	downloadOnly := flag.Bool("download-only", false, "Skip upload")
	uploadOnly := flag.Bool("upload-only", false, "Skip download")
	timeout := flag.Int("timeout", 90, "Overall timeout seconds")
	iface := flag.String("interface", "", "Network interface to bind to (e.g. eth4)")
	showVersion := flag.Bool("version", false, "Print version")
	serverCount := flag.Int("servers", 1, "Number of servers to use for throughput")
	startAt := flag.Int64("start-at", 0, "Unix timestamp to start throughput (for synchronized parallel tests)")

	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		os.Exit(0)
	}

	cfg := uwn.UwnConfig{
		Streams:      *streams,
		DurationSecs: *duration,
		Interface:    *iface,
		ServerCount:  *serverCount,
		DownloadOnly: *downloadOnly,
		UploadOnly:   *uploadOnly,
		TimeoutSecs:  *timeout,
		StartAt:      *startAt,
	}

	result := run(cfg)

	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(result); err != nil {
		fmt.Fprintf(os.Stderr, "failed to encode JSON: %v\n", err)
		os.Exit(1)
	}

	if !result.Success {
		os.Exit(1)
	}
}

func run(cfg uwn.UwnConfig) speedtest.Result {
	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(cfg.TimeoutSecs)*time.Second)
	defer cancel()

	// Create client for discovery and latency phases
	client, err := speedtest.NewClient(speedtest.Config{Interface: cfg.Interface}, 30*time.Second)
	if err != nil {
		return errorResult("bind interface: " + err.Error())
	}

	result := speedtest.Result{
		Timestamp: time.Now().UTC(),
	}

	if cfg.Interface != "" {
		fmt.Fprintf(os.Stderr, "Binding to interface %s\n", cfg.Interface)
	}

	// Phase 1: Acquire token and IP info
	fmt.Fprintf(os.Stderr, "Acquiring test token...\n")
	token, err := uwn.FetchToken(ctx, client)
	if err != nil {
		return errorResult("token: " + err.Error())
	}

	// Fetch external IP info (non-fatal - used for WAN identification)
	var ipInfo *uwn.IpInfo
	ipInfo, err = uwn.FetchIpInfo(ctx, client)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Warning: could not fetch IP info: %v\n", err)
	} else {
		fmt.Fprintf(os.Stderr, "IP: %s (%s)\n", ipInfo.IP, ipInfo.ISP)
	}

	// Phase 2: Discover and select servers
	fmt.Fprintf(os.Stderr, "Discovering servers...\n")
	candidates, err := uwn.DiscoverServers(ctx, client)
	if err != nil {
		return errorResult("discover: " + err.Error())
	}
	fmt.Fprintf(os.Stderr, "Found %d servers, selecting best %d...\n", len(candidates), cfg.ServerCount)

	// Use IP info coords for geo sorting if available
	var clientLat, clientLon float64
	if ipInfo != nil {
		clientLat, clientLon = ipInfo.Lat, ipInfo.Lon
	}
	servers, err := uwn.SelectServers(ctx, client, token, candidates, cfg.ServerCount, clientLat, clientLon)
	if err != nil {
		return errorResult("select servers: " + err.Error())
	}

	// Build metadata from selected servers (deduplicate same-city entries)
	counts := make(map[string]int)
	var seen []string
	for _, s := range servers {
		cc := s.CountryCode
		if cc == "" {
			cc = s.Country
		}
		label := fmt.Sprintf("%s, %s", s.City, cc)
		if counts[label] == 0 {
			seen = append(seen, label)
		}
		counts[label]++
	}
	var serverInfoParts []string
	for _, label := range seen {
		if counts[label] > 1 {
			serverInfoParts = append(serverInfoParts, fmt.Sprintf("%s (x%d)", label, counts[label]))
		} else {
			serverInfoParts = append(serverInfoParts, label)
		}
	}
	// Extract primary server host for path analysis
	var serverHost string
	if u, err := url.Parse(servers[0].URL); err == nil {
		serverHost = u.Hostname()
	}
	result.Metadata = &speedtest.Metadata{
		Colo:       strings.Join(serverInfoParts, " | "),
		ServerHost: serverHost,
	}
	if ipInfo != nil {
		result.Metadata.IP = ipInfo.IP
		result.Metadata.Country = ipInfo.ISP
	}
	fmt.Fprintf(os.Stderr, "Servers: %s\n", result.Metadata.Colo)

	// Phase 3: Unloaded latency (against best server)
	fmt.Fprintf(os.Stderr, "Measuring latency...\n")
	latency, err := uwn.MeasureLatency(ctx, servers[0], cfg.Interface)
	if err != nil {
		return errorResult("latency: " + err.Error())
	}
	result.Latency = latency
	fmt.Fprintf(os.Stderr, "Latency: %.1f ms (jitter: %.1f ms)\n", latency.UnloadedMs, latency.JitterMs)

	// Synchronized start: wait until the specified time before starting throughput
	if cfg.StartAt > 0 {
		startTime := time.Unix(cfg.StartAt, 0)
		wait := time.Until(startTime)
		if wait > 0 {
			fmt.Fprintf(os.Stderr, "Waiting %.1fs for synchronized start...\n", wait.Seconds())
			select {
			case <-time.After(wait):
			case <-ctx.Done():
				return errorResult("timeout waiting for start")
			}
		}
		fmt.Fprintf(os.Stderr, "Starting throughput test\n")
	}

	// Phase 4: Download
	if !cfg.UploadOnly {
		fmt.Fprintf(os.Stderr, "Testing download (%d streams across %d servers, %ds)...\n", cfg.Streams, len(servers), cfg.DurationSecs)
		dl, err := uwn.MeasureThroughput(ctx, false, cfg, servers, token)
		if err != nil {
			return errorResult("download: " + err.Error())
		}
		result.Download = dl
		fmt.Fprintf(os.Stderr, "Download: %.1f Mbps\n", dl.Bps/1_000_000)
	}

	// Phase 5: Upload
	if !cfg.DownloadOnly {
		fmt.Fprintf(os.Stderr, "Testing upload (%d streams across %d servers, %ds)...\n", cfg.Streams, len(servers), cfg.DurationSecs)
		ul, err := uwn.MeasureThroughput(ctx, true, cfg, servers, token)
		if err != nil {
			return errorResult("upload: " + err.Error())
		}
		result.Upload = ul
		fmt.Fprintf(os.Stderr, "Upload: %.1f Mbps\n", ul.Bps/1_000_000)
	}

	result.Success = true
	result.Streams = cfg.Streams
	result.DurationSeconds = cfg.DurationSecs

	return result
}

func errorResult(msg string) speedtest.Result {
	return speedtest.Result{
		Success:   false,
		Error:     msg,
		Timestamp: time.Now().UTC(),
	}
}
