package main

import (
	"fmt"
	"log/slog"
	"os/exec"
	"sync"
	"time"
)

// HealthChecker monitors WAN link health and reports up/down state.
type HealthChecker struct {
	cfg           *Config
	mu            sync.RWMutex
	failCounts    map[string]int  // consecutive failures per WAN
	passCounts    map[string]int  // consecutive successes per WAN
	healthy       map[string]bool // current health state per WAN
	lastCheck     map[string]time.Time
	onStateChange func(wan string, healthy bool)
}

func newHealthChecker(cfg *Config, onStateChange func(string, bool)) *HealthChecker {
	h := &HealthChecker{
		cfg:           cfg,
		failCounts:    make(map[string]int),
		passCounts:    make(map[string]int),
		healthy:       make(map[string]bool),
		lastCheck:     make(map[string]time.Time),
		onStateChange: onStateChange,
	}
	// All WANs start healthy
	for name := range cfg.WANInterfaces {
		h.healthy[name] = true
	}
	return h
}

// checkAll pings all WAN health targets and updates state.
func (h *HealthChecker) checkAll() {
	for name, wan := range h.cfg.WANInterfaces {
		if wan.HealthTarget == "" || wan.FWMark == "" {
			continue
		}
		reachable := ping(wan.HealthTarget, wan.Interface, h.cfg.HealthCheckTimeout)
		h.update(name, reachable)
	}
}

// update processes a single health check result with hysteresis.
// Calls onStateChange outside the lock to avoid deadlock (callback may call unhealthyWANs).
func (h *HealthChecker) update(wan string, reachable bool) {
	var stateChanged bool
	var newHealthy bool

	h.mu.Lock()
	h.lastCheck[wan] = time.Now()
	wasHealthy := h.healthy[wan]

	if reachable {
		h.failCounts[wan] = 0
		h.passCounts[wan]++
		if !wasHealthy && h.passCounts[wan] >= h.cfg.HealthPassThreshold {
			h.healthy[wan] = true
			stateChanged = true
			newHealthy = true
			slog.Info("wan recovered", "wan", wan, "consecutive_passes", h.passCounts[wan])
		}
	} else {
		h.passCounts[wan] = 0
		h.failCounts[wan]++
		if wasHealthy && h.failCounts[wan] >= h.cfg.HealthFailThreshold {
			h.healthy[wan] = false
			stateChanged = true
			newHealthy = false
			slog.Warn("wan down", "wan", wan, "consecutive_failures", h.failCounts[wan])
		}
	}
	h.mu.Unlock()

	// Callback outside lock to prevent deadlock
	if stateChanged && h.onStateChange != nil {
		h.onStateChange(wan, newHealthy)
	}
}

// isHealthy returns the current health state of a WAN.
func (h *HealthChecker) isHealthy(wan string) bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	healthy, ok := h.healthy[wan]
	return ok && healthy
}

// unhealthyWANs returns the set of WANs currently marked unhealthy.
func (h *HealthChecker) unhealthyWANs() map[string]bool {
	h.mu.RLock()
	defer h.mu.RUnlock()
	result := make(map[string]bool)
	for name, healthy := range h.healthy {
		if !healthy {
			result[name] = true
		}
	}
	return result
}

// snapshot returns health state for the status file.
func (h *HealthChecker) snapshot() map[string]WANHealth {
	h.mu.RLock()
	defer h.mu.RUnlock()
	result := make(map[string]WANHealth)
	for name := range h.cfg.WANInterfaces {
		result[name] = WANHealth{
			Healthy:    h.healthy[name],
			FailCount:  h.failCounts[name],
			PassCount:  h.passCounts[name],
			LastCheck:  h.lastCheck[name],
		}
	}
	return result
}

// ping sends an ICMP ping via a specific interface.
func ping(target, iface string, timeoutSecs int) bool {
	args := []string{
		"-c", "1",
		"-W", fmt.Sprintf("%d", timeoutSecs),
	}
	if iface != "" {
		args = append(args, "-I", iface)
	}
	args = append(args, target)

	cmd := exec.Command("ping", args...)
	return cmd.Run() == nil
}
