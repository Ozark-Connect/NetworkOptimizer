package uwn

// Server represents a UWN speed test server from the discovery API.
type Server struct {
	URL      string  `json:"url"`
	Provider string  `json:"provider"`
	City     string  `json:"city"`
	Country  string  `json:"country"`
	Lat      float64 `json:"lat"`
	Lon      float64 `json:"lon"`

	// Set after latency probing
	LatencyMs float64 `json:"-"`
}

// tokenResponse is the JSON response from the token endpoint.
type tokenResponse struct {
	Token string `json:"token"`
	TTL   int    `json:"ttl"`
}

// UwnConfig extends the common speedtest config with UWN-specific settings.
type UwnConfig struct {
	Streams      int
	DurationSecs int
	Interface    string
	ServerCount  int
	DownloadOnly bool
	UploadOnly   bool
	TimeoutSecs  int
}
