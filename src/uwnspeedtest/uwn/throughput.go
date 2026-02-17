package uwn

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"net/http"
	"sync"
	"sync/atomic"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

const (
	uploadSize = 5_000_000 // 5 MB per upload request
)

// MeasureThroughput runs concurrent download or upload workers distributed
// round-robin across the selected servers. A latency probe runs concurrently
// to measure loaded latency.
func MeasureThroughput(ctx context.Context, isUpload bool, cfg UwnConfig, servers []Server, token string) (*speedtest.ThroughputResult, error) {
	duration := time.Duration(cfg.DurationSecs) * time.Second
	ctx, cancel := context.WithTimeout(ctx, duration+5*time.Second)
	defer cancel()

	var totalBytes atomic.Int64
	var activeWorkers atomic.Int32
	var wg sync.WaitGroup

	var latencyMu sync.Mutex
	var loadedLatencies []float64

	// Upload payload (shared, content is irrelevant)
	var uploadPayload []byte
	if isUpload {
		uploadPayload = make([]byte, uploadSize)
	}

	stopCh := make(chan struct{})

	// Launch throughput workers, distributed round-robin across servers
	for w := 0; w < cfg.Streams; w++ {
		server := servers[w%len(servers)]
		wg.Add(1)
		go func(srv Server) {
			defer wg.Done()
			client, err := speedtest.NewWorkerClient(60*time.Second, cfg.Interface)
			if err != nil {
				return
			}
			activeWorkers.Add(1)
			defer client.CloseIdleConnections()

			buf := make([]byte, speedtest.ReadBufferSize)

			for {
				select {
				case <-stopCh:
					return
				case <-ctx.Done():
					return
				default:
				}

				if isUpload {
					url := srv.URL + "/upload"
					cr := &speedtest.CountingReader{
						R:       bytes.NewReader(uploadPayload),
						Counter: &totalBytes,
					}
					req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, cr)
					if err != nil {
						continue
					}
					req.Header.Set("User-Agent", userAgent)
					req.Header.Set("x-test-token", token)
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
					io.Copy(io.Discard, resp.Body)
					resp.Body.Close()

					if resp.StatusCode != http.StatusOK {
						time.Sleep(100 * time.Millisecond)
					}
				} else {
					url := srv.URL + "/download"
					req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
					if err != nil {
						continue
					}
					req.Header.Set("User-Agent", userAgent)
					req.Header.Set("x-test-token", token)

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
		}(server)
	}

	// Launch latency probe against first server
	wg.Add(1)
	go func() {
		defer wg.Done()
		probeClient, err := speedtest.NewWorkerClient(10*time.Second, cfg.Interface)
		if err != nil {
			return
		}
		defer probeClient.CloseIdleConnections()

		probeURL := servers[0].URL + "/ping"
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
			req.Header.Set("User-Agent", userAgent)
			req.Header.Set("x-test-token", token)

			start := time.Now()
			resp, err := probeClient.Do(req)
			if err != nil {
				select {
				case <-stopCh:
					return
				case <-ctx.Done():
					return
				default:
					time.Sleep(speedtest.ProbeInterval)
					continue
				}
			}
			elapsed := time.Since(start).Seconds() * 1000

			io.Copy(io.Discard, resp.Body)
			resp.Body.Close()

			if elapsed > 0 {
				latencyMu.Lock()
				loadedLatencies = append(loadedLatencies, elapsed)
				latencyMu.Unlock()
			}

			select {
			case <-stopCh:
				return
			case <-ctx.Done():
				return
			case <-time.After(speedtest.ProbeInterval):
			}
		}
	}()

	// Brief wait for workers to initialize
	time.Sleep(100 * time.Millisecond)
	if activeWorkers.Load() == 0 && cfg.Streams > 0 {
		close(stopCh)
		wg.Wait()
		return nil, fmt.Errorf("no workers could bind to interface %q", cfg.Interface)
	}

	// Sample throughput at regular intervals
	var mbpsSamples []float64
	var lastBytes int64
	start := time.Now()
	lastTime := start

	for time.Since(start) < duration {
		select {
		case <-ctx.Done():
			close(stopCh)
			wg.Wait()
			return nil, ctx.Err()
		case <-time.After(speedtest.SampleInterval):
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

	close(stopCh)
	wg.Wait()

	finalBytes := totalBytes.Load()
	if len(mbpsSamples) == 0 {
		return &speedtest.ThroughputResult{Bytes: finalBytes}, nil
	}

	// Skip warmup samples, compute mean of steady-state
	skipCount := int(float64(len(mbpsSamples)) * speedtest.WarmupFraction)
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

	latencyMu.Lock()
	samples := loadedLatencies
	latencyMu.Unlock()

	loadedMedian, loadedJitter := speedtest.ComputeLatencyStats(samples)

	return &speedtest.ThroughputResult{
		Bps:             bps,
		Bytes:           finalBytes,
		LoadedLatencyMs: loadedMedian,
		LoadedJitterMs:  loadedJitter,
	}, nil
}
