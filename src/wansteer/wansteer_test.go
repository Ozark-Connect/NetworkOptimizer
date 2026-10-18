package main

import (
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// Config validation
// ---------------------------------------------------------------------------

func validConfig() *Config {
	return &Config{
		WANInterfaces: map[string]WANInterface{
			"primary": {Interface: "eth0", Gateway: "192.0.2.1", RouteTable: "100", FWMark: "0x20000", HealthTarget: "1.1.1.1"},
			"backup":  {Interface: "eth1", Gateway: "198.51.100.1", RouteTable: "101", FWMark: "0x40000", HealthTarget: "8.8.8.8"},
		},
		DefaultWAN: "primary",
		TrafficClasses: []TrafficClass{
			{
				Name:        "gaming",
				Match:       MatchCriteria{DstCIDRs: []string{"162.254.192.0/21"}},
				Probability: 1.0,
				TargetWAN:   "backup",
				Enabled:     true,
			},
		},
		// High instability threshold for existing tests so normal health
		// transitions don't trigger spurious "unstable" warnings.
		InstabilityThreshold:     100,
		InstabilityWindowSeconds: 300,
		BackoffRecoverySeconds:   60,
	}
}

func TestValidateConfig_Valid(t *testing.T) {
	if err := validateConfig(validConfig()); err != nil {
		t.Fatalf("expected valid config, got: %v", err)
	}
}

func TestValidateConfig_MissingWANInterfaces(t *testing.T) {
	cfg := validConfig()
	cfg.WANInterfaces = nil
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for missing wan_interfaces")
	}
}

func TestValidateConfig_MissingDefaultWAN(t *testing.T) {
	cfg := validConfig()
	cfg.DefaultWAN = ""
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for missing default_wan")
	}
}

func TestValidateConfig_DefaultWANNotInInterfaces(t *testing.T) {
	cfg := validConfig()
	cfg.DefaultWAN = "nonexistent"
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for default_wan not in wan_interfaces")
	}
}

func TestValidateConfig_NoMatchCriteria(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{}
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for traffic class with no match criteria")
	}
}

func TestValidateConfig_PortsWithoutProtocol(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		DstCIDRs: []string{"10.0.0.0/8"},
		DstPorts: []string{"443"},
	}
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for ports without protocol")
	}
}

func TestValidateConfig_InvalidProbability(t *testing.T) {
	tests := []struct {
		name string
		prob float64
	}{
		{"zero", 0},
		{"negative", -0.5},
		{"greater_than_one", 1.5},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := validConfig()
			cfg.TrafficClasses[0].Probability = tt.prob
			if err := validateConfig(cfg); err == nil {
				t.Fatalf("expected error for probability %v", tt.prob)
			}
		})
	}
}

func TestValidateConfig_InvalidTargetWAN(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].TargetWAN = "nonexistent"
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for invalid target_wan")
	}
}

func TestValidateConfig_EmptyTargetWAN(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].TargetWAN = ""
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for empty target_wan")
	}
}

func TestValidateMatch_SrcRangesCountAsValid(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		SrcRanges: []string{"192.168.1.1-192.168.1.50"},
	}
	if err := validateConfig(cfg); err != nil {
		t.Fatalf("SrcRanges should be valid match criteria, got: %v", err)
	}
}

func TestValidateMatch_DstRangesCountAsValid(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		DstRanges: []string{"10.0.0.1-10.0.0.254"},
	}
	if err := validateConfig(cfg); err != nil {
		t.Fatalf("DstRanges should be valid match criteria, got: %v", err)
	}
}

func TestValidateMatch_SrcMACsCountAsValid(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		SrcMACs: []string{"aa:bb:cc:dd:ee:ff"},
	}
	if err := validateConfig(cfg); err != nil {
		t.Fatalf("SrcMACs should be valid match criteria, got: %v", err)
	}
}

func TestValidateMatch_ProtocolAloneIsValid(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{Protocol: "tcp"}
	if err := validateConfig(cfg); err != nil {
		t.Fatalf("protocol alone should be valid match criteria, got: %v", err)
	}
}

func TestValidateMatch_InvalidProtocol(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{Protocol: "icmp"}
	if err := validateConfig(cfg); err == nil {
		t.Fatal("expected error for invalid protocol")
	}
}

// ---------------------------------------------------------------------------
// Port normalization
// ---------------------------------------------------------------------------

func TestNormalizePortsForIptables_DashToColon(t *testing.T) {
	got := normalizePortsForIptables([]string{"27015-27030"})
	if len(got) != 1 || got[0] != "27015:27030" {
		t.Fatalf("expected [27015:27030], got %v", got)
	}
}

func TestNormalizePortsForIptables_SinglePort(t *testing.T) {
	got := normalizePortsForIptables([]string{"443"})
	if len(got) != 1 || got[0] != "443" {
		t.Fatalf("expected [443], got %v", got)
	}
}

func TestNormalizePortsForIptables_Multiple(t *testing.T) {
	got := normalizePortsForIptables([]string{"443", "8080-8090"})
	if len(got) != 2 {
		t.Fatalf("expected 2 results, got %d", len(got))
	}
	if got[0] != "443" {
		t.Errorf("expected 443, got %s", got[0])
	}
	if got[1] != "8080:8090" {
		t.Errorf("expected 8080:8090, got %s", got[1])
	}
}

func TestNormalizePortsForIptables_Empty(t *testing.T) {
	got := normalizePortsForIptables([]string{})
	if len(got) != 0 {
		t.Fatalf("expected empty result, got %v", got)
	}
}

// ---------------------------------------------------------------------------
// Expected rule count
// ---------------------------------------------------------------------------

func TestExpectedRuleCount_SingleCIDR(t *testing.T) {
	cfg := validConfig() // 1 dst CIDR, no src = 1*1*2 = 2
	got := expectedRuleCount(cfg, nil)
	if got != 2 {
		t.Fatalf("expected 2, got %d", got)
	}
}

