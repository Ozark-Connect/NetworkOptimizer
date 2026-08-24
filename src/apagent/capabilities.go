package main

import (
	"sync"
	"sync/atomic"
	"time"
)

// AgentInfo identifies the build. BinaryVersion is the contract version the collector compares
// against its own embedded copy to decide whether to prompt for a redeploy.
type AgentInfo struct {
	Version       string    `json:"version"`
	BinaryVersion int       `json:"binary_version"`
	StartedAt     time.Time `json:"started_at"`
	PID           int       `json:"pid"`
}

// ListenerInfo advertises the address and port actually bound, not the configured default.
type ListenerInfo struct {
	Interface string `json:"interface,omitempty"`
	Address   string `json:"address"`
	Port      int    `json:"port"`
	TLS       bool   `json:"tls"`
	Auth      string `json:"auth"`
}

// Capabilities is the GET /capabilities payload.
type Capabilities struct {
	Agent          AgentInfo        `json:"agent"`
	Platform       PlatformInfo     `json:"platform"`
	Listener       ListenerInfo     `json:"listener"`
	Vaps           []string         `json:"vaps"`
	Radios         []string         `json:"radios"`
	Probes         []ProbeResult    `json:"probes"`
	ControlSurface []ControlSurface `json:"control_surface"`
	Interfaces     []InterfaceInfo  `json:"interfaces"`
	ProbedAt       time.Time        `json:"probed_at"`
	CollectedAt    time.Time        `json:"collected_at"`
}

// ProbeHealth is one probe's last outcome, for GET /health.
type ProbeHealth struct {
	Available bool      `json:"available"`
	CheckedAt time.Time `json:"checked_at"`
}

// Health is the GET /health payload.
type Health struct {
	Version       string                 `json:"version"`
	BinaryVersion int                    `json:"binary_version"`
	StartedAt     time.Time              `json:"started_at"`
	UptimeSeconds int64                  `json:"uptime_seconds"`
	Degraded      bool                   `json:"degraded"`
	Unavailable   []string               `json:"unavailable,omitempty"`
	Probes        map[string]ProbeHealth `json:"probes"`
	LastProbeRun  time.Time              `json:"last_probe_run"`
	ProbeRuns     uint64                 `json:"probe_runs"`
	ProbeFailures uint64                 `json:"probe_failures"`
	Requests      uint64                 `json:"requests"`
	AuthFailures  uint64                 `json:"auth_failures"`
	CollectedAt   time.Time              `json:"collected_at"`
}

// Counters are the error and request tallies /health reports.
type Counters struct {
	ProbeRuns     atomic.Uint64
	ProbeFailures atomic.Uint64
	Requests      atomic.Uint64
	AuthFailures  atomic.Uint64
}

// State holds the probe snapshot the endpoints serve. Requests read this snapshot and never
// trigger a collection, so N pollers cannot cost N times the collection.
type State struct {
	mu        sync.RWMutex
	startedAt time.Time
	platform  PlatformInfo
	listener  ListenerInfo
	probes    ProbeSet
	counters  Counters
}

func NewState(startedAt time.Time, platform PlatformInfo) *State {
	return &State{startedAt: startedAt, platform: platform}
}

func (s *State) SetListener(l ListenerInfo) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.listener = l
}

func (s *State) SetProbes(p ProbeSet) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.probes = p
	if p.Firmware != "" {
		s.platform.Firmware = p.Firmware
	}
	s.counters.ProbeRuns.Add(1)
	s.counters.ProbeFailures.Add(uint64(len(p.Unavailable())))
}

func (s *State) Capabilities() Capabilities {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return Capabilities{
		Agent: AgentInfo{
			Version:       version,
			BinaryVersion: binaryVersion(),
			StartedAt:     s.startedAt,
			PID:           processID(),
		},
		Platform:       s.platform,
		Listener:       s.listener,
		Vaps:           s.probes.Vaps,
		Radios:         s.probes.Radios,
		Probes:         s.probes.Results,
		ControlSurface: s.probes.ControlSurface,
		Interfaces:     collectInterfaces(),
		ProbedAt:       s.probes.ProbedAt,
		CollectedAt:    time.Now().UTC(),
	}
}

func (s *State) Health() Health {
	s.mu.RLock()
	defer s.mu.RUnlock()

	probes := make(map[string]ProbeHealth, len(s.probes.Results))
	for _, r := range s.probes.Results {
		probes[r.Name] = ProbeHealth{Available: r.Available, CheckedAt: r.CheckedAt}
	}
	unavailable := s.probes.Unavailable()
	now := time.Now().UTC()

	return Health{
		Version:       version,
		BinaryVersion: binaryVersion(),
		StartedAt:     s.startedAt,
		UptimeSeconds: int64(now.Sub(s.startedAt).Seconds()),
		Degraded:      len(unavailable) > 0,
		Unavailable:   unavailable,
		Probes:        probes,
		LastProbeRun:  s.probes.ProbedAt,
		ProbeRuns:     s.counters.ProbeRuns.Load(),
		ProbeFailures: s.counters.ProbeFailures.Load(),
		Requests:      s.counters.Requests.Load(),
		AuthFailures:  s.counters.AuthFailures.Load(),
		CollectedAt:   now,
	}
}
