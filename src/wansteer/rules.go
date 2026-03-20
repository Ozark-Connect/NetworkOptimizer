package main

import (
	"fmt"
	"log/slog"
	"os/exec"
	"strings"
)

const chainName = "WAN_STEER"

// applyRules creates the WAN_STEER chain and populates it from config.
// Idempotent: flushes the chain if it already exists.
func applyRules(cfg *Config) error {
	// Create chain (ignore error if already exists)
	run("iptables", "-t", "mangle", "-N", chainName)

	// Flush any existing rules
	if err := run("iptables", "-t", "mangle", "-F", chainName); err != nil {
		return fmt.Errorf("flush chain: %w", err)
	}

	// Add rules for each enabled traffic class
	for _, tc := range cfg.TrafficClasses {
		if !tc.Enabled {
			continue
		}
		wan, ok := cfg.WANInterfaces[tc.TargetWAN]
		if !ok {
			continue
		}
		if err := addTrafficClassRules(&tc, &wan); err != nil {
			return fmt.Errorf("traffic class %q: %w", tc.Name, err)
		}
	}

	// Ensure jump from PREROUTING exists (insert at position 1)
	if !hasJump() {
		if err := run("iptables", "-t", "mangle", "-I", "PREROUTING", "1", "-j", chainName); err != nil {
			return fmt.Errorf("insert PREROUTING jump: %w", err)
		}
	}

	slog.Info("rules applied", "traffic_classes", countEnabled(cfg))
	return nil
}

// addTrafficClassRules generates iptables rules for a single traffic class.
// It builds the cross-product of source × destination matchers, each combined
// with protocol/port filters.
func addTrafficClassRules(tc *TrafficClass, wan *WANInterface) error {
	// Build all source matchers
	srcMatchers := buildSourceMatchers(&tc.Match)

	// Build all destination matchers
	dstMatchers := buildDestMatchers(&tc.Match)

	// Build shared args: protocol, ports, probability
	sharedArgs := buildSharedArgs(tc)

	// Cross-product: for each source × destination combination, add MARK + CONNMARK rules
	for _, src := range srcMatchers {
		for _, dst := range dstMatchers {
			// MARK rule gets the full shared args (protocol, ports, state NEW, probability)
			var markArgs []string
			markArgs = append(markArgs, "-t", "mangle", "-A", chainName)
			markArgs = append(markArgs, src...)
			markArgs = append(markArgs, dst...)
			markArgs = append(markArgs, sharedArgs...)
			markArgs = append(markArgs, "-j", "MARK", "--set-xmark", wan.FWMark+"/0x7e0000")
			if err := run("iptables", markArgs...); err != nil {
				return fmt.Errorf("add mark rule: %w", err)
			}

			// CONNMARK save rule: NO probability (save for every packet that got marked).
			// Uses src/dst/protocol/port matchers but checks fwmark instead of rolling dice again.
			var connmarkArgs []string
			connmarkArgs = append(connmarkArgs, "-t", "mangle", "-A", chainName)
			connmarkArgs = append(connmarkArgs, src...)
			connmarkArgs = append(connmarkArgs, dst...)
			connmarkArgs = append(connmarkArgs, connmarkSharedArgs(tc)...)
			connmarkArgs = append(connmarkArgs, "-m", "mark", "--mark", wan.FWMark+"/0x7e0000")
			connmarkArgs = append(connmarkArgs, "-j", "CONNMARK", "--save-mark",
				"--nfmask", "0x7e0000", "--ctmask", "0x7e0000")
			if err := run("iptables", connmarkArgs...); err != nil {
				return fmt.Errorf("add connmark rule: %w", err)
			}
		}
	}

	return nil
}

// buildSourceMatchers returns a list of iptables arg slices for source matching.
// Each entry is one OR branch (e.g., one src CIDR or one src MAC).
// Returns a single empty-args entry if no source filters are specified (match all sources).
func buildSourceMatchers(m *MatchCriteria) [][]string {
	var matchers [][]string

	for _, cidr := range m.SrcCIDRs {
		matchers = append(matchers, []string{"-s", cidr})
	}
	for _, r := range m.SrcRanges {
		matchers = append(matchers, []string{"-m", "iprange", "--src-range", r})
	}
	for _, mac := range m.SrcMACs {
		matchers = append(matchers, []string{"-m", "mac", "--mac-source", mac})
	}

	// If no source filters, match everything
	if len(matchers) == 0 {
		matchers = append(matchers, []string{})
	}
	return matchers
}

