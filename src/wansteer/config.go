package main

import (
	"encoding/json"
	"fmt"
	"os"
)

// Config is the top-level configuration for wan-steer.
type Config struct {
	WANInterfaces       map[string]WANInterface `json:"wan_interfaces"`
	DefaultWAN          string                  `json:"default_wan"`
	ReconcileInterval   int                     `json:"reconcile_interval_seconds"`
	HealthCheckInterval int                     `json:"health_check_interval_seconds"`
	HealthCheckTimeout  int                     `json:"health_check_timeout_seconds"`
	HealthFailThreshold int                     `json:"health_fail_threshold"`
	HealthPassThreshold int                     `json:"health_pass_threshold"`
	TrafficClasses      []TrafficClass          `json:"traffic_classes"`
	StatusFile          string                  `json:"status_file"`
}

// WANInterface describes a WAN link the daemon can steer traffic to.
type WANInterface struct {
	Interface    string `json:"interface"`
	Gateway      string `json:"gateway"`
	RouteTable   string `json:"route_table"`
	FWMark       string `json:"fwmark"`
	HealthTarget string `json:"health_target"`
}

// TrafficClass describes a set of traffic to load-balance across WANs.
type TrafficClass struct {
	Name        string       `json:"name"`
	Match       MatchCriteria `json:"match"`
	Probability float64      `json:"probability"`
	TargetWAN   string       `json:"target_wan"`
	Enabled     bool         `json:"enabled"`
}

// MatchCriteria defines what traffic to match. All specified fields are ANDed together.
// Multiple values within a field (e.g., multiple dst_cidrs) are ORed - each generates
// a separate iptables rule.
type MatchCriteria struct {
	// Source matching (OR across entries, AND with other fields)
	SrcCIDRs  []string `json:"src_cidrs,omitempty"`   // e.g., ["192.168.1.0/24"] - IPs and CIDRs
	SrcRanges []string `json:"src_ranges,omitempty"` // e.g., ["192.168.1.1-192.168.1.50"] - IP ranges
	SrcMACs   []string `json:"src_macs,omitempty"`   // e.g., ["aa:bb:cc:dd:ee:ff"]

	// Destination matching (OR across entries, AND with other fields)
	DstCIDRs  []string `json:"dst_cidrs,omitempty"`   // e.g., ["162.254.192.0/21"]
	DstRanges []string `json:"dst_ranges,omitempty"` // e.g., ["162.254.192.1-162.254.199.254"]

	// Protocol and port matching (AND with source/dest)
	Protocol string   `json:"protocol,omitempty"` // "tcp", "udp", or "" for any
	SrcPorts []string `json:"src_ports,omitempty"` // e.g., ["1234", "5000-5100"]
	DstPorts []string `json:"dst_ports,omitempty"` // e.g., ["443", "27015-27030"]
}

func loadConfig(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read config: %w", err)
	}

	var cfg Config
	if err := json.Unmarshal(data, &cfg); err != nil {
		return nil, fmt.Errorf("parse config: %w", err)
	}

	if err := validateConfig(&cfg); err != nil {
		return nil, fmt.Errorf("validate config: %w", err)
	}

	// Apply defaults
	if cfg.ReconcileInterval <= 0 {
		cfg.ReconcileInterval = 30
	}
	if cfg.HealthCheckInterval <= 0 {
		cfg.HealthCheckInterval = 10
	}
	if cfg.HealthCheckTimeout <= 0 {
		cfg.HealthCheckTimeout = 3
	}
	if cfg.HealthFailThreshold <= 0 {
		cfg.HealthFailThreshold = 3
	}
	if cfg.HealthPassThreshold <= 0 {
		cfg.HealthPassThreshold = 2
	}
	if cfg.StatusFile == "" {
		cfg.StatusFile = "/tmp/wan-steer-status.json"
	}

	return &cfg, nil
}

func validateConfig(cfg *Config) error {
	if len(cfg.WANInterfaces) == 0 {
		return fmt.Errorf("no wan_interfaces defined")
	}
	if cfg.DefaultWAN == "" {
		return fmt.Errorf("default_wan is required")
	}
	if _, ok := cfg.WANInterfaces[cfg.DefaultWAN]; !ok {
		return fmt.Errorf("default_wan %q not found in wan_interfaces", cfg.DefaultWAN)
	}
	for i, tc := range cfg.TrafficClasses {
		if tc.Name == "" {
			return fmt.Errorf("traffic_classes[%d]: name is required", i)
		}
		if err := validateMatch(&tc.Match, i, tc.Name); err != nil {
			return err
		}
		if tc.Probability <= 0 || tc.Probability > 1 {
			return fmt.Errorf("traffic_classes[%d] %q: probability must be between 0 and 1", i, tc.Name)
		}
		if tc.TargetWAN == "" {
			return fmt.Errorf("traffic_classes[%d] %q: target_wan is required", i, tc.Name)
		}
		if _, ok := cfg.WANInterfaces[tc.TargetWAN]; !ok {
			return fmt.Errorf("traffic_classes[%d] %q: target_wan %q not found in wan_interfaces", i, tc.Name, tc.TargetWAN)
		}
	}
	return nil
}

func validateMatch(m *MatchCriteria, idx int, name string) error {
	// Must have at least one match criterion
	if len(m.SrcCIDRs) == 0 && len(m.SrcRanges) == 0 && len(m.SrcMACs) == 0 &&
		len(m.DstCIDRs) == 0 && len(m.DstRanges) == 0 &&
		m.Protocol == "" && len(m.SrcPorts) == 0 && len(m.DstPorts) == 0 {
		return fmt.Errorf("traffic_classes[%d] %q: match must have at least one criterion", idx, name)
	}

	// Protocol is required if ports are specified
	if (len(m.SrcPorts) > 0 || len(m.DstPorts) > 0) && m.Protocol == "" {
		return fmt.Errorf("traffic_classes[%d] %q: protocol is required when ports are specified", idx, name)
	}

	// Protocol must be tcp or udp if specified
	if m.Protocol != "" && m.Protocol != "tcp" && m.Protocol != "udp" {
		return fmt.Errorf("traffic_classes[%d] %q: protocol must be \"tcp\" or \"udp\"", idx, name)
	}

	return nil
}
