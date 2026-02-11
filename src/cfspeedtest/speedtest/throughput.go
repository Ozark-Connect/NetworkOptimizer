package speedtest

import (
	"bytes"
	"context"
	"fmt"
	"net/http"
	"sync"
	"sync/atomic"
	"time"
)

const (
	minDownloadChunkSize = 100_000 // Floor for adaptive chunk reduction on 429
	sampleInterval       = 200 * time.Millisecond
	probeInterval        = 500 * time.Millisecond
	warmupFraction       = 0.20 // Skip first 20% of samples
	readBufferSize       = 81920 // 80 KB read buffer per worker
)

// newWorkerClient creates an HTTP client that forces HTTP/1.1 and optionally
// binds to a specific interface, ensuring each worker gets its own TCP connection.
func newWorkerClient(timeout time.Duration, ifaceName string) (*http.Client, error) {
	t, err := newTransport(ifaceName)
	if err != nil {
		return nil, err
	}
	return &http.Client{
		Timeout:   timeout,
		Transport: t,
	}, nil
}

// countingReader wraps a reader and atomically adds bytes read to a counter.
// This allows upload throughput to be sampled incrementally as data is sent,
// matching the C# ProgressContent approach.
type countingReader struct {
	r       *bytes.Reader
	counter *atomic.Int64
}

func (cr *countingReader) Read(p []byte) (int, error) {
	n, err := cr.r.Read(p)
	if n > 0 {
		cr.counter.Add(int64(n))
	}
	return n, err
}

