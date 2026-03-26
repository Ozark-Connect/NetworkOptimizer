package main

import (
	"flag"
	"fmt"
	"log/slog"
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
	)

	// Apply initial rules
	if err := applyRules(cfg); err != nil {
		slog.Error("failed to apply initial rules", "error", err)
		os.Exit(1)
	}

	startedAt := time.Now()
	var lastReconcile time.Time
	reconcileCount := 0

	// Health checker with callback to re-apply rules on state change
	var health *HealthChecker
	health = newHealthChecker(cfg, func(wan string, healthy bool) {
		unhealthy := health.unhealthyWANs()
		if err := reapplyRules(cfg, unhealthy); err != nil {
			slog.Error("failed to reapply rules after health change", "error", err)
		}
		// Flush conntrack entries for the dead WAN so existing connections
		// fall back to default routing instead of blackholing
		if !healthy {
			if w, ok := cfg.WANInterfaces[wan]; ok {
				flushConntrackForMark(w.FWMark)
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
				health = newHealthChecker(cfg, func(wan string, healthy bool) {
					unhealthy := health.unhealthyWANs()
					if err := reapplyRules(cfg, unhealthy); err != nil {
						slog.Error("failed to reapply rules after health change", "error", err)
					}
					if !healthy {
						if w, ok := cfg.WANInterfaces[wan]; ok {
							flushConntrackForMark(w.FWMark)
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
				status := buildStatus(cfg, startedAt, lastReconcile, reconcileCount, health)
				status.Running = false
				writeStatus(cfg.StatusFile, status)
				os.Exit(0)
			}

		case <-reconcileTicker.C:
			// Check if our chain and rules still exist (drift detection)
			expected := expectedRuleCount(cfg)
			actual := ruleCount()
			jumpOk := hasJump()

			if !jumpOk || actual != expected {
				slog.Warn("drift detected, re-applying rules",
					"expected_rules", expected,
					"actual_rules", actual,
					"jump_present", jumpOk,
				)
				// Flush SFE before rule changes: when rules are rebuilt,
				// connections may shift WANs and SFE's offloaded paths
				// become stale. Flushing prevents the double-free race.
				flushSFE()
				unhealthy := health.unhealthyWANs()
				if err := reapplyRules(cfg, unhealthy); err != nil {
					slog.Error("reconciliation failed", "error", err)
				}
				reconcileCount++
			}
			lastReconcile = time.Now()

		case <-healthTicker.C:
			health.checkAll()

		case <-statusTicker.C:
			status := buildStatus(cfg, startedAt, lastReconcile, reconcileCount, health)
			if err := writeStatus(cfg.StatusFile, status); err != nil {
				slog.Error("failed to write status", "error", err)
			}
		}
	}
}