func TestExpectedRuleCount_MultipleDstCIDRs(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match.DstCIDRs = []string{"10.0.0.0/8", "172.16.0.0/12"}
	got := expectedRuleCount(cfg, nil)
	// 2 dst * 1 src * 2 = 4
	if got != 4 {
		t.Fatalf("expected 4, got %d", got)
	}
}

func TestExpectedRuleCount_SrcAndDstCrossProduct(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match.SrcCIDRs = []string{"192.168.1.0/24", "192.168.2.0/24"}
	cfg.TrafficClasses[0].Match.DstCIDRs = []string{"10.0.0.0/8", "172.16.0.0/12", "203.0.113.0/24"}
	got := expectedRuleCount(cfg, nil)
	// 2 src * 3 dst * 2 = 12
	if got != 12 {
		t.Fatalf("expected 12, got %d", got)
	}
}

func TestExpectedRuleCount_DisabledClassNotCounted(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Enabled = false
	got := expectedRuleCount(cfg, nil)
	if got != 0 {
		t.Fatalf("expected 0 for disabled class, got %d", got)
	}
}

func TestExpectedRuleCount_RangesIncluded(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		SrcRanges: []string{"192.168.1.1-192.168.1.50"},
		DstCIDRs:  []string{"10.0.0.0/8"},
		DstRanges: []string{"172.16.0.1-172.16.0.254"},
	}
	got := expectedRuleCount(cfg, nil)
	// 1 src_range * (1 dst_cidr + 1 dst_range) * 2 = 4
	if got != 4 {
		t.Fatalf("expected 4, got %d", got)
	}
}

func TestExpectedRuleCount_MixedSrcTypes(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Match = MatchCriteria{
		SrcCIDRs:  []string{"192.168.1.0/24"},
		SrcRanges: []string{"10.0.0.1-10.0.0.50"},
		SrcMACs:   []string{"aa:bb:cc:dd:ee:ff"},
		DstCIDRs:  []string{"203.0.113.0/24"},
	}
	got := expectedRuleCount(cfg, nil)
	// 3 src * 1 dst * 2 = 6
	if got != 6 {
		t.Fatalf("expected 6, got %d", got)
	}
}

func TestExpectedRuleCount_MultipleClasses(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses = append(cfg.TrafficClasses, TrafficClass{
		Name:        "video",
		Match:       MatchCriteria{DstCIDRs: []string{"198.51.100.0/24", "203.0.113.0/24"}},
		Probability: 0.5,
		TargetWAN:   "backup",
		Enabled:     true,
	})
	got := expectedRuleCount(cfg, nil)
	// class 0: 1*1*2=2, class 1: 1*2*2=4, total=6
	if got != 6 {
		t.Fatalf("expected 6, got %d", got)
	}
}

func TestExpectedRuleCount_UnhealthyWANExcluded(t *testing.T) {
	cfg := validConfig() // gaming -> backup
	cfg.TrafficClasses = append(cfg.TrafficClasses, TrafficClass{
		Name:        "video",
		Match:       MatchCriteria{DstCIDRs: []string{"198.51.100.0/24"}},
		Probability: 1.0,
		TargetWAN:   "primary",
		Enabled:     true,
	})
	// Full config: gaming=2 + video=2 = 4
	got := expectedRuleCount(cfg, nil)
	if got != 4 {
		t.Fatalf("expected 4 with no disabled WANs, got %d", got)
	}

	// backup unhealthy: gaming disabled, video still counts = 2
	got = expectedRuleCount(cfg, map[string]bool{"backup": true})
	if got != 2 {
		t.Fatalf("expected 2 with backup disabled, got %d", got)
	}

	// both unhealthy: 0
	got = expectedRuleCount(cfg, map[string]bool{"backup": true, "primary": true})
	if got != 0 {
		t.Fatalf("expected 0 with all WANs disabled, got %d", got)
	}
}

// ---------------------------------------------------------------------------
// Active target WANs
// ---------------------------------------------------------------------------

func TestActiveTargetWANs_EnabledOnly(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses = append(cfg.TrafficClasses, TrafficClass{
		Name:        "video",
		Match:       MatchCriteria{DstCIDRs: []string{"10.0.0.0/8"}},
		Probability: 1.0,
		TargetWAN:   "primary",
		Enabled:     true,
	})
	got := activeTargetWANs(cfg)
	if !got["backup"] {
		t.Error("expected backup in active targets")
	}
	if !got["primary"] {
		t.Error("expected primary in active targets")
	}
}

func TestActiveTargetWANs_DisabledExcluded(t *testing.T) {
	cfg := validConfig()
	cfg.TrafficClasses[0].Enabled = false
	got := activeTargetWANs(cfg)
	if got["backup"] {
		t.Error("disabled class target should not appear in active targets")
	}
	if len(got) != 0 {
		t.Errorf("expected empty map, got %v", got)
	}
}

// ---------------------------------------------------------------------------
// connmarkSharedArgs
// ---------------------------------------------------------------------------

func TestConnmarkSharedArgs_NoProtocol(t *testing.T) {
	tc := &TrafficClass{Match: MatchCriteria{DstCIDRs: []string{"10.0.0.0/8"}}}
	got := connmarkSharedArgs(tc)
	if len(got) != 0 {
		t.Fatalf("expected empty args, got %v", got)
	}
}

