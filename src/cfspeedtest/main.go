package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

var version = "dev"

func main() {
	cfg := speedtest.DefaultConfig()

	streams := flag.Int("streams", cfg.Streams, "Concurrent connections")
	duration := flag.Int("duration", int(cfg.Duration.Seconds()), "Seconds per phase")
	downloadSize := flag.Int("download-size", cfg.DownloadSize, "Download chunk bytes")
	uploadSize := flag.Int("upload-size", cfg.UploadSize, "Upload chunk bytes")
	downloadOnly := flag.Bool("download-only", false, "Skip upload")
	uploadOnly := flag.Bool("upload-only", false, "Skip download")
	timeout := flag.Int("timeout", int(cfg.Timeout.Seconds()), "Overall timeout seconds")
	iface := flag.String("interface", "", "Network interface to bind to (e.g. eth2)")
	showVersion := flag.Bool("version", false, "Print version")

	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		os.Exit(0)
	}

	cfg.Streams = *streams
	cfg.Duration = time.Duration(*duration) * time.Second
	cfg.DownloadSize = *downloadSize
	cfg.UploadSize = *uploadSize
	cfg.DownloadOnly = *downloadOnly
	cfg.UploadOnly = *uploadOnly
	cfg.Timeout = time.Duration(*timeout) * time.Second
	cfg.Interface = *iface

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

func run(cfg speedtest.Config) speedtest.Result {
	ctx, cancel := context.WithTimeout(context.Background(), cfg.Timeout)
	defer cancel()

	client, err := speedtest.NewClient(cfg, 30*time.Second)
	if err != nil {
		return errorResult("bind interface: " + err.Error())
	}

	result := speedtest.Result{
		Timestamp: time.Now().UTC(),
	}

	// Phase 1: Metadata
	if cfg.Interface != "" {
		fmt.Fprintf(os.Stderr, "Binding to interface %s\n", cfg.Interface)
	}
	fmt.Fprintf(os.Stderr, "Fetching metadata...\n")
	meta, err := speedtest.FetchMetadata(ctx, client)
	if err != nil {
		return errorResult("metadata: " + err.Error())
	}
	result.Metadata = meta
	fmt.Fprintf(os.Stderr, "Edge: %s (%s) - IP: %s\n", meta.Colo, meta.Country, meta.IP)

	// Phase 2: Unloaded latency
	fmt.Fprintf(os.Stderr, "Measuring latency...\n")
	latency, err := speedtest.MeasureLatency(ctx, client)
	if err != nil {
		return errorResult("latency: " + err.Error())
	}
	result.Latency = latency
	fmt.Fprintf(os.Stderr, "Latency: %.1f ms (jitter: %.1f ms)\n", latency.UnloadedMs, latency.JitterMs)

	// Phase 3: Download
	if !cfg.UploadOnly {
		fmt.Fprintf(os.Stderr, "Testing download (%d streams, %ds)...\n", cfg.Streams, int(cfg.Duration.Seconds()))
		dl, err := speedtest.MeasureThroughput(ctx, false, cfg)
		if err != nil {
			return errorResult("download: " + err.Error())
		}
		result.Download = dl
		fmt.Fprintf(os.Stderr, "Download: %.1f Mbps\n", dl.Bps/1_000_000)
	}

	// Phase 4: Upload
	if !cfg.DownloadOnly {
		fmt.Fprintf(os.Stderr, "Testing upload (%d streams, %ds)...\n", cfg.Streams, int(cfg.Duration.Seconds()))
		ul, err := speedtest.MeasureThroughput(ctx, true, cfg)
		if err != nil {
			return errorResult("upload: " + err.Error())
		}
		result.Upload = ul
		fmt.Fprintf(os.Stderr, "Upload: %.1f Mbps\n", ul.Bps/1_000_000)
	}

	result.Success = true
	result.Streams = cfg.Streams
	result.DurationSeconds = int(cfg.Duration.Seconds())

	return result
}

func errorResult(msg string) speedtest.Result {
	return speedtest.Result{
		Success:   false,
		Error:     msg,
		Timestamp: time.Now().UTC(),
	}
}
