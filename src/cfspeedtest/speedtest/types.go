package speedtest

import "time"

// Result is the top-level JSON output of a speed test run.
type Result struct {
	Success         bool              `json:"success"`
	Error           string            `json:"error,omitempty"`
	Metadata        *Metadata         `json:"metadata,omitempty"`
	Latency         *LatencyResult    `json:"latency,omitempty"`
	Download        *ThroughputResult `json:"download,omitempty"`
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