// buildDestMatchers returns a list of iptables arg slices for destination matching.
// Returns a single empty-args entry if no destination filters are specified.
func buildDestMatchers(m *MatchCriteria) [][]string {
	var matchers [][]string

	for _, cidr := range m.DstCIDRs {
		matchers = append(matchers, []string{"-d", cidr})
	}
	for _, r := range m.DstRanges {
		matchers = append(matchers, []string{"-m", "iprange", "--dst-range", r})
	}

	if len(matchers) == 0 {
		matchers = append(matchers, []string{})
	}
	return matchers
}

// buildSharedArgs returns iptables args shared across all rules for a traffic class:
// protocol, ports, state NEW, and probability.
func buildSharedArgs(tc *TrafficClass) []string {
	var args []string

	// Protocol (must come before port matching)
	if tc.Match.Protocol != "" {
		args = append(args, "-p", tc.Match.Protocol)
	}

	// Source ports (convert dash ranges to colon for iptables: 1024-4096 -> 1024:4096)
	if len(tc.Match.SrcPorts) > 0 {
		ports := normalizePortsForIptables(tc.Match.SrcPorts)
		if len(ports) == 1 {
			args = append(args, "--sport", ports[0])
		} else {
			args = append(args, "-m", "multiport", "--sports", strings.Join(ports, ","))
		}
	}

	// Destination ports
	if len(tc.Match.DstPorts) > 0 {
		ports := normalizePortsForIptables(tc.Match.DstPorts)
		if len(ports) == 1 {
			args = append(args, "--dport", ports[0])
		} else {
			args = append(args, "-m", "multiport", "--dports", strings.Join(ports, ","))
		}
	}

	// Only match NEW connections
	args = append(args, "-m", "state", "--state", "NEW")

	// Load balance probability
	args = append(args, "-m", "statistic", "--mode", "random",
		"--probability", fmt.Sprintf("%.10f", tc.Probability))

	return args
}

// connmarkSharedArgs returns protocol/port args for the CONNMARK save rule.
// Unlike buildSharedArgs, this excludes state NEW and probability - CONNMARK
// should save for every packet that was already marked, not roll the dice again.
func connmarkSharedArgs(tc *TrafficClass) []string {
	var args []string

	if tc.Match.Protocol != "" {
		args = append(args, "-p", tc.Match.Protocol)
	}

	if len(tc.Match.SrcPorts) > 0 {
		ports := normalizePortsForIptables(tc.Match.SrcPorts)
		if len(ports) == 1 {
			args = append(args, "--sport", ports[0])
		} else {
			args = append(args, "-m", "multiport", "--sports", strings.Join(ports, ","))
		}
	}

	if len(tc.Match.DstPorts) > 0 {
		ports := normalizePortsForIptables(tc.Match.DstPorts)
		if len(ports) == 1 {
			args = append(args, "--dport", ports[0])
		} else {
			args = append(args, "-m", "multiport", "--dports", strings.Join(ports, ","))
		}
	}

	return args
}

// removeRules tears down the WAN_STEER chain and all references to it.
func removeRules() error {
	// Remove jump from PREROUTING (may need multiple passes if duplicated)
	for i := 0; i < 10; i++ {
		if err := run("iptables", "-t", "mangle", "-D", "PREROUTING", "-j", chainName); err != nil {
			break
		}
	}

	// Flush and delete chain
	run("iptables", "-t", "mangle", "-F", chainName)
	run("iptables", "-t", "mangle", "-X", chainName)

	slog.Info("rules removed")
	return nil
}

