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
	onStateChange func(wan string, healthy bool, inBackoff bool)

	// Instability tracking: timestamps of recent state transitions per WAN.
	transitions map[string][]time.Time
	// When instability was last cleared (all WANs stable). Used to track
	// whether BackoffRecoverySeconds has elapsed.
	stableSince time.Time
	// backoff is set by the main loop so the onStateChange callback can
	// access it without closing over a variable from a different scope.
	backoff bool
}

func newHealthChecker(cfg *Config, onStateChange func(string, bool, bool)) *HealthChecker {
	h := &HealthChecker{
		cfg:           cfg,
		failCounts:    make(map[string]int),
		passCounts:    make(map[string]int),
		healthy:       make(map[string]bool),
		lastCheck:     make(map[string]time.Time),
		transitions:   make(map[string][]time.Time),
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
	now := time.Now()
	h.lastCheck[wan] = now
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

	// Record state transition for instability detection
	if stateChanged {
		h.transitions[wan] = append(h.transitions[wan], now)
		h.pruneTransitions(wan, now)

		if h.isUnstableLocked(wan) {
			slog.Warn("wan marked unstable due to rapid state changes",
				"wan", wan,
				"transitions", len(h.transitions[wan]),
				"window_seconds", h.cfg.InstabilityWindowSeconds,
			)
		}
		// Reset stableSince whenever a transition occurs — recovery timer restarts
		h.stableSince = time.Time{}
	}

	h.mu.Unlock()

	// Callback outside lock to prevent deadlock.
	// Read backoff under lock so the callback gets a consistent value.
	var backoffState bool
	if stateChanged {
		h.mu.RLock()
		backoffState = h.backoff
		h.mu.RUnlock()
	}
	if stateChanged && h.onStateChange != nil {
		h.onStateChange(wan, newHealthy, backoffState)
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

// setBackoff updates the backoff state so the onStateChange callback can access it.
func (h *HealthChecker) setBackoff(b bool) {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.backoff = b
}

// pruneTransitions removes transitions older than the instability window.
// Must be called with h.mu held.
func (h *HealthChecker) pruneTransitions(wan string, now time.Time) {
	window := time.Duration(h.cfg.InstabilityWindowSeconds) * time.Second
	cutoff := now.Add(-window)
	ts := h.transitions[wan]
	i := 0
	for i < len(ts) && ts[i].Before(cutoff) {
		i++
	}
	if i > 0 {
		h.transitions[wan] = ts[i:]
	}
}

// isUnstableLocked returns true if the WAN has exceeded the instability threshold.
// Must be called with h.mu held (at least RLock).
func (h *HealthChecker) isUnstableLocked(wan string) bool {
	return len(h.transitions[wan]) >= h.cfg.InstabilityThreshold
}

// anyUnstable returns true if any WAN is currently in an unstable state.
func (h *HealthChecker) anyUnstable() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	now := time.Now()
	for wan := range h.cfg.WANInterfaces {
		h.pruneTransitions(wan, now)
		if h.isUnstableLocked(wan) {
			return true
		}
	}
	return false
}

// unstableWANs returns the set of WANs currently in unstable state.
func (h *HealthChecker) unstableWANs() map[string]bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	now := time.Now()
	result := make(map[string]bool)
	for wan := range h.cfg.WANInterfaces {
		h.pruneTransitions(wan, now)
		if h.isUnstableLocked(wan) {
			result[wan] = true
		}
	}
	return result
}

// checkStability checks if all WANs have been stable (no unstable WANs) and
// returns true if they have been stable for at least BackoffRecoverySeconds.
// This is called from the main loop to decide when to exit backoff mode.
func (h *HealthChecker) checkStability() bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	now := time.Now()

	anyUnstable := false
	for wan := range h.cfg.WANInterfaces {
		h.pruneTransitions(wan, now)
		if h.isUnstableLocked(wan) {
			anyUnstable = true
			break
		}
	}

	if anyUnstable {
		h.stableSince = time.Time{}
		return false
	}

	// All WANs are stable — start or check recovery timer
	if h.stableSince.IsZero() {
		h.stableSince = now
		return false
	}
	recovery := time.Duration(h.cfg.BackoffRecoverySeconds) * time.Second
	return now.Sub(h.stableSince) >= recovery
}

// resetStableSince resets the stability timer (called after exiting backoff).
func (h *HealthChecker) resetStableSince() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.stableSince = time.Time{}
	// Clear all transition history so we start fresh
	for wan := range h.transitions {
		h.transitions[wan] = nil
	}
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
