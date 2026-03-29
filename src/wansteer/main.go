package main

import (
	"flag"
	"fmt"
	"log/slog"
	"net"
	"os"
	"os/signal"
	"syscall"
	"time"
)

var version = "dev"

func main() {
	configPath := flag.String("config", "/data/wan-steer/config.json", "Path to config file")
	cleanup := flag.Bool("cleanup", false, "Remove all rules and exit (for ExecStopPost)")
	showVersion := flag.Bool("version", false, "Print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(version)
		os.Exit(0)
	}

	slog.SetDefault(slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	})))

	if *cleanup {
		if err := removeRules(); err != nil {
			slog.Error("cleanup failed", "error", err)
			os.Exit(1)
		}
		os.Exit(0)
	}

	cfg, err := loadConfig(*configPath)
	if err != nil {
		slog.Error("failed to load config", "error", err)
		os.Exit(1)
	}

	slog.Info("starting wan-steer",
		"version", version,
		"wan_interfaces", len(cfg.WANInterfaces),
		"traffic_classes", countEnabled(cfg),
		"reconcile_interval", cfg.ReconcileInterval,
		"health_interval", cfg.HealthCheckInterval,
		"startup_grace", cfg.StartupGraceSeconds,
		"instability_threshold", cfg.InstabilityThreshold,
		"instability_window", cfg.InstabilityWindowSeconds,
		"backoff_recovery", cfg.BackoffRecoverySeconds,
		"sfe_cooldown", cfg.SFEFlushCooldownSeconds,
	)

	// Initialize SFE flush cooldown from config
	initSFECooldown(cfg.SFEFlushCooldownSeconds)

	// Startup grace period: wait for WAN interfaces to be link-up and stable
	// before applying rules. This prevents stale "all healthy" assumptions
	// while interfaces are still coming up post-reboot.
	waitForWANStability(cfg)

	// Apply initial rules
	if err := applyRules(cfg); err != nil {
		slog.Error("failed to apply initial rules", "error", err)
		os.Exit(1)
	}

	startedAt := time.Now()
	var lastReconcile time.Time
	reconcileCount := 0
	inBackoff := false

	// Health checker with callback to re-apply rules on state change.
	// When in backoff mode, suppress SFE/conntrack flushes but still reapply rules.
	var health *HealthChecker
	health = newHealthChecker(cfg, func(wan string, healthy bool) {
		unhealthy := health.unhealthyWANs()
		if err := reapplyRules(cfg, unhealthy); err != nil {
			slog.Error("failed to reapply rules after health change", "error", err)
		}
		if !healthy {
			if inBackoff {
				slog.Info("suppressing conntrack flush (backoff active)", "wan", wan)
			} else {
				if w, ok := cfg.WANInterfaces[wan]; ok {
					flushConntrackForMark(w.FWMark)
				}
			}
		}
	})

	// Signal handling: SIGTERM/SIGINT = clean shutdown, SIGHUP = reload config
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGTERM, syscall.SIGINT, syscall.SIGHUP)

	reconcileTicker := time.NewTicker(time.Duration(cfg.ReconcileInterval) * time.Second)
	defer reconcileTicker.Stop()

	healthTicker := time.NewTicker(time.Duration(cfg.HealthCheckInterval) * time.Second)
	defer healthTicker.Stop()

	statusTicker := time.NewTicker(10 * time.Second)
	defer statusTicker.Stop()

	// Stability check runs on the health check interval to detect backoff entry/exit
	stabilityTicker := time.NewTicker(time.Duration(cfg.HealthCheckInterval) * time.Second)
	defer stabilityTicker.Stop()

	slog.Info("wan-steer running", "status_file", cfg.StatusFile)

	for {
		select {
		case sig := <-sigCh:
			switch sig {
			case syscall.SIGHUP:
				slog.Info("SIGHUP received, reloading config")
				newCfg, err := loadConfig(*configPath)
				if err != nil {
					slog.Error("failed to reload config, keeping current", "error", err)
					continue
				}
				// Flush conntrack for WANs that had traffic classes but no longer do
				oldTargets := activeTargetWANs(cfg)
				newTargets := activeTargetWANs(newCfg)
				for wan := range oldTargets {
					if !newTargets[wan] {
						if w, ok := cfg.WANInterfaces[wan]; ok && w.FWMark != "" {
							slog.Info("flushing conntrack for removed WAN target", "wan", wan)
							flushConntrackForMark(w.FWMark)
						}
					}
				}
				cfg = newCfg
				initSFECooldown(cfg.SFEFlushCooldownSeconds)
				inBackoff = false
				health = newHealthChecker(cfg, func(wan string, healthy bool) {
					unhealthy := health.unhealthyWANs()
					if err := reapplyRules(cfg, unhealthy); err != nil {
						slog.Error("failed to reapply rules after health change", "error", err)
					}
					if !healthy {
						if inBackoff {
							slog.Info("suppressing conntrack flush (backoff active)", "wan", wan)
						} else {
							if w, ok := cfg.WANInterfaces[wan]; ok {
								flushConntrackForMark(w.FWMark)
							}
						}
					}
				})
				if err := applyRules(cfg); err != nil {
					slog.Error("failed to apply rules after reload", "error", err)
				}
				slog.Info("config reloaded", "traffic_classes", countEnabled(cfg))

			default:
				slog.Info("shutdown signal received", "signal", sig)
				removeRules()
				// SFE flush happens inside flushAllSteeredConntrack via flushConntrackForMark
				flushAllSteeredConntrack(cfg)
				// Write final status
				status := buildStatus(cfg, startedAt, lastReconcile, reconcileCount, health, inBackoff)
				status.Running = false
				writeStatus(cfg.StatusFile, status)
				os.Exit(0)
			}

		case <-reconcileTicker.C:
			// Check if our chain and rules still exist (drift detection).
			// Account for unhealthy WANs so we don't false-positive on
			// intentionally disabled traffic classes.
			unhealthy := health.unhealthyWANs()
			expected := expectedRuleCount(cfg, unhealthy)
			actual := ruleCount()
			jumpOk := hasJump()

			if !jumpOk || actual != expected {
				slog.Warn("drift detected, re-applying rules",
					"expected_rules", expected,
					"actual_rules", actual,
					"jump_present", jumpOk,
					"backoff", inBackoff,
				)
				if inBackoff {
					slog.Info("suppressing SFE flush during reconciliation (backoff active)")
				} else {
					// Flush SFE before rule changes: when rules are rebuilt,
					// connections may shift WANs and SFE's offloaded paths
					// become stale. Flushing prevents the double-free race.
					flushSFE()
				}
				if err := reapplyRules(cfg, unhealthy); err != nil {
					slog.Error("reconciliation failed", "error", err)
				}
				reconcileCount++
			}
			lastReconcile = time.Now()

		case <-healthTicker.C:
			health.checkAll()

		case <-stabilityTicker.C:
			// Check for backoff entry/exit
			if health.anyUnstable() {
				if !inBackoff {
					inBackoff = true
					slog.Warn("entering backoff mode: WAN instability detected, suppressing SFE flushes",
						"unstable_wans", health.unstableWANs(),
					)
				}
			} else if inBackoff {
				// All WANs stable — check if recovery period has elapsed
				if health.checkStability() {
					inBackoff = false
					slog.Info("exiting backoff mode: all WANs stable, performing recovery flush")
					health.resetStableSince()
					// Single forced SFE flush + full rule reapply for clean state
					flushSFEForce()
					unhealthy := health.unhealthyWANs()
					if err := reapplyRules(cfg, unhealthy); err != nil {
						slog.Error("recovery reapply failed", "error", err)
					}
				}
			}

		case <-statusTicker.C:
			status := buildStatus(cfg, startedAt, lastReconcile, reconcileCount, health, inBackoff)
			if err := writeStatus(cfg.StatusFile, status); err != nil {
				slog.Error("failed to write status", "error", err)
			}
		}
	}
}