// reapplyRules re-applies rules with unhealthy WANs disabled.
func reapplyRules(cfg *Config, disabledWANs map[string]bool) error {
	modified := *cfg
	modified.TrafficClasses = make([]TrafficClass, len(cfg.TrafficClasses))
	for i, tc := range cfg.TrafficClasses {
		modified.TrafficClasses[i] = tc
		if disabledWANs[tc.TargetWAN] {
			modified.TrafficClasses[i].Enabled = false
		}
	}
	return applyRules(&modified)
}

// hasJump checks if PREROUTING already has a jump to WAN_STEER.
func hasJump() bool {
	out, err := exec.Command("iptables", "-t", "mangle", "-L", "PREROUTING", "-n", "--line-numbers").Output()
	if err != nil {
		return false
	}
	return strings.Contains(string(out), chainName)
}

// chainExists checks if the WAN_STEER chain exists in mangle table.
func chainExists() bool {
	return run("iptables", "-t", "mangle", "-L", chainName, "-n") == nil
}

// ruleCount returns the number of rules in WAN_STEER.
func ruleCount() int {
	out, err := exec.Command("iptables", "-t", "mangle", "-L", chainName, "-n").Output()
	if err != nil {
		return -1
	}
	lines := strings.Split(strings.TrimSpace(string(out)), "\n")
	if len(lines) <= 2 {
		return 0
	}
	return len(lines) - 2
}

// expectedRuleCount returns how many iptables rules the current config should produce.
func expectedRuleCount(cfg *Config) int {
	count := 0
	for _, tc := range cfg.TrafficClasses {
		if !tc.Enabled {
			continue
		}
		srcCount := len(tc.Match.SrcCIDRs) + len(tc.Match.SrcRanges) + len(tc.Match.SrcMACs)
		if srcCount == 0 {
			srcCount = 1
		}
		dstCount := len(tc.Match.DstCIDRs) + len(tc.Match.DstRanges)
		if dstCount == 0 {
			dstCount = 1
		}
		// 2 rules (MARK + CONNMARK) per source × destination combination
		count += srcCount * dstCount * 2
	}
	return count
}

func countEnabled(cfg *Config) int {
	n := 0
	for _, tc := range cfg.TrafficClasses {
		if tc.Enabled {
			n++
		}
	}
	return n
}

// normalizePortsForIptables converts dash-style ranges (1024-4096) to colon-style (1024:4096) for iptables.
func normalizePortsForIptables(ports []string) []string {
	result := make([]string, len(ports))
	for i, p := range ports {
		result[i] = strings.ReplaceAll(p, "-", ":")
	}
	return result
}

// flushAllSteeredConntrack flushes conntrack entries for all non-default WANs.
// Called on clean shutdown so stale connmarks don't keep routing traffic to secondary WANs.
func flushAllSteeredConntrack(cfg *Config) {
	for name, wan := range cfg.WANInterfaces {
		if name == cfg.DefaultWAN || wan.FWMark == "" {
			continue
		}
		flushConntrackForMark(wan.FWMark)
	}
}

// activeTargetWANs returns the set of WAN names that have enabled traffic classes targeting them.
func activeTargetWANs(cfg *Config) map[string]bool {
	targets := make(map[string]bool)
	for _, tc := range cfg.TrafficClasses {
		if tc.Enabled {
			targets[tc.TargetWAN] = true
		}
	}
	return targets
}

// flushConntrackForMark deletes all conntrack entries with the given WAN fwmark.
// This forces existing connections to be re-routed through the default WAN
// when their assigned WAN goes down.
func flushConntrackForMark(fwmark string) {
	// conntrack -D -m <mark> deletes entries matching the mark
	// The mark includes the WAN bits in 0x7e0000, so we match on those
	err := run("conntrack", "-D", "-m", fwmark)
	if err != nil {
		// Not an error if there are no matching entries
		slog.Debug("conntrack flush", "fwmark", fwmark, "result", err)
	} else {
		slog.Info("flushed conntrack entries for dead wan", "fwmark", fwmark)
	}
}

// run executes a command and returns any error.
func run(name string, args ...string) error {
	cmd := exec.Command(name, args...)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s %s: %s (%w)", name, strings.Join(args, " "), strings.TrimSpace(string(out)), err)
	}
	return nil
}
