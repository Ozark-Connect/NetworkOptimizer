package main

import (
	"context"
	"fmt"
	"strings"
	"time"
)

// Probe names, stable across releases because the collector keys on them.
const (
	ProbeHostapdCtrl = "hostapd_ctrl"
	ProbeWlanconfig  = "wlanconfig"
	ProbeMcaDump     = "mca_dump"
	ProbeUbus        = "ubus"
	ProbeStahtd      = "stahtd"
	ProbeAthstats    = "athstats"
)

// ProbeResult is one capability, resolved by behavior. Only the hostapd control socket is fatal;
// every other probe degrades a single feature and the agent keeps running.
type ProbeResult struct {
	Name      string    `json:"name"`
	Available bool      `json:"available"`
	Fatal     bool      `json:"fatal"`
	Detail    string    `json:"detail,omitempty"`
	Degrades  string    `json:"degrades,omitempty"`
	CheckedAt time.Time `json:"checked_at"`
}

// ProbeSet is one full pass of the capability probes.
type ProbeSet struct {
	Results        []ProbeResult    `json:"results"`
	Vaps           []string         `json:"vaps"`
	Radios         []string         `json:"radios"`
	ControlSurface []ControlSurface `json:"control_surface"`
	Firmware       string           `json:"firmware,omitempty"`
	ProbedAt       time.Time        `json:"probed_at"`
}

// FatalFailure returns the fatal probe that did not resolve, if any.
func (p ProbeSet) FatalFailure() (ProbeResult, bool) {
	for _, r := range p.Results {
		if r.Fatal && !r.Available {
			return r, true
		}
	}
	return ProbeResult{}, false
}

// Get returns a named probe result.
func (p ProbeSet) Get(name string) (ProbeResult, bool) {
	for _, r := range p.Results {
		if r.Name == name {
			return r, true
		}
	}
	return ProbeResult{}, false
}

// Unavailable lists the probes that did not resolve, for the startup summary.
func (p ProbeSet) Unavailable() []string {
	var out []string
	for _, r := range p.Results {
		if !r.Available {
			out = append(out, r.Name)
		}
	}
	return out
}

// runProbes resolves every capability by behavior. Never match a model string or firmware version:
// an allowlist breaks on each new SKU, and a shape check does not.
func runProbes(ctx context.Context, cfg *Config) ProbeSet {
	now := time.Now().UTC()
	set := ProbeSet{ProbedAt: now}

	vaps, vapErr := discoverVaps(cfg.HostapdDir)
	set.Vaps = vaps
	set.Results = append(set.Results, probeHostapdCtrl(cfg, vaps, vapErr, now))

	set.Results = append(set.Results, probeWlanconfig(ctx, vaps, now))

	mca, mcaResult := probeMcaDump(ctx, now)
	set.Results = append(set.Results, mcaResult)
	set.Firmware = mca.Version

	set.Radios = mergeRadios(radiosFromVaps(vaps), mca.RadioNames)

	ubusOK, ubusResult := probeUbus(ctx, now)
	set.Results = append(set.Results, ubusResult)
	if ubusOK {
		set.ControlSurface = inventoryControlSurface(ctx, vaps)
	}

	set.Results = append(set.Results, probeStahtd(cfg, now))
	set.Results = append(set.Results, probeAthstats(ctx, set.Radios, now))

	return set
}

func probeHostapdCtrl(cfg *Config, vaps []string, vapErr error, now time.Time) ProbeResult {
	r := ProbeResult{Name: ProbeHostapdCtrl, Fatal: true, CheckedAt: now,
		Degrades: "no event source, the agent cannot run"}

	if vapErr != nil {
		r.Detail = vapErr.Error()
		return r
	}
	if len(vaps) == 0 {
		r.Detail = fmt.Sprintf("no VAP sockets in %s", cfg.HostapdDir)
		return r
	}

	var lastErr error
	for _, vap := range vaps {
		reply, err := pingHostapd(cfg.HostapdDir, vap, 2*time.Second)
		if err != nil {
			lastErr = err
			continue
		}
		if strings.HasPrefix(reply, "PONG") {
			r.Available = true
			r.Detail = fmt.Sprintf("PONG from %s, %d VAP sockets", vap, len(vaps))
			return r
		}
		lastErr = fmt.Errorf("%s answered %q, expected PONG", vap, reply)
	}
	r.Detail = fmt.Sprintf("no VAP answered PING: %v", lastErr)
	return r
}

