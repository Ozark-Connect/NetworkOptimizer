package main

import (
	"time"
	"encoding/json"
	"fmt"
	"os"
	"strings"
)

const (
	// defaultPort sits below the AP's ephemeral floor of 32768 and is free on every AP checked.
	// 8902 was rejected deliberately: it is adjacent to Ubiquiti's wifimanserver on 8901.
	defaultPort = 8899

	// defaultListenInterface is the management bridge. Binding 0.0.0.0 would expose client PII on
	// every client VLAN the AP holds an interface on.
	defaultListenInterface = "br0"

	defaultHostapdDir      = "/var/run/hostapd"
	defaultSyslogPath      = "/var/log/messages"
	defaultSyslogTailBytes = 256 * 1024
	defaultMemoryLimitMB   = 64
	defaultProbeInterval   = 300

	// The three collection tiers are paced by how fast the underlying data actually changes.
	// Membership is pushed and costs nothing; RF metrics cost ~5 ms per VAP; identity comes from
	// mca-dump, which costs ~300 ms and is the reason the slow tier exists at all.
	defaultFastIntervalMs      = 1000
	defaultSlowIntervalSeconds = 30
	minFastIntervalMs          = 200
	maxFastIntervalMs          = 10000
	minSlowIntervalSeconds     = 10
	maxSlowIntervalSeconds     = 600

	defaultEventBufferSize   = 1024
	defaultClientTTLSeconds  = 120

	// absentGrace is how long a client may be missing from a VAP the poll actually read before it
	// is dropped. Short on purpose: the read is the evidence, so this only has to survive a missed
	// one. ClientTTLSeconds remains the bound for entries no poll has covered at all.
	absentGrace = 6 * time.Second
	defaultMaxTrackedClients = 512
	// defaultInstallDir is tmpfs. The AP agent is ephemeral by design: the config partition behind
	// /etc/persistent is 1 MB, so a Go binary cannot live there, and controller provisioning wipes
	// crontab, so there is no durable auto-run hook either. The server redeploys on every boot and
	// the AP keeps zero footprint.
	defaultInstallDir = "/tmp/netopt-apagent"
	defaultConfigPath = defaultInstallDir + "/config.json"

	// tokenEnvVar keeps the bearer token off the command line, where ps would expose it.
	tokenEnvVar = "APAGENT_TOKEN"
)

// Config is the AP agent's configuration. Every path and port is settable because the AP layout
// is unofficial surface that can move between firmware releases. The agent holds no on-disk state:
// it starts fresh on every run and has nothing to carry over from a previous one.
type Config struct {
	ListenInterface string `json:"listen_interface"`
	ListenAddress   string `json:"listen_address"`
	Port            int    `json:"port"`
	Token           string `json:"token"`
	TokenFile       string `json:"token_file"`
	HostapdDir      string `json:"hostapd_dir"`
	SyslogPath      string `json:"syslog_path"`
	SyslogTailBytes int64  `json:"syslog_tail_bytes"`
	MemoryLimitMB   int    `json:"memory_limit_mb"`
	ProbeInterval   int    `json:"probe_interval_seconds"`
	BoardInfoPath   string `json:"board_info_path"`
	FirmwarePath    string `json:"firmware_path"`

	FastIntervalMs      int `json:"fast_interval_ms"`
	SlowIntervalSeconds int `json:"slow_interval_seconds"`
	EventBufferSize     int `json:"event_buffer_size"`
	ClientTTLSeconds    int `json:"client_ttl_seconds"`
	MaxTrackedClients   int `json:"max_tracked_clients"`
}

// Overrides are the flag values that win over the config file when set.
type Overrides struct {
	ListenInterface string
	ListenAddress   string
	Port            int
	TokenFile       string
	HostapdDir      string
	SyslogPath      string
	FastIntervalMs  int
	SlowInterval    int
}

func loadConfig(path string, ov Overrides) (*Config, error) {
	cfg := &Config{}

	if path != "" {
		data, err := os.ReadFile(path)
		switch {
		case err == nil:
			if err := json.Unmarshal(data, cfg); err != nil {
				return nil, fmt.Errorf("parse config %s: %w", path, err)
			}
		case os.IsNotExist(err):
			// Defaults plus APAGENT_TOKEN is a valid configuration, so an absent file is fine.
		default:
			return nil, fmt.Errorf("read config %s: %w", path, err)
		}
	}

	applyOverrides(cfg, ov)
	applyDefaults(cfg)

	if err := resolveToken(cfg); err != nil {
		return nil, err
	}
	if err := validateConfig(cfg); err != nil {
		return nil, err
	}
	return cfg, nil
}

func applyOverrides(cfg *Config, ov Overrides) {
	if ov.ListenInterface != "" {
		cfg.ListenInterface = ov.ListenInterface
	}
	if ov.ListenAddress != "" {
		cfg.ListenAddress = ov.ListenAddress
	}
	if ov.Port != 0 {
		cfg.Port = ov.Port
	}
	if ov.TokenFile != "" {
		cfg.TokenFile = ov.TokenFile
	}
	if ov.HostapdDir != "" {
		cfg.HostapdDir = ov.HostapdDir
	}
	if ov.SyslogPath != "" {
		cfg.SyslogPath = ov.SyslogPath
	}
	if ov.FastIntervalMs != 0 {
		cfg.FastIntervalMs = ov.FastIntervalMs
	}
	if ov.SlowInterval != 0 {
		cfg.SlowIntervalSeconds = ov.SlowInterval
	}
}