// MeasureThroughput runs concurrent download or upload workers for the given
// duration, sampling aggregate throughput every 200ms. A concurrent latency
// probe measures loaded latency every 500ms.
func MeasureThroughput(ctx context.Context, isUpload bool, cfg Config) (*ThroughputResult, error) {
	ctx, cancel := context.WithTimeout(ctx, cfg.Duration+5*time.Second)
	defer cancel()

	var totalBytes atomic.Int64
	var wg sync.WaitGroup

	// Loaded latency probe samples
	var latencyMu sync.Mutex
	var loadedLatencies []float64

	// Upload payload (shared, content is irrelevant)
	var uploadPayload []byte
	if isUpload {
		uploadPayload = make([]byte, cfg.UploadSize)
	}

	chunkSize := cfg.DownloadSize
	if isUpload {
		chunkSize = cfg.UploadSize
	}

	// Signal to stop workers when duration expires
	stopCh := make(chan struct{})

	// Launch throughput workers
	for w := 0; w < cfg.Streams; w++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			client, err := newWorkerClient(60*time.Second, cfg.Interface)
			if err != nil {
				return // interface binding failed; will show as lower throughput
			}
			defer client.CloseIdleConnections()

			workerChunk := chunkSize
			buf := make([]byte, readBufferSize) // per-worker read buffer

			for {
				select {
				case <-stopCh:
					return
				case <-ctx.Done():
					return
				default:
				}

				if isUpload {
					url := baseURL + "/" + uploadPath
					cr := &countingReader{
						r:       bytes.NewReader(uploadPayload),
						counter: &totalBytes,
					}
					req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, cr)
					if err != nil {
						continue
					}
					req.Header.Set("User-Agent", "cfspeedtest/1.0")
					req.ContentLength = int64(len(uploadPayload))

					resp, err := client.Do(req)
					if err != nil {
						select {
						case <-stopCh:
							return
						case <-ctx.Done():
							return
						default:
							time.Sleep(100 * time.Millisecond)
							continue
						}
					}
					resp.Body.Close()

					if resp.StatusCode != http.StatusOK {
						time.Sleep(100 * time.Millisecond)
					}
				} else {
					url := fmt.Sprintf("%s/%s%d", baseURL, downloadPath, workerChunk)
					req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
					if err != nil {
						continue
					}
					req.Header.Set("User-Agent", "cfspeedtest/1.0")

					resp, err := client.Do(req)
					if err != nil {
						select {
						case <-stopCh:
							return
						case <-ctx.Done():
							return
						default:
							time.Sleep(100 * time.Millisecond)
							continue
						}
					}

					if resp.StatusCode != http.StatusOK {
						resp.Body.Close()
						// On 429: halve chunk size (matching cloudflare-speed-cli behavior)
						if resp.StatusCode == 429 {
							next := workerChunk / 2
							if next < minDownloadChunkSize {
								next = minDownloadChunkSize
							}
							if next < workerChunk {
								workerChunk = next
							}
						}
						time.Sleep(100 * time.Millisecond)
						continue
					}

					// Stream download, counting bytes incrementally
					for {
						n, err := resp.Body.Read(buf)
						if n > 0 {
							totalBytes.Add(int64(n))
						}
						if err != nil {
							break
						}
					}
					resp.Body.Close()
				}
			}
		}()
	}

	// Launch latency probe
	wg.Add(1)
	go func() {
		defer wg.Done()
		probeClient, err := newWorkerClient(10*time.Second, cfg.Interface)
		if err != nil {
			return
		}
		defer probeClient.CloseIdleConnections()

		probeURL := baseURL + "/" + downloadPath + "0"
		for {
			select {
			case <-stopCh:
				return
			case <-ctx.Done():
				return
			default:
			}

			req, err := http.NewRequestWithContext(ctx, http.MethodGet, probeURL, nil)
			if err != nil {
				continue
			}
			req.Header.Set("User-Agent", "cfspeedtest/1.0")

			start := time.Now()
			resp, err := probeClient.Do(req)
			if err != nil {
				select {
				case <-stopCh:
					return
				case <-ctx.Done():
					return
				default:
					time.Sleep(probeInterval)
					continue
				}
			}
			elapsed := time.Since(start).Seconds() * 1000

			serverMs := parseServerTiming(resp)
			resp.Body.Close()

			latency := elapsed - serverMs
			if latency > 0 {
				latencyMu.Lock()
				loadedLatencies = append(loadedLatencies, latency)
				latencyMu.Unlock()
			}

			select {
			case <-stopCh:
				return
			case <-ctx.Done():
				return
			case <-time.After(probeInterval):
			}
		}
	}()

	// Sample throughput at regular intervals
	var mbpsSamples []float64
	var lastBytes int64
	start := time.Now()
	lastTime := start

	for time.Since(start) < cfg.Duration {
		select {
		case <-ctx.Done():
			close(stopCh)
			wg.Wait()
			return nil, ctx.Err()
		case <-time.After(sampleInterval):
		}

		now := time.Now()
		currentBytes := totalBytes.Load()
		intervalBytes := currentBytes - lastBytes
		intervalSecs := now.Sub(lastTime).Seconds()

		if intervalSecs > 0.01 {
			mbps := (float64(intervalBytes) * 8.0 / 1_000_000.0) / intervalSecs
			mbpsSamples = append(mbpsSamples, mbps)
		}

		lastBytes = currentBytes
		lastTime = now
	}

	// Stop workers
	close(stopCh)
	wg.Wait()

	finalBytes := totalBytes.Load()
	if len(mbpsSamples) == 0 {
		return &ThroughputResult{Bytes: finalBytes}, nil
	}

	// Skip warmup samples, compute mean of steady-state
	skipCount := int(float64(len(mbpsSamples)) * warmupFraction)
	steadySamples := mbpsSamples[skipCount:]
	if len(steadySamples) == 0 {
		steadySamples = mbpsSamples
	}

	var sum float64
	for _, v := range steadySamples {
		sum += v
	}
	meanMbps := sum / float64(len(steadySamples))
	bps := meanMbps * 1_000_000.0

	// Compute loaded latency stats
	latencyMu.Lock()
	samples := loadedLatencies
	latencyMu.Unlock()

	loadedMedian, loadedJitter := computeLatencyStats(samples)

	return &ThroughputResult{
		Bps:             bps,
		Bytes:           finalBytes,
		LoadedLatencyMs: loadedMedian,
		LoadedJitterMs:  loadedJitter,
	}, nil
}
