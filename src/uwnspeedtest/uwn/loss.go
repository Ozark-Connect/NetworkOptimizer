package uwn

import (
	"context"
	"math"
	"net"
	"net/url"
	"sort"
	"sync"
	"time"

	"github.com/Ozark-Connect/NetworkOptimizer/src/cfspeedtest/speedtest"
)

const (
	lossProbeInterval = 20 * time.Millisecond
	// Long enough for the first SYN retransmit (1 s) and the second (3 s) to complete.
	lossConnectTimeout = 3500 * time.Millisecond
	// A lost SYN or SYN-ACK costs one initial RTO (1 s on Linux/macOS). A connect this much
	// slower than the median paid a retransmit, which bufferbloat alone rarely produces.
	lossRetransmitMarginMs = 900.0
)

// minIdleLossWindow is the shortest idle phase worth running (100 probes).
const minIdleLossWindow = 2 * time.Second

// IdleLossWindow returns how long the idle phase may run. With a synchronized start it must finish,
// in-flight connects included, before throughput begins on every WAN; 0 means skip it.
func IdleLossWindow(requested time.Duration, startAt int64, now time.Time) time.Duration {
	window := requested
	if startAt > 0 {
		if remaining := time.Unix(startAt, 0).Sub(now) - lossConnectTimeout; remaining < window {
			window = remaining
		}
	}
	if window < minIdleLossWindow {
		return 0
	}
	return window.Truncate(time.Second)
}

// lossTargetCandidates are probed in order for a loss target off the throughput servers, so
// loss under load is not mixed with the test server's own SYN handling while it is saturated.
// speed.cloudflare.com is built for speed test traffic, so the connect burst is load it expects.
// 8.8.8.8 answers only on 443.
var lossTargetCandidates = []string{"speed.cloudflare.com:443", "google.com:80", "1.1.1.1:80", "8.8.8.8:443"}

const (
	lossTargetChecks     = 5
	lossTargetMinAnswers = 4
	lossTargetTimeout    = time.Second
)

// SelectLossTarget returns the first candidate (resolved once to IPv4) that answers
// lossTargetMinAnswers of lossTargetChecks connects, else the throughput server's own address.
// neutral is false for that fallback: its timings share the test's own saturation.
func SelectLossTarget(ctx context.Context, server Server, ifaceName string) (addr string, neutral bool, err error) {
	dialer, err := newProbeDialer(ifaceName, lossTargetTimeout)
	if err != nil {
		return "", false, err
	}
	for _, candidate := range lossTargetCandidates {
		host, port, err := net.SplitHostPort(candidate)
		if err != nil {
			continue
		}
		ips, err := net.DefaultResolver.LookupIP(ctx, "ip4", host)
		if err != nil || len(ips) == 0 {
			continue
		}
		addr := net.JoinHostPort(ips[0].String(), port)
		answers, misses := 0, 0
		for i := 0; i < lossTargetChecks && misses <= lossTargetChecks-lossTargetMinAnswers; i++ {
			if conn, err := dialer.DialContext(ctx, "tcp", addr); err == nil {
				conn.Close()
				answers++
			} else {
				misses++
			}
		}
		if answers >= lossTargetMinAnswers {
			return addr, true, nil
		}
	}
	fallback, err := serverDialAddr(server)
	return fallback, false, err
}

// CorrectLoadedLatency caps a phase's loaded latency at the idle ping plus the loaded-minus-idle
// SYN delta to the loss target, and its loaded jitter at the SYN jitter. Whatever the /ping probe
// reads above those is the saturated test server's own queueing, not the access link's. Returns
// true when either figure was lowered.
func CorrectLoadedLatency(pingMs float64, idle *speedtest.LossResult, phase *speedtest.ThroughputResult) bool {
	if phase == nil || phase.Loss == nil {
		return false
	}
	corrected := false
	if idle != nil && idle.MedianMs > 0 && phase.Loss.MedianMs > 0 && phase.LoadedLatencyMs > 0 {
		if bound := round1(pingMs + math.Max(0, phase.Loss.MedianMs-idle.MedianMs)); bound < phase.LoadedLatencyMs {
			phase.LoadedLatencyMs = bound
			corrected = true
		}
	}
	if phase.Loss.JitterMs > 0 && phase.Loss.JitterMs < phase.LoadedJitterMs {
		phase.LoadedJitterMs = phase.Loss.JitterMs
		corrected = true
	}
	return corrected
}

