package main

import (
	"encoding/json"
	"os"
	"time"
)

// Status is the JSON structure written to the status file for Network Optimizer to read.
type Status struct {
	Version         string                  `json:"version"`
	Running         bool                    `json:"running"`
	StartedAt       time.Time               `json:"started_at"`
	LastReconcile   time.Time               `json:"last_reconcile"`
	RuleCount       int                     `json:"rule_count"`
	ReconcileCount  int                     `json:"reconcile_count"`
	WANHealth       map[string]WANHealth    `json:"wan_health"`
	TrafficClasses  []TrafficClassStatus    `json:"traffic_classes"`
}

// WANHealth is the health state of a single WAN link.
type WANHealth struct {
	Healthy   bool      `json:"healthy"`
	FailCount int       `json:"fail_count"`
	PassCount int       `json:"pass_count"`
	LastCheck time.Time `json:"last_check"`
}

// TrafficClassStatus is the status of a single traffic class.
type TrafficClassStatus struct {
	Name        string        `json:"name"`
	Enabled     bool          `json:"enabled"`
	TargetWAN   string        `json:"target_wan"`
	Probability float64       `json:"probability"`
	Match       MatchCriteria `json:"match"`
}

func writeStatus(path string, status *Status) error {
	data, err := json.MarshalIndent(status, "", "  ")
	if err != nil {
		return err
	}
	// Write atomically: write to temp file, then rename
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, data, 0644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func buildStatus(cfg *Config, startedAt time.Time, lastReconcile time.Time, reconcileCount int, health *HealthChecker) *Status {
	classes := make([]TrafficClassStatus, 0, len(cfg.TrafficClasses))
	for _, tc := range cfg.TrafficClasses {
		classes = append(classes, TrafficClassStatus{
			Name:        tc.Name,
			Enabled:     tc.Enabled,
			TargetWAN:   tc.TargetWAN,
			Probability: tc.Probability,
			Match:       tc.Match,
		})
	}

	return &Status{
		Version:        version,
		Running:        true,
		StartedAt:      startedAt,
		LastReconcile:  lastReconcile,
		RuleCount:      ruleCount(),
		ReconcileCount: reconcileCount,
		WANHealth:      health.snapshot(),
		TrafficClasses: classes,
	}
}