func TestConnmarkSharedArgs_WithProtocolAndPorts(t *testing.T) {
	tc := &TrafficClass{
		Match: MatchCriteria{
			Protocol: "udp",
			DstPorts: []string{"27015-27030"},
			DstCIDRs: []string{"10.0.0.0/8"},
		},
	}
	got := connmarkSharedArgs(tc)
	// Should have: -p udp --dport 27015:27030
	expected := []string{"-p", "udp", "--dport", "27015:27030"}
	if len(got) != len(expected) {
		t.Fatalf("expected %v, got %v", expected, got)
	}
	for i := range expected {
		if got[i] != expected[i] {
			t.Errorf("arg[%d]: expected %q, got %q", i, expected[i], got[i])
		}
	}
}

func TestConnmarkSharedArgs_MultiplePortsUseMultiport(t *testing.T) {
	tc := &TrafficClass{
		Match: MatchCriteria{
			Protocol: "tcp",
			SrcPorts: []string{"1234", "5678"},
			DstCIDRs: []string{"10.0.0.0/8"},
		},
	}
	got := connmarkSharedArgs(tc)
	// Should have: -p tcp -m multiport --sports 1234,5678
	expected := []string{"-p", "tcp", "-m", "multiport", "--sports", "1234,5678"}
	if len(got) != len(expected) {
		t.Fatalf("expected %v, got %v", expected, got)
	}
	for i := range expected {
		if got[i] != expected[i] {
			t.Errorf("arg[%d]: expected %q, got %q", i, expected[i], got[i])
		}
	}
}

// ---------------------------------------------------------------------------
// Config defaults (loadConfig with temp file)
// ---------------------------------------------------------------------------

func TestLoadConfig_Defaults(t *testing.T) {
	cfgJSON := `{
		"wan_interfaces": {
			"primary": {"interface": "eth0", "gateway": "192.0.2.1", "route_table": "100", "fwmark": "0x20000"}
		},
		"default_wan": "primary",
		"traffic_classes": [
			{
				"name": "test",
				"match": {"dst_cidrs": ["10.0.0.0/8"]},
				"probability": 1.0,
				"target_wan": "primary",
				"enabled": true
			}
		]
	}`

	dir := t.TempDir()
	path := filepath.Join(dir, "config.json")
	if err := os.WriteFile(path, []byte(cfgJSON), 0644); err != nil {
		t.Fatal(err)
	}

	cfg, err := loadConfig(path)
	if err != nil {
		t.Fatalf("loadConfig failed: %v", err)
	}

	if cfg.ReconcileInterval != 30 {
		t.Errorf("expected default ReconcileInterval 30, got %d", cfg.ReconcileInterval)
	}
	if cfg.HealthCheckInterval != 10 {
		t.Errorf("expected default HealthCheckInterval 10, got %d", cfg.HealthCheckInterval)
	}
	if cfg.HealthCheckTimeout != 3 {
		t.Errorf("expected default HealthCheckTimeout 3, got %d", cfg.HealthCheckTimeout)
	}
	if cfg.HealthFailThreshold != 3 {
		t.Errorf("expected default HealthFailThreshold 3, got %d", cfg.HealthFailThreshold)
	}
	if cfg.HealthPassThreshold != 2 {
		t.Errorf("expected default HealthPassThreshold 2, got %d", cfg.HealthPassThreshold)
	}
	if cfg.StatusFile != "/tmp/wan-steer-status.json" {
		t.Errorf("expected default StatusFile, got %q", cfg.StatusFile)
	}
}

func TestLoadConfig_CustomValuesNotOverridden(t *testing.T) {
	cfgJSON := `{
		"wan_interfaces": {
			"primary": {"interface": "eth0", "gateway": "192.0.2.1", "route_table": "100", "fwmark": "0x20000"}
		},
		"default_wan": "primary",
		"reconcile_interval_seconds": 60,
		"health_check_interval_seconds": 20,
		"status_file": "/var/run/wan-steer.json",
		"traffic_classes": [
			{
				"name": "test",
				"match": {"dst_cidrs": ["10.0.0.0/8"]},
				"probability": 1.0,
				"target_wan": "primary",
				"enabled": true
			}
		]
	}`

	dir := t.TempDir()
	path := filepath.Join(dir, "config.json")
	if err := os.WriteFile(path, []byte(cfgJSON), 0644); err != nil {
		t.Fatal(err)
	}

	cfg, err := loadConfig(path)
	if err != nil {
		t.Fatalf("loadConfig failed: %v", err)
	}

	if cfg.ReconcileInterval != 60 {
		t.Errorf("expected ReconcileInterval 60, got %d", cfg.ReconcileInterval)
	}
	if cfg.HealthCheckInterval != 20 {
		t.Errorf("expected HealthCheckInterval 20, got %d", cfg.HealthCheckInterval)
	}
	if cfg.StatusFile != "/var/run/wan-steer.json" {
		t.Errorf("expected custom StatusFile, got %q", cfg.StatusFile)
	}
}

// ---------------------------------------------------------------------------
// Health checker update logic
// ---------------------------------------------------------------------------

func TestHealthChecker_StartsHealthy(t *testing.T) {
	cfg := validConfig()
	h := newHealthChecker(cfg, nil)

	if !h.isHealthy("primary") {
		t.Error("primary should start healthy")
	}
	if !h.isHealthy("backup") {
		t.Error("backup should start healthy")
	}
}

func TestHealthChecker_FailThreshold(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 3
	cfg.HealthPassThreshold = 2

	var changed []bool
	h := newHealthChecker(cfg, func(wan string, healthy bool, inBackoff bool) {
		changed = append(changed, healthy)
	})

	// Fail twice - should still be healthy (threshold is 3)
	h.update("backup", false)
	h.update("backup", false)
	if !h.isHealthy("backup") {
		t.Error("should still be healthy after 2 failures (threshold 3)")
	}
	if len(changed) != 0 {
		t.Error("callback should not have fired yet")
	}

	// Third failure - should go unhealthy
	h.update("backup", false)
	if h.isHealthy("backup") {
		t.Error("should be unhealthy after 3 failures")
	}
	if len(changed) != 1 || changed[0] != false {
		t.Errorf("expected callback with healthy=false, got %v", changed)
	}
}