// MeasureConnectLoss opens a TCP connection to host:port every lossProbeInterval until the
// window elapses or stop closes, then waits for the connects in flight. A connect that fails,
// or that took at least the median plus lossRetransmitMarginMs, counts as a lost packet.
func MeasureConnectLoss(ctx context.Context, host string, ifaceName string, window time.Duration, stop <-chan struct{}) (*speedtest.LossResult, error) {
	dialer, err := newProbeDialer(ifaceName, lossConnectTimeout)
	if err != nil {
		return nil, err
	}

	type timedConnect struct {
		seq int
		ms  float64
	}
	var mu sync.Mutex
	var connects []timedConnect
	failed := 0

	var wg sync.WaitGroup
	ticker := time.NewTicker(lossProbeInterval)
	defer ticker.Stop()
	deadline := time.After(window)
	sent := 0
	startedAt := time.Now().UTC()
	var endedAt time.Time

send:
	for {
		wg.Add(1)
		sent++
		seq := sent
		go func() {
			defer wg.Done()
			dialCtx, cancel := context.WithTimeout(ctx, lossConnectTimeout)
			defer cancel()
			start := time.Now()
			conn, err := dialer.DialContext(dialCtx, "tcp", host)
			elapsed := time.Since(start).Seconds() * 1000
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				failed++
				return
			}
			conn.Close()
			connects = append(connects, timedConnect{seq, elapsed})
		}()

		select {
		case <-ctx.Done():
			break send
		case <-stop:
			break send
		case <-deadline:
			break send
		case <-ticker.C:
		}
	}
	endedAt = time.Now().UTC()
	wg.Wait()

	// Completion order is not send order; jitter needs send order.
	sort.Slice(connects, func(i, j int) bool { return connects[i].seq < connects[j].seq })
	samples := make([]float64, len(connects))
	for i, c := range connects {
		samples[i] = c.ms
	}
	result := summarizeConnectLoss(host, sent, samples, failed)
	result.StartedAt, result.EndedAt = &startedAt, &endedAt
	return result, nil
}

// summarizeConnectLoss takes completed connect times in send order.
func summarizeConnectLoss(target string, sent int, inOrder []float64, failed int) *speedtest.LossResult {
	result := &speedtest.LossResult{
		Method: "tcp_connect",
		Target: target,
		Sent:   sent,
		Failed: failed,
	}
	if len(inOrder) == 0 {
		if sent > 0 {
			result.LossPct = 100
		}
		return result
	}

	samples := append([]float64(nil), inOrder...)
	sort.Float64s(samples)
	median := percentile(samples, 0.5)
	threshold := median + lossRetransmitMarginMs

	retried := 0
	for _, s := range samples {
		if s >= threshold {
			retried++
		}
	}

	result.Retried = retried
	result.MedianMs = round1(median)
	result.P95Ms = round1(percentile(samples, 0.95))
	result.MaxMs = round1(samples[len(samples)-1])
	result.ThresholdMs = round1(threshold)
	result.JitterMs = round1(connectJitter(inOrder, threshold))
	if sent > 0 {
		result.LossPct = math.Round(float64(retried+failed)/float64(sent)*10000) / 100
	}
	return result
}

// connectJitter is the mean absolute difference between consecutive connects in send order, the
// statistic /ping jitter uses. Retransmitted connects are loss, not jitter, so they are skipped.
func connectJitter(inOrder []float64, threshold float64) float64 {
	var prev, sum float64
	n, havePrev := 0, false
	for _, s := range inOrder {
		if s >= threshold {
			continue
		}
		if havePrev {
			sum += math.Abs(s - prev)
			n++
		}
		prev, havePrev = s, true
	}
	if n == 0 {
		return 0
	}
	return sum / float64(n)
}

// percentile reads a nearest-rank percentile from sorted samples.
func percentile(sorted []float64, p float64) float64 {
	idx := int(math.Ceil(p*float64(len(sorted)))) - 1
	if idx < 0 {
		idx = 0
	}
	return sorted[idx]
}

func round1(v float64) float64 { return math.Round(v*10) / 10 }

// serverDialAddr returns host:port for TCP probes against the server URL.
func serverDialAddr(server Server) (string, error) {
	u, err := url.Parse(server.URL)
	if err != nil {
		return "", err
	}
	host := u.Host
	if _, _, err := net.SplitHostPort(host); err != nil {
		port := u.Port()
		if port == "" {
			port = "80"
		}
		host = net.JoinHostPort(u.Hostname(), port)
	}
	return host, nil
}

// newProbeDialer builds a dialer bound to the interface's address when one is given.
func newProbeDialer(ifaceName string, timeout time.Duration) (*net.Dialer, error) {
	dialer := &net.Dialer{Timeout: timeout}
	if ifaceName != "" {
		localAddr, err := speedtest.ResolveInterfaceAddr(ifaceName)
		if err != nil {
			return nil, err
		}
		dialer.LocalAddr = localAddr
	}
	return dialer, nil
}