func probeWlanconfig(ctx context.Context, vaps []string, now time.Time) ProbeResult {
	r := ProbeResult{Name: ProbeWlanconfig, CheckedAt: now, Degrades: "no fast RF metrics"}
	if len(vaps) == 0 {
		r.Detail = "no VAP to probe against"
		return r
	}
	out, err := runCommand(ctx, 5*time.Second, "wlanconfig", vaps[0], "list", "sta")
	if err != nil {
		r.Detail = err.Error()
		return r
	}
	cols, ok := parseWlanconfigHeader(out)
	if !ok {
		r.Detail = fmt.Sprintf("unrecognized output from wlanconfig %s list sta", vaps[0])
		return r
	}
	r.Available = true
	r.Detail = fmt.Sprintf("%s: %d columns", vaps[0], len(cols))
	return r
}

func probeMcaDump(ctx context.Context, now time.Time) (mcaSummary, ProbeResult) {
	r := ProbeResult{Name: ProbeMcaDump, CheckedAt: now, Degrades: "no identity (ip, hostname)"}
	out, err := runCommand(ctx, 20*time.Second, "mca-dump")
	if err != nil {
		r.Detail = err.Error()
		return mcaSummary{}, r
	}
	summary, err := parseMcaDump([]byte(out))
	if err != nil {
		r.Detail = err.Error()
		return mcaSummary{}, r
	}
	r.Available = true
	r.Detail = fmt.Sprintf("%d radios, %d VAPs, %d KB", summary.RadioCount, summary.VapCount, len(out)/1024)
	return summary, r
}

func probeUbus(ctx context.Context, now time.Time) (bool, ProbeResult) {
	r := ProbeResult{Name: ProbeUbus, CheckedAt: now, Degrades: "no get_clients, no control inventory"}
	out, err := runCommand(ctx, 5*time.Second, "ubus", "list")
	if err != nil {
		r.Detail = err.Error()
		return false, r
	}
	objects := parseUbusObjects(out, "hostapd")
	if len(objects) == 0 {
		r.Detail = "ubus list returned no hostapd objects"
		return false, r
	}
	r.Available = true
	r.Detail = fmt.Sprintf("%d hostapd objects", len(objects))
	return true, r
}

func probeStahtd(cfg *Config, now time.Time) ProbeResult {
	r := ProbeResult{Name: ProbeStahtd, CheckedAt: now, Degrades: "no roam phase timing or auth_rssi"}
	data, err := tailFile(cfg.SyslogPath, cfg.SyslogTailBytes)
	if err != nil {
		r.Detail = err.Error()
		return r
	}
	if !containsStahtd(data) {
		r.Detail = fmt.Sprintf("no %s line in the last %d KB of %s", stahtdMarker, cfg.SyslogTailBytes/1024, cfg.SyslogPath)
		return r
	}
	r.Available = true
	r.Detail = fmt.Sprintf("%s lines present in %s", stahtdMarker, cfg.SyslogPath)
	return r
}

func probeAthstats(ctx context.Context, radios []string, now time.Time) ProbeResult {
	r := ProbeResult{Name: ProbeAthstats, CheckedAt: now, Degrades: "no radio health"}
	if len(radios) == 0 {
		r.Detail = "no radio discovered to probe against"
		return r
	}
	radio := radios[0]

	if out, err := runCommand(ctx, 10*time.Second, "athstats", "-i", radio); err == nil {
		if found := matchedRadioCounters(out); len(found) > 0 {
			r.Available = true
			r.Detail = fmt.Sprintf("athstats -i %s: %s", radio, strings.Join(found, ", "))
			return r
		}
	}
	// apstats needs a LEVEL flag: bare -R is AP level and carries no cycle counters at all, which
	// answers successfully with nothing useful rather than failing.
	if out, err := runCommand(ctx, 10*time.Second, "apstats", "-r", "-i", radio); err == nil {
		if found := matchedRadioCounters(out); len(found) > 0 {
			r.Available = true
			r.Detail = fmt.Sprintf("apstats -r -i %s: %s", radio, strings.Join(found, ", "))
			return r
		}
	}
	r.Detail = fmt.Sprintf("neither athstats -i %s nor apstats -r -i %s returned known counters", radio, radio)
	return r
}