func TestHealthChecker_PassThreshold(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 2

	h := newHealthChecker(cfg, nil)

	// Mark unhealthy
	h.update("backup", false)
	if h.isHealthy("backup") {
		t.Fatal("should be unhealthy")
	}

	// One pass not enough
	h.update("backup", true)
	if h.isHealthy("backup") {
		t.Error("should still be unhealthy after 1 pass (threshold 2)")
	}

	// Second pass recovers
	h.update("backup", true)
	if !h.isHealthy("backup") {
		t.Error("should be healthy after 2 passes")
	}
}

func TestHealthChecker_FailResetsPassCount(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 3

	h := newHealthChecker(cfg, nil)

	// Go unhealthy
	h.update("backup", false)

	// 2 passes, then a failure resets
	h.update("backup", true)
	h.update("backup", true)
	h.update("backup", false)

	// Need 3 fresh passes now
	h.update("backup", true)
	h.update("backup", true)
	if h.isHealthy("backup") {
		t.Error("should still be unhealthy - fail reset the pass count")
	}
	h.update("backup", true)
	if !h.isHealthy("backup") {
		t.Error("should be healthy after 3 consecutive passes")
	}
}

func TestHealthChecker_UnhealthyWANs(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1

	h := newHealthChecker(cfg, nil)
	h.update("backup", false)

	unhealthy := h.unhealthyWANs()
	if !unhealthy["backup"] {
		t.Error("backup should be in unhealthy set")
	}
	if unhealthy["primary"] {
		t.Error("primary should not be in unhealthy set")
	}
}

func TestHealthChecker_Snapshot(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1

	h := newHealthChecker(cfg, nil)
	h.update("backup", false)

	snap := h.snapshot()
	if snap["backup"].Healthy {
		t.Error("backup snapshot should show unhealthy")
	}
	if !snap["primary"].Healthy {
		t.Error("primary snapshot should show healthy")
	}
	if snap["backup"].FailCount != 1 {
		t.Errorf("expected fail count 1, got %d", snap["backup"].FailCount)
	}
}

// ---------------------------------------------------------------------------
// buildStatus
// ---------------------------------------------------------------------------

func TestBuildStatus_PopulatesFields(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1

	h := newHealthChecker(cfg, nil)
	started := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	lastReconcile := time.Date(2026, 1, 1, 0, 1, 0, 0, time.UTC)

	status := buildStatus(cfg, started, lastReconcile, 5, h, false)

	if !status.Running {
		t.Error("expected Running=true")
	}
	if status.StartedAt != started {
		t.Error("StartedAt mismatch")
	}
	if status.LastReconcile != lastReconcile {
		t.Error("LastReconcile mismatch")
	}
	if status.ReconcileCount != 5 {
		t.Errorf("expected ReconcileCount 5, got %d", status.ReconcileCount)
	}
	if len(status.TrafficClasses) != 1 {
		t.Fatalf("expected 1 traffic class, got %d", len(status.TrafficClasses))
	}
	tc := status.TrafficClasses[0]
	if tc.Name != "gaming" || tc.TargetWAN != "backup" || tc.Probability != 1.0 || !tc.Enabled {
		t.Errorf("traffic class mismatch: %+v", tc)
	}
	if len(status.WANHealth) != 2 {
		t.Errorf("expected 2 WAN health entries, got %d", len(status.WANHealth))
	}
}

// ---------------------------------------------------------------------------
// SFE flush
// ---------------------------------------------------------------------------

func TestFlushSFE_NoSysfs(t *testing.T) {
	// flushSFE should not panic or error when /sys/sfe_ipv4/flush doesn't exist.
	// On non-gateway hosts (dev machines, CI), the sysfs paths won't exist and
	// the function should silently skip them.
	flushSFE() // must not panic
}

func TestFlushSFE_Idempotent(t *testing.T) {
	// Calling flushSFE multiple times should be safe (e.g. when
	// flushAllSteeredConntrack calls flushConntrackForWAN per-WAN).
	flushSFE()
	flushSFE()
	// No panic, no error - global SFE flush is idempotent
}

// ---------------------------------------------------------------------------
// Config defaults for new stability fields
// ---------------------------------------------------------------------------

func TestLoadConfig_StabilityDefaults(t *testing.T) {
	cfgJSON := `{
		"wan_interfaces": {
			"primary": {"interface": "eth0", "gateway": "192.0.2.1", "route_table": "100", "fwmark": "0x20000"}
		},
		"default_wan": "primary",
		"traffic_classes": [
			{
				"name": "test",
				"match": {"dst_cidrs": ["10.0.0.0/8"]},
				"probability": 1.0,
				"target_wan": "primary",
				"enabled": true
			}
		]
	}`

	dir := t.TempDir()
	path := filepath.Join(dir, "config.json")
	if err := os.WriteFile(path, []byte(cfgJSON), 0644); err != nil {
		t.Fatal(err)
	}

	cfg, err := loadConfig(path)
	if err != nil {
		t.Fatalf("loadConfig failed: %v", err)
	}

	if cfg.StartupGraceSeconds != 30 {
		t.Errorf("expected default StartupGraceSeconds 30, got %d", cfg.StartupGraceSeconds)
	}
	if cfg.InstabilityThreshold != 3 {
		t.Errorf("expected default InstabilityThreshold 3, got %d", cfg.InstabilityThreshold)
	}
	if cfg.InstabilityWindowSeconds != 300 {
		t.Errorf("expected default InstabilityWindowSeconds 300, got %d", cfg.InstabilityWindowSeconds)
	}
	if cfg.BackoffRecoverySeconds != 60 {
		t.Errorf("expected default BackoffRecoverySeconds 60, got %d", cfg.BackoffRecoverySeconds)
	}
	if cfg.SFEFlushCooldownSeconds != 10 {
		t.Errorf("expected default SFEFlushCooldownSeconds 10, got %d", cfg.SFEFlushCooldownSeconds)
	}
}

