package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
	"github.com/Ozark-Connect/NetworkOptimizer/src/uwnspeedtest/uwn"
)

var version = "dev"

func main() {
	streams := flag.Int("streams", 8, "Concurrent connections")
	duration := flag.Int("duration", 10, "Seconds per phase")
	downloadOnly := flag.Bool("download-only", false, "Skip upload")
	uploadOnly := flag.Bool("upload-only", false, "Skip download")
	timeout := flag.Int("timeout", 90, "Overall timeout seconds")
	iface := flag.String("interface", "", "Network interface to bind to (e.g. eth4)")
	showVersion := flag.Bool("version", false, "Print version")
	serverCount := flag.Int("servers", 1, "Number of servers to use for throughput")

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

	// Phase 1: Acquire token
	fmt.Fprintf(os.Stderr, "Acquiring test token...\n")
	token, err := uwn.FetchToken(ctx, client)
	if err != nil {
		return errorResult("token: " + err.Error())
	}

	// Phase 2: Discover and select servers
	fmt.Fprintf(os.Stderr, "Discovering servers...\n")
	candidates, err := uwn.DiscoverServers(ctx, client)
	if err != nil {
		return errorResult("discover: " + err.Error())
	}
	fmt.Fprintf(os.Stderr, "Found %d servers, selecting best %d...\n", len(candidates), cfg.ServerCount)

	// Use 0,0 for client coords - servers will be sorted by ping RTT
	servers, err := uwn.SelectServers(ctx, client, token, candidates, cfg.ServerCount, 0, 0)
	if err != nil {
		return errorResult("select servers: " + err.Error())
	}

	// Build metadata from selected servers
	var serverInfoParts []string
	for _, s := range servers {
		cc := s.CountryCode
		if cc == "" {
			cc = s.Country
		}
		serverInfoParts = append(serverInfoParts, fmt.Sprintf("%s, %s", s.City, cc))
	}
	result.Metadata = &speedtest.Metadata{
		Colo: strings.Join(serverInfoParts, ", "),
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
