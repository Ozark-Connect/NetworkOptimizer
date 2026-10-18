package main

import (
	"fmt"
	"log/slog"
	"math"
	"os"
	"reflect"
	"strconv"
	"strings"
	"time"
)

// probBackend is the iptables match that implements a traffic class's probability.
type probBackend string

const (
	// backendStatistic is xt_statistic in nth mode: deterministic round-robin. IPQ gateways only.
	backendStatistic probBackend = "statistic"
	// backendDynRandom is Ubiquiti's xt_dyn_random, which native UniFi load balancing uses. It
	// ships on every gateway family, including the UDM line, EFG, and UDM-Beast, which lack
	// an xt_statistic kernel module.
	backendDynRandom probBackend = "dyn_random"
	// backendNone means neither match loads: only 100% classes can be applied.
	backendNone probBackend = "none"
)

const (
	probeChain    = "WAN_STEER_PROBE"
	dynRandomProc = "/proc/net/xt_dyn_random"
	// dynRandomSticky is the source+destination stickiness flag native UniFi writes: the match
	// decides once per (src,dst) flow instead of per packet.
	dynRandomSticky = 3
	// probEpsilon absorbs float error, so 0.2/0.2 in a cascade counts as 100%.
	probEpsilon = 1e-9
)

// activeBackend is resolved once at startup by resolveProbBackend. It defaults to statistic,
// the backend every daemon before this one used.
var activeBackend = backendStatistic

// Test seams: tests replace these to observe /proc writes and skip the probe retry delay.
var (
	writeProcFile   = func(path, value string) error { return os.WriteFile(path, []byte(value), 0644) }
	probeRetryDelay = 2 * time.Second
)

// resolveProbBackend picks the probability match by attempting a rule with each candidate.
// Availability is never read from /proc/net/ip_tables_matches (it lists only loaded matches) or
// from the presence of libxt_statistic.so: the UDM line ships the .so without the kernel module.
// A "none" result is retried, so xtables lock contention at boot cannot pin the daemon to it.
func resolveProbBackend() probBackend {
	for attempt := 0; ; attempt++ {
		if probeMatch("-m", "statistic", "--mode", "nth", "--every", "2", "--packet", "0") {
			return backendStatistic
		}
		if probeMatch("-m", "dyn_random", "--prob-name", "netopt-ws-probe") {
			return backendDynRandom
		}
		if attempt >= 2 {
			return backendNone
		}
		time.Sleep(probeRetryDelay)
	}
}

// probeMatch reports whether a mangle rule with the given match can be inserted. The rule lives
// in a throwaway chain, which is removed again either way. Inserting it auto-loads the module.
func probeMatch(match ...string) bool {
	run("iptables", "-t", "mangle", "-N", probeChain) // ignore error: may survive a crashed probe
	run("iptables", "-t", "mangle", "-F", probeChain)
	defer func() {
		run("iptables", "-t", "mangle", "-F", probeChain)
		run("iptables", "-t", "mangle", "-X", probeChain)
	}()
	args := append([]string{"-t", "mangle", "-A", probeChain}, match...)
	args = append(args, "-j", "RETURN")
	return run("iptables", args...) == nil
}

// isWeighted reports whether a class steers less than all of its matching traffic, and so
// needs a probability match.
func isWeighted(tc *TrafficClass) bool {
	return tc.Probability < 1.0-probEpsilon
}

// isRefused reports whether a class is skipped because no probability backend is available.
func isRefused(tc *TrafficClass) bool {
	return activeBackend == backendNone && isWeighted(tc)
}

// conditionalProbabilities converts each enabled weighted class's absolute probability into the
// probability it must apply to the traffic still unmarked when it is reached.
//
// Weighted MARK rules only match unmarked packets, so with identical match criteria the rules
// cascade: 50/30/20 across three WANs becomes 0.5, then 0.3/0.5 = 0.6, then 0.2/0.2 = 1.0.
// Classes whose criteria differ do not share traffic in a defined way and keep their absolute
// probability. Disabled and 100% classes do not take part.
func conditionalProbabilities(classes []TrafficClass) []float64 {
	result := make([]float64, len(classes))
	for i := range classes {
		result[i] = classes[i].Probability
	}
	assigned := make(map[int]bool)
	for i := range classes {
		if assigned[i] || !classes[i].Enabled || !isWeighted(&classes[i]) {
			continue
		}
		remaining := 1.0
		for j := i; j < len(classes); j++ {
			tc := &classes[j]
			if !tc.Enabled || !isWeighted(tc) || !reflect.DeepEqual(tc.Match, classes[i].Match) {
				continue
			}
			assigned[j] = true
			if remaining <= probEpsilon {
				result[j] = 1.0
			} else {
				result[j] = math.Min(1.0, tc.Probability/remaining)
			}
			remaining -= tc.Probability
		}
	}
	return result
}

// probabilityMatch returns the match args that apply probability p, or nil when p needs no
// match (100%). name is the dyn_random entry for this class; statistic ignores it.
func probabilityMatch(p float64, name string) []string {
	switch activeBackend {
	case backendStatistic:
		// nth matches every Nth packet: 0.5 = every 2, 0.33 = every 3.
		every := int(math.Round(1.0 / p))
		if every <= 1 {
			return nil
		}
		return []string{"-m", "statistic", "--mode", "nth",
			"--every", strconv.Itoa(every), "--packet", "0"}
	case backendDynRandom:
		if p >= 1.0-probEpsilon {
			return nil
		}
		return []string{"-m", "dyn_random", "--prob-name", name}
	}
	return nil
}

// dynRandomName returns the dyn_random entry name for a class. It is stable across re-applies, so
// a re-apply overwrites its own entry, and never collides with UniFi's *-wf-group-*-randomweight.
func dynRandomName(classIndex int, wan *WANInterface) string {
	mark := strings.TrimPrefix(strings.ToLower(wan.FWMark), "0x")
	return fmt.Sprintf("netopt-ws-%s-%d", mark, classIndex)
}

// dynRandomValue encodes probability p as the /proc value: (sticky << 8) | prob. The match
// selects a flow when its per-flow byte is <= prob, so floor(p*255) reproduces native UniFi's
// 50% (127) and 100% (255) values.
func dynRandomValue(p float64) int {
	prob := int(math.Floor(p*255 + probEpsilon))
	prob = max(0, min(255, prob))
	return dynRandomSticky<<8 | prob
}

// setDynRandomProb writes probability p to a dyn_random entry. The entry exists only once a rule
// references it, so call this after the rule is in place.
func setDynRandomProb(name string, p float64) error {
	value := strconv.Itoa(dynRandomValue(p))
	if err := writeProcFile(dynRandomProc+"/"+name, value); err != nil {
		return fmt.Errorf("set dyn_random %s: %w", name, err)
	}
	slog.Debug("dyn_random probability set", "name", name, "probability", p, "value", value)
	return nil
}

// unsupportedClasses returns the enabled classes skipped because no probability backend loads.
func unsupportedClasses(cfg *Config) []string {
	var names []string
	for i := range cfg.TrafficClasses {
		tc := &cfg.TrafficClasses[i]
		if tc.Enabled && isRefused(tc) {
			names = append(names, tc.Name)
		}
	}
	return names
}