func TestLoadConfig_StabilityCustomValues(t *testing.T) {
	cfgJSON := `{
		"wan_interfaces": {
			"primary": {"interface": "eth0", "gateway": "192.0.2.1", "route_table": "100", "fwmark": "0x20000"}
		},
		"default_wan": "primary",
		"startup_grace_seconds": 15,
		"instability_threshold": 5,
		"instability_window_seconds": 600,
		"backoff_recovery_seconds": 120,
		"sfe_flush_cooldown_seconds": 20,
		"traffic_classes": [
			{
				"name": "test",
				"match": {"dst_cidrs": ["10.0.0.0/8"]},
				"probability": 1.0,
				"target_wan": "primary",
				"enabled": true
			}
		]
	}`

	dir := t.TempDir()
	path := filepath.Join(dir, "config.json")
	if err := os.WriteFile(path, []byte(cfgJSON), 0644); err != nil {
		t.Fatal(err)
	}

	cfg, err := loadConfig(path)
	if err != nil {
		t.Fatalf("loadConfig failed: %v", err)
	}

	if cfg.StartupGraceSeconds != 15 {
		t.Errorf("expected StartupGraceSeconds 15, got %d", cfg.StartupGraceSeconds)
	}
	if cfg.InstabilityThreshold != 5 {
		t.Errorf("expected InstabilityThreshold 5, got %d", cfg.InstabilityThreshold)
	}
	if cfg.InstabilityWindowSeconds != 600 {
		t.Errorf("expected InstabilityWindowSeconds 600, got %d", cfg.InstabilityWindowSeconds)
	}
	if cfg.BackoffRecoverySeconds != 120 {
		t.Errorf("expected BackoffRecoverySeconds 120, got %d", cfg.BackoffRecoverySeconds)
	}
	if cfg.SFEFlushCooldownSeconds != 20 {
		t.Errorf("expected SFEFlushCooldownSeconds 20, got %d", cfg.SFEFlushCooldownSeconds)
	}
}

// ---------------------------------------------------------------------------
// Instability detection
// ---------------------------------------------------------------------------

func TestHealthChecker_InstabilityDetection(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 3
	cfg.InstabilityWindowSeconds = 300

	h := newHealthChecker(cfg, nil)

	// Transition 1: healthy -> unhealthy
	h.update("backup", false)
	if len(h.unstableWANs()) != 0 {
		t.Error("should not be unstable after 1 transition")
	}

	// Transition 2: unhealthy -> healthy
	h.update("backup", true)
	if len(h.unstableWANs()) != 0 {
		t.Error("should not be unstable after 2 transitions")
	}

	// Transition 3: healthy -> unhealthy (threshold reached)
	h.update("backup", false)
	unstable := h.unstableWANs()
	if !unstable["backup"] {
		t.Error("backup should be unstable after 3 transitions")
	}
	if h.anyUnstable() != true {
		t.Error("anyUnstable should return true")
	}
}

func TestHealthChecker_InstabilityWindowExpiry(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 3
	cfg.InstabilityWindowSeconds = 1 // 1 second window for test speed

	h := newHealthChecker(cfg, nil)

	// Create 3 transitions to trigger instability
	h.update("backup", false)
	h.update("backup", true)
	h.update("backup", false)

	if !h.anyUnstable() {
		t.Fatal("should be unstable after 3 rapid transitions")
	}

	// Wait for window to expire
	time.Sleep(1100 * time.Millisecond)

	if h.anyUnstable() {
		t.Error("should no longer be unstable after window expires")
	}
}

func TestHealthChecker_StabilityRecovery(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 3
	cfg.InstabilityWindowSeconds = 1
	cfg.BackoffRecoverySeconds = 1

	h := newHealthChecker(cfg, nil)

	// Trigger instability
	h.update("backup", false)
	h.update("backup", true)
	h.update("backup", false)

	// checkStability should return false while unstable
	if h.checkStability() {
		t.Error("checkStability should be false while unstable")
	}

	// Wait for instability window to expire
	time.Sleep(1100 * time.Millisecond)

	// First check starts the recovery timer
	if h.checkStability() {
		t.Error("checkStability should be false on first check (recovery timer just started)")
	}

	// Wait for recovery period
	time.Sleep(1100 * time.Millisecond)

	if !h.checkStability() {
		t.Error("checkStability should be true after recovery period")
	}
}

func TestHealthChecker_ResetStableSince(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 3
	cfg.InstabilityWindowSeconds = 1
	cfg.BackoffRecoverySeconds = 1

	h := newHealthChecker(cfg, nil)

	// Trigger and let expire
	h.update("backup", false)
	h.update("backup", true)
	h.update("backup", false)
	time.Sleep(1100 * time.Millisecond)
	h.checkStability() // start recovery timer
	time.Sleep(1100 * time.Millisecond)

	// Reset clears history
	h.resetStableSince()

	// After reset, transitions should be cleared
	if h.anyUnstable() {
		t.Error("should not be unstable after reset")
	}
}

func TestHealthChecker_OnlyTargetWANUnstable(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 3
	cfg.InstabilityWindowSeconds = 300

	h := newHealthChecker(cfg, nil)

	// Flap backup only
	h.update("backup", false)
	h.update("backup", true)
	h.update("backup", false)

	unstable := h.unstableWANs()
	if !unstable["backup"] {
		t.Error("backup should be unstable")
	}
	if unstable["primary"] {
		t.Error("primary should not be unstable")
	}
}

// ---------------------------------------------------------------------------
// SFE flush cooldown
// ---------------------------------------------------------------------------

