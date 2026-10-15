package speedtest

import "time"

// Result is the top-level JSON output of a speed test run.
type Result struct {
	Success         bool              `json:"success"`
	Error           string            `json:"error,omitempty"`
	Metadata        *Metadata         `json:"metadata,omitempty"`
	Latency         *LatencyResult    `json:"latency,omitempty"`
	IdleLoss        *LossResult       `json:"idle_loss,omitempty"`
	Download       *ThroughputResult `json:"download,omitempty"`
	Upload          *ThroughputResult `json:"upload,omitempty"`
	Streams         int               `json:"streams,omitempty"`
	DurationSeconds int               `json:"duration_seconds,omitempty"`
	Timestamp       time.Time         `json:"timestamp"`
}

// Metadata from the speed test provider.
type Metadata struct {
	IP         string   `json:"ip"`
	Colo       string   `json:"colo"`
	Country    string   `json:"country"`
	ServerHost string   `json:"server_host,omitempty"`
	ServerIPs  []string `json:"server_ips,omitempty"`
}

// LatencyResult holds unloaded latency measurement.
type LatencyResult struct {
	UnloadedMs float64 `json:"unloaded_ms"`
	JitterMs   float64 `json:"jitter_ms"`
}

// ThroughputResult holds download or upload measurement.
type ThroughputResult struct {
	Bps             float64 `json:"bps"`
	Bytes           int64   `json:"bytes"`
	LoadedLatencyMs float64 `json:"loaded_latency_ms"`
	LoadedJitterMs  float64 `json:"loaded_jitter_ms"`
	Loss            *LossResult `json:"loss,omitempty"`
	// Measurement window (after warmup), for matching the WAN's own counters to the test.
	StartedAt *time.Time `json:"started_at,omitempty"`
	EndedAt   *time.Time `json:"ended_at,omitempty"`
}

// LossResult holds packet loss measured by a probe burst. For "tcp_connect", a connect that
// failed or paid a SYN retransmit (Retried) counts as one lost packet.
type LossResult struct {
	Method      string  `json:"method"`
	Target      string  `json:"target"`
	Sent        int     `json:"sent"`
	Retried     int     `json:"retried"`
	Failed      int     `json:"failed"`
	LossPct     float64 `json:"loss_pct"`
	MedianMs    float64 `json:"median_ms"`
	P95Ms       float64 `json:"p95_ms"`
	MaxMs       float64 `json:"max_ms"`
	ThresholdMs float64 `json:"threshold_ms"`
	JitterMs    float64 `json:"jitter_ms"`
	// When probes were sent (in-flight connects finish after EndedAt).
	StartedAt *time.Time `json:"started_at,omitempty"`
	EndedAt   *time.Time `json:"ended_at,omitempty"`
}

// Config holds test parameters.
type Config struct {
	Streams       int
	Duration      time.Duration
	DownloadSize  int
	UploadSize    int
	DownloadOnly  bool
	UploadOnly    bool
	Timeout       time.Duration
	Interface     string // Network interface to bind to (e.g. "eth2")
}

// DefaultConfig returns sensible defaults matching the C# service.
func DefaultConfig() Config {
	return Config{
		Streams:      8,
		Duration:     10 * time.Second,
		DownloadSize: 10_000_000, // 10 MB
		UploadSize:   5_000_000,  // 5 MB
		Timeout:      90 * time.Second,
	}
}
