package main

import (
	"context"
	"regexp"
	"sort"
	"strings"
	"time"
)

// controlMethodsOfInterest are the 802.11k/v and channel-control methods whose presence we want
// inventoried across the fleet. Phase 0 is read-only: these are reported, never called.
var controlMethodsOfInterest = []string{
	"wnm_disassoc_imminent",
	"rrm_nr_list",
	"rrm_beacon_req",
	"switch_chan",
}

// ControlSurface records which control methods a VAP exposes, without exercising any of them.
type ControlSurface struct {
	Vap        string          `json:"vap"`
	Watched    map[string]bool `json:"watched"`
	AllMethods []string        `json:"all_methods,omitempty"`
}

// parseUbusObjects keeps the lines of `ubus list` output that name an object under prefix.
func parseUbusObjects(out, prefix string) []string {
	objects := make([]string, 0, 8)
	for _, line := range strings.Split(out, "\n") {
		name := strings.TrimSpace(line)
		if name == "" || !strings.HasPrefix(name, prefix) {
			continue
		}
		objects = append(objects, name)
	}
	sort.Strings(objects)
	return objects
}

var ubusMethodLine = regexp.MustCompile(`^\s*"([A-Za-z0-9_.-]+)"\s*:`)

// parseUbusMethods extracts method names from `ubus -v list <object>` introspection output.
func parseUbusMethods(out string) []string {
	seen := map[string]bool{}
	methods := make([]string, 0, 16)
	for _, line := range strings.Split(out, "\n") {
		m := ubusMethodLine.FindStringSubmatch(line)
		if m == nil || seen[m[1]] {
			continue
		}
		seen[m[1]] = true
		methods = append(methods, m[1])
	}
	sort.Strings(methods)
	return methods
}

// watchedMethods marks which of controlMethodsOfInterest appear in methods.
func watchedMethods(methods []string) map[string]bool {
	have := map[string]bool{}
	for _, m := range methods {
		have[m] = true
	}
	watched := make(map[string]bool, len(controlMethodsOfInterest))
	for _, m := range controlMethodsOfInterest {
		watched[m] = have[m]
	}
	return watched
}

// inventoryControlSurface introspects each VAP's ubus object. Introspection only: no method here
// is ever invoked, because control is deferred and a read-only agent's worst failure is lost
// telemetry rather than a radio taken down.
func inventoryControlSurface(ctx context.Context, vaps []string) []ControlSurface {
	surfaces := make([]ControlSurface, 0, len(vaps))
	for _, vap := range vaps {
		out, err := runCommand(ctx, 5*time.Second, "ubus", "-v", "list", "hostapd."+vap)
		if err != nil {
			continue
		}
		methods := parseUbusMethods(out)
		if len(methods) == 0 {
			continue
		}
		surfaces = append(surfaces, ControlSurface{
			Vap:        vap,
			Watched:    watchedMethods(methods),
			AllMethods: methods,
		})
	}
	return surfaces
}