func TestSFEFlushCooldown(t *testing.T) {
	// Initialize with a 1-second cooldown
	initSFECooldown(1)

	// First flush should work (resets the timer)
	flushSFE()

	// Record time of first flush
	sfeFlushState.mu.Lock()
	firstFlush := sfeFlushState.lastTime
	sfeFlushState.mu.Unlock()

	// Second flush within cooldown should be skipped (lastTime unchanged)
	flushSFE()

	sfeFlushState.mu.Lock()
	afterSecond := sfeFlushState.lastTime
	sfeFlushState.mu.Unlock()

	if !afterSecond.Equal(firstFlush) {
		t.Error("second flush should have been skipped (cooldown)")
	}

	// Wait for cooldown to expire
	time.Sleep(1100 * time.Millisecond)

	// Third flush should work
	flushSFE()

	sfeFlushState.mu.Lock()
	afterThird := sfeFlushState.lastTime
	sfeFlushState.mu.Unlock()

	if afterThird.Equal(firstFlush) {
		t.Error("third flush should have proceeded after cooldown expired")
	}

	// Reset cooldown for other tests
	initSFECooldown(0)
}

func TestSFEFlushForce_BypassesCooldown(t *testing.T) {
	initSFECooldown(60) // 60-second cooldown

	flushSFE() // sets lastTime

	sfeFlushState.mu.Lock()
	firstFlush := sfeFlushState.lastTime
	sfeFlushState.mu.Unlock()

	// Force flush should bypass cooldown
	time.Sleep(10 * time.Millisecond) // ensure time difference
	flushSFEForce()

	sfeFlushState.mu.Lock()
	afterForce := sfeFlushState.lastTime
	sfeFlushState.mu.Unlock()

	if !afterForce.After(firstFlush) {
		t.Error("force flush should have updated lastTime despite cooldown")
	}

	// Reset cooldown for other tests
	initSFECooldown(0)
}

// ---------------------------------------------------------------------------
// buildStatus with backoff fields
// ---------------------------------------------------------------------------

func TestBuildStatus_BackoffFields(t *testing.T) {
	cfg := validConfig()
	cfg.HealthFailThreshold = 1
	cfg.HealthPassThreshold = 1
	cfg.InstabilityThreshold = 2
	cfg.InstabilityWindowSeconds = 300

	h := newHealthChecker(cfg, nil)
	started := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)

	// No backoff
	status := buildStatus(cfg, started, time.Time{}, 0, h, false)
	if status.InBackoff {
		t.Error("InBackoff should be false")
	}
	if len(status.UnstableWANs) != 0 {
		t.Error("UnstableWANs should be empty")
	}

	// Trigger instability
	h.update("backup", false)
	h.update("backup", true)

	status = buildStatus(cfg, started, time.Time{}, 0, h, true)
	if !status.InBackoff {
		t.Error("InBackoff should be true")
	}
	if len(status.UnstableWANs) != 1 || status.UnstableWANs[0] != "backup" {
		t.Errorf("expected unstable_wans=[backup], got %v", status.UnstableWANs)
	}
}

func TestMarkMask_DefaultsToLegacyLayout(t *testing.T) {
	// A config written before fwmask existed must keep marking the pre-6.0 way.
	w := WANInterface{FWMark: "0x200000"}
	if w.markMask() != "0x7e0000" {
		t.Errorf("expected legacy mask 0x7e0000, got %s", w.markMask())
	}
	if w.markWithMask() != "0x200000/0x7e0000" {
		t.Errorf("unexpected mark/mask: %s", w.markWithMask())
	}
}

func TestMarkMask_UsesConfiguredMask(t *testing.T) {
	// UniFi OS 6.0.5 layout as read off `ip rule`.
	w := WANInterface{FWMark: "0x216e0000", FWMask: "0x3ffe0000"}
	if w.markWithMask() != "0x216e0000/0x3ffe0000" {
		t.Errorf("unexpected mark/mask: %s", w.markWithMask())
	}
}


// ---------------------------------------------------------------------------
// Probability backends (statistic / dyn_random / none)
// ---------------------------------------------------------------------------

// captureRun replaces run with a recorder. fail decides which iptables calls fail.
func captureRun(t *testing.T, fail func(args []string) bool) *[][]string {
	t.Helper()
	calls := &[][]string{}
	orig := run
	run = func(name string, args ...string) error {
		*calls = append(*calls, append([]string{name}, args...))
		if fail != nil && fail(args) {
			return errors.New("simulated failure")
		}
		return nil
	}
	t.Cleanup(func() { run = orig })
	return calls
}

func withBackend(t *testing.T, b probBackend) {
	t.Helper()
	orig := activeBackend
	activeBackend = b
	t.Cleanup(func() { activeBackend = orig })
}

// captureProcWrites replaces the /proc writer and returns path -> value.
func captureProcWrites(t *testing.T) map[string]string {
	t.Helper()
	writes := map[string]string{}
	orig := writeProcFile
	writeProcFile = func(path, value string) error {
		writes[path] = value
		return nil
	}
	t.Cleanup(func() { writeProcFile = orig })
	return writes
}

// markRules returns the MARK rule commands, joined for substring checks.
func markRules(calls [][]string) []string {
	var out []string
	for _, c := range calls {
		line := strings.Join(c, " ")
		if strings.Contains(line, "-j MARK") {
			out = append(out, line)
		}
	}
	return out
}

func containsAny(line string, subs ...string) bool {
	for _, s := range subs {
		if strings.Contains(line, s) {
			return true
		}
	}
	return false
}