func applyDefaults(cfg *Config) {
	if cfg.ListenInterface == "" {
		cfg.ListenInterface = defaultListenInterface
	}
	if cfg.Port == 0 {
		cfg.Port = defaultPort
	}
	if cfg.HostapdDir == "" {
		cfg.HostapdDir = defaultHostapdDir
	}
	if cfg.SyslogPath == "" {
		cfg.SyslogPath = defaultSyslogPath
	}
	if cfg.SyslogTailBytes <= 0 {
		cfg.SyslogTailBytes = defaultSyslogTailBytes
	}
	if cfg.MemoryLimitMB <= 0 {
		cfg.MemoryLimitMB = defaultMemoryLimitMB
	}
	if cfg.ProbeInterval <= 0 {
		cfg.ProbeInterval = defaultProbeInterval
	}
	if cfg.BoardInfoPath == "" {
		cfg.BoardInfoPath = "/etc/board.info"
	}
	if cfg.FirmwarePath == "" {
		cfg.FirmwarePath = "/usr/lib/version"
	}
	if cfg.FastIntervalMs <= 0 {
		cfg.FastIntervalMs = defaultFastIntervalMs
	}
	if cfg.SlowIntervalSeconds <= 0 {
		cfg.SlowIntervalSeconds = defaultSlowIntervalSeconds
	}
	if cfg.EventBufferSize <= 0 {
		cfg.EventBufferSize = defaultEventBufferSize
	}
	if cfg.ClientTTLSeconds <= 0 {
		cfg.ClientTTLSeconds = defaultClientTTLSeconds
	}
	if cfg.MaxTrackedClients <= 0 {
		cfg.MaxTrackedClients = defaultMaxTrackedClients
	}
}

// resolveToken takes the token from the environment first, then a token file, then the config
// file. There is deliberately no -token flag: ps would expose it to every user on the AP.
func resolveToken(cfg *Config) error {
	if v := strings.TrimSpace(os.Getenv(tokenEnvVar)); v != "" {
		cfg.Token = v
		return nil
	}
	if cfg.TokenFile != "" {
		data, err := os.ReadFile(cfg.TokenFile)
		if err != nil {
			return fmt.Errorf("read token_file %s: %w", cfg.TokenFile, err)
		}
		cfg.Token = strings.TrimSpace(firstLine(string(data)))
	}
	cfg.Token = strings.TrimSpace(cfg.Token)
	return nil
}

func validateConfig(cfg *Config) error {
	// The payload is client PII, so an unauthenticated listener is refused rather than warned about.
	if cfg.Token == "" {
		return fmt.Errorf("no bearer token: set %s, token_file, or token in the config file", tokenEnvVar)
	}
	if len(cfg.Token) < 16 {
		return fmt.Errorf("bearer token is %d characters, 16 is the minimum", len(cfg.Token))
	}
	if cfg.Port < 1 || cfg.Port > 65535 {
		return fmt.Errorf("port %d is out of range", cfg.Port)
	}
	if cfg.Port >= 32768 {
		return fmt.Errorf("port %d is in the AP's ephemeral range and can collide with an outbound socket", cfg.Port)
	}
	if cfg.ListenAddress == "" && cfg.ListenInterface == "" {
		return fmt.Errorf("one of listen_address or listen_interface is required")
	}
	// The cadence is refused rather than clamped: a busy AP is turned down deliberately, and a
	// silently corrected value would hide a typo in a deployed config.
	if cfg.FastIntervalMs < minFastIntervalMs || cfg.FastIntervalMs > maxFastIntervalMs {
		return fmt.Errorf("fast_interval_ms %d is outside %d to %d", cfg.FastIntervalMs, minFastIntervalMs, maxFastIntervalMs)
	}
	if cfg.SlowIntervalSeconds < minSlowIntervalSeconds || cfg.SlowIntervalSeconds > maxSlowIntervalSeconds {
		return fmt.Errorf("slow_interval_seconds %d is outside %d to %d", cfg.SlowIntervalSeconds, minSlowIntervalSeconds, maxSlowIntervalSeconds)
	}
	if cfg.ClientTTLSeconds < cfg.SlowIntervalSeconds {
		return fmt.Errorf("client_ttl_seconds %d is below slow_interval_seconds %d, which would expire clients between passes",
			cfg.ClientTTLSeconds, cfg.SlowIntervalSeconds)
	}
	return nil
}

// addressForInterface picks the interface's first IPv4 address to bind. IPv4 only: the collector
// reaches the AP by its management address, which is what the console records.
func addressForInterface(ifaces []InterfaceInfo, name string) (string, error) {
	for _, ifi := range ifaces {
		if ifi.Name != name {
			continue
		}
		for _, addr := range ifi.Addresses {
			ip, _, found := strings.Cut(addr, "/")
			if !found {
				ip = addr
			}
			if strings.Count(ip, ".") == 3 && !strings.Contains(ip, ":") {
				return ip, nil
			}
		}
		return "", fmt.Errorf("interface %s has no IPv4 address", name)
	}
	return "", fmt.Errorf("interface %s not found (have: %s)", name, strings.Join(interfaceNames(ifaces), ", "))
}

func interfaceNames(ifaces []InterfaceInfo) []string {
	names := make([]string, 0, len(ifaces))
	for _, ifi := range ifaces {
		names = append(names, ifi.Name)
	}
	return names
}