// waitForWANStability polls WAN interfaces and blocks until all configured
// WANs have been link-up consistently for the startup grace period.
// This prevents applying rules while interfaces are still coming up post-reboot.
func waitForWANStability(cfg *Config) {
	grace := time.Duration(cfg.StartupGraceSeconds) * time.Second
	if grace <= 0 {
		return
	}

	slog.Info("startup grace period: waiting for WAN interfaces to stabilize",
		"grace_seconds", cfg.StartupGraceSeconds,
	)

	// Collect interfaces that have health targets (i.e., WANs we monitor)
	type wanState struct {
		iface   string
		stableAt time.Time // when this WAN was first seen up continuously
	}
	wans := make(map[string]*wanState)
	for name, wan := range cfg.WANInterfaces {
		if wan.Interface != "" {
			wans[name] = &wanState{iface: wan.Interface}
		}
	}

	if len(wans) == 0 {
		return
	}

	pollInterval := 2 * time.Second
	ticker := time.NewTicker(pollInterval)
	defer ticker.Stop()

	// Also enforce a hard timeout: don't wait forever if a WAN is genuinely down
	deadline := time.After(grace * 3) // 3x grace as hard timeout

	for {
		select {
		case <-ticker.C:
			now := time.Now()
			allStable := true
			for name, ws := range wans {
				up := isInterfaceUp(ws.iface)
				if up {
					if ws.stableAt.IsZero() {
						ws.stableAt = now
						slog.Info("wan interface link-up", "wan", name, "interface", ws.iface)
					}
					if now.Sub(ws.stableAt) < grace {
						allStable = false
					}
				} else {
					if !ws.stableAt.IsZero() {
						slog.Warn("wan interface link-down during grace period", "wan", name, "interface", ws.iface)
					}
					ws.stableAt = time.Time{} // reset
					allStable = false
				}
			}
			if allStable {
				slog.Info("all WAN interfaces stable, proceeding")
				return
			}

		case <-deadline:
			slog.Warn("startup grace deadline exceeded, proceeding with current state")
			return
		}
	}
}

// isInterfaceUp checks if a network interface has link-up status.
func isInterfaceUp(name string) bool {
	iface, err := net.InterfaceByName(name)
	if err != nil {
		return false
	}
	return iface.Flags&net.FlagUp != 0
}