func TestFullClass_EmitsNoProbabilityMatch(t *testing.T) {
	for _, b := range []probBackend{backendStatistic, backendDynRandom, backendNone} {
		t.Run(string(b), func(t *testing.T) {
			withBackend(t, b)
			calls := captureRun(t, nil)
			writes := captureProcWrites(t)
			cfg := validConfig()
			wan := cfg.WANInterfaces["backup"]

			if err := addTrafficClassRules(&cfg.TrafficClasses[0], &wan, 1.0, dynRandomName(0, &wan)); err != nil {
				t.Fatal(err)
			}
			rules := markRules(*calls)
			if len(rules) != 1 {
				t.Fatalf("expected 1 MARK rule, got %d", len(rules))
			}
			if containsAny(rules[0], "statistic", "dyn_random", "--mark 0x0/") {
				t.Errorf("100%% class must emit no probability match or guard: %s", rules[0])
			}
			if len(writes) != 0 {
				t.Errorf("100%% class must not write dyn_random entries: %v", writes)
			}
		})
	}
}

func TestWeightedClass_StatisticBackend(t *testing.T) {
	withBackend(t, backendStatistic)
	calls := captureRun(t, nil)
	writes := captureProcWrites(t)
	cfg := validConfig()
	cfg.TrafficClasses[0].Probability = 0.33
	wan := cfg.WANInterfaces["backup"]

	if err := addTrafficClassRules(&cfg.TrafficClasses[0], &wan, 0.33, dynRandomName(0, &wan)); err != nil {
		t.Fatal(err)
	}
	rules := markRules(*calls)
	want := "-m state --state NEW -m mark --mark 0x0/0x7e0000 -m statistic --mode nth --every 3 --packet 0 -j MARK"
	if len(rules) != 1 || !strings.Contains(rules[0], want) {
		t.Errorf("expected %q in MARK rule, got %v", want, rules)
	}
	if len(writes) != 0 {
		t.Errorf("statistic backend must not write dyn_random entries: %v", writes)
	}
}

func TestWeightedClass_DynRandomBackend(t *testing.T) {
	withBackend(t, backendDynRandom)
	calls := captureRun(t, nil)
	writes := captureProcWrites(t)
	cfg := validConfig()
	cfg.TrafficClasses[0].Probability = 0.5
	cfg.TrafficClasses[0].Match.DstCIDRs = []string{"192.0.2.0/24", "198.51.100.0/24"}
	wan := cfg.WANInterfaces["backup"]

	if err := addTrafficClassRules(&cfg.TrafficClasses[0], &wan, 0.5, dynRandomName(0, &wan)); err != nil {
		t.Fatal(err)
	}
	rules := markRules(*calls)
	want := "-m state --state NEW -m mark --mark 0x0/0x7e0000 -m dyn_random --prob-name netopt-ws-40000-0 -j MARK"
	if len(rules) != 2 {
		t.Fatalf("expected 2 MARK rules, got %d", len(rules))
	}
	for _, r := range rules {
		if !strings.Contains(r, want) || strings.Contains(r, "statistic") {
			t.Errorf("expected %q in MARK rule, got %s", want, r)
		}
	}
	// (3 << 8) | floor(0.5*255) = 0x37f, the value native UniFi writes for 50%
	if got := writes["/proc/net/xt_dyn_random/netopt-ws-40000-0"]; got != "895" {
		t.Errorf("expected dyn_random value 895, got %q (all writes: %v)", got, writes)
	}
}

func TestDynRandomValue(t *testing.T) {
	tests := []struct {
		p    float64
		want int
	}{
		{0.5, 3<<8 | 127},
		{1.0, 3<<8 | 255},
		{0.6, 3<<8 | 153},
		{0.01, 3<<8 | 2},
	}
	for _, tt := range tests {
		if got := dynRandomValue(tt.p); got != tt.want {
			t.Errorf("dynRandomValue(%v) = %d, want %d", tt.p, got, tt.want)
		}
	}
}

func cascadeConfig(probs ...float64) *Config {
	cfg := validConfig()
	cfg.WANInterfaces["third"] = WANInterface{Interface: "eth2", FWMark: "0x60000"}
	cfg.WANInterfaces["fourth"] = WANInterface{Interface: "eth3", FWMark: "0x80000"}
	targets := []string{"backup", "third", "fourth"}
	cfg.TrafficClasses = nil
	for i, p := range probs {
		cfg.TrafficClasses = append(cfg.TrafficClasses, TrafficClass{
			Name:        fmt.Sprintf("way%d", i),
			Match:       MatchCriteria{Protocol: "udp"},
			Probability: p,
			TargetWAN:   targets[i],
			Enabled:     true,
		})
	}
	return cfg
}

func TestConditionalProbabilities_Cascade(t *testing.T) {
	got := conditionalProbabilities(cascadeConfig(0.5, 0.3, 0.2).TrafficClasses)
	want := []float64{0.5, 0.6, 1.0}
	for i := range want {
		if math.Abs(got[i]-want[i]) > 1e-9 {
			t.Errorf("class %d: conditional %v, want %v", i, got[i], want[i])
		}
	}
}

func TestConditionalProbabilities_DifferentMatchKeepsAbsolute(t *testing.T) {
	cfg := cascadeConfig(0.5, 0.3)
	cfg.TrafficClasses[1].Match = MatchCriteria{Protocol: "tcp"}
	got := conditionalProbabilities(cfg.TrafficClasses)
	if got[0] != 0.5 || got[1] != 0.3 {
		t.Errorf("expected absolute 0.5/0.3 for different matches, got %v", got)
	}
}

func TestConditionalProbabilities_SkipsDisabledAndFull(t *testing.T) {
	// A disabled class (an unhealthy WAN, via reapplyRules) leaves the cascade, so the
	// survivors keep their absolute share: 30% then 20% of the whole.
	cfg := cascadeConfig(0.5, 0.3, 0.2)
	cfg.TrafficClasses[0].Enabled = false
	got := conditionalProbabilities(cfg.TrafficClasses)
	if math.Abs(got[1]-0.3) > 1e-9 || math.Abs(got[2]-0.2/0.7) > 1e-9 {
		t.Errorf("expected 0.3, %v; got %v", 0.2/0.7, got)
	}

	full := cascadeConfig(1.0, 0.5)
	got = conditionalProbabilities(full.TrafficClasses)
	if got[0] != 1.0 || got[1] != 0.5 {
		t.Errorf("a 100%% class must not shrink the next class's share, got %v", got)
	}
}

func TestApplyRules_CascadeStatistic(t *testing.T) {
	withBackend(t, backendStatistic)
	calls := captureRun(t, nil)
	captureProcWrites(t)

	if err := applyRules(cascadeConfig(0.5, 0.25, 0.25)); err != nil {
		t.Fatal(err)
	}
	rules := markRules(*calls)
	if len(rules) != 3 {
		t.Fatalf("expected 3 MARK rules, got %d", len(rules))
	}
	// 50% of all, 50% of the remaining half, then the rest: an exact 50/25/25 split
	if !strings.Contains(rules[0], "--every 2 ") || !strings.Contains(rules[1], "--every 2 ") {
		t.Errorf("expected --every 2 on the first two ways: %v", rules)
	}
	if strings.Contains(rules[2], "statistic") || !strings.Contains(rules[2], "--mark 0x0/0x7e0000") {
		t.Errorf("last way must take every unmarked connection, guarded: %s", rules[2])
	}
	for _, r := range rules {
		if !strings.Contains(r, "--mark 0x0/0x7e0000 ") {
			t.Errorf("weighted MARK rule missing unmarked-only guard: %s", r)
		}
	}
}

func TestApplyRules_CascadeDynRandom(t *testing.T) {
	withBackend(t, backendDynRandom)
	calls := captureRun(t, nil)
	writes := captureProcWrites(t)

	if err := applyRules(cascadeConfig(0.5, 0.3, 0.2)); err != nil {
		t.Fatal(err)
	}
	rules := markRules(*calls)
	if len(rules) != 3 {
		t.Fatalf("expected 3 MARK rules, got %d", len(rules))
	}
	want := map[string]string{
		"/proc/net/xt_dyn_random/netopt-ws-40000-0": "895", // 0.5
		"/proc/net/xt_dyn_random/netopt-ws-60000-1": "921", // 0.3 / 0.5 = 0.6
	}
	if len(writes) != len(want) {
		t.Errorf("expected %d dyn_random writes, got %v", len(want), writes)
	}
	for path, v := range want {
		if writes[path] != v {
			t.Errorf("%s = %q, want %q", path, writes[path], v)
		}
	}
	if strings.Contains(rules[2], "dyn_random") || !strings.Contains(rules[2], "--mark 0x0/0x7e0000") {
		t.Errorf("last way must take every unmarked connection, guarded: %s", rules[2])
	}
}

func TestResolveProbBackend(t *testing.T) {
	origDelay := probeRetryDelay
	probeRetryDelay = 0
	t.Cleanup(func() { probeRetryDelay = origDelay })

	failing := func(matches ...string) func([]string) bool {
		return func(args []string) bool {
			line := strings.Join(args, " ")
			if !strings.Contains(line, "-A "+probeChain) {
				return false
			}
			return containsAny(line, matches...)
		}
	}

	tests := []struct {
		name string
		fail func([]string) bool
		want probBackend
	}{
		{"statistic loads", nil, backendStatistic},
		{"statistic missing", failing("statistic"), backendDynRandom},
		{"both missing", failing("statistic", "dyn_random"), backendNone},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			calls := captureRun(t, tt.fail)
			if got := resolveProbBackend(); got != tt.want {
				t.Errorf("got %s, want %s", got, tt.want)
			}
			last := strings.Join((*calls)[len(*calls)-1], " ")
			if !strings.Contains(last, "-X "+probeChain) {
				t.Errorf("probe chain not removed, last call: %s", last)
			}
		})
	}
}

func TestNoBackend_RefusesWeightedKeepsFull(t *testing.T) {
	withBackend(t, backendNone)
	calls := captureRun(t, nil)
	captureProcWrites(t)
	cfg := cascadeConfig(1.0, 0.5)

	if err := applyRules(cfg); err != nil {
		t.Fatal(err)
	}
	rules := markRules(*calls)
	if len(rules) != 1 || !strings.Contains(rules[0], "-j MARK --set-xmark 0x40000/") {
		t.Errorf("expected only the 100%% class's MARK rule, got %v", rules)
	}
	if got := unsupportedClasses(cfg); len(got) != 1 || got[0] != "way1" {
		t.Errorf("expected unsupported [way1], got %v", got)
	}
	if got := expectedRuleCount(cfg, nil); got != 2 {
		t.Errorf("refused class must not count toward drift detection: expected 2 rules, got %d", got)
	}

	h := newHealthChecker(cfg, nil)
	status := buildStatus(cfg, time.Now(), time.Now(), 0, h, false)
	if status.ProbBackend != "none" || len(status.UnsupportedClasses) != 1 {
		t.Errorf("status mismatch: backend %q, unsupported %v", status.ProbBackend, status.UnsupportedClasses)
	}
}

func TestDynRandomName_StableAndUnique(t *testing.T) {
	wan := WANInterface{FWMark: "0x1A0000"}
	a, b := dynRandomName(0, &wan), dynRandomName(1, &wan)
	if a == b {
		t.Errorf("names must differ per class: %s", a)
	}
	if a != dynRandomName(0, &wan) {
		t.Error("name must be stable across calls")
	}
	for _, n := range []string{a, b} {
		if containsAny(n, "wf-group", "randomweight") || !strings.HasPrefix(n, "netopt-ws-") {
			t.Errorf("name %q may collide with UniFi's entries", n)
		}
	}
}