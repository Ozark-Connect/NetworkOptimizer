package main

import (
	"context"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// maxRadioCounters bounds one radio's counter map. The measured athstats output alone carries ~350
// counters, so the cap is headroom over a known shape rather than a guess.
const maxRadioCounters = 512

// u32Sentinel is a saturated or negative-one u32 counter. Its value is served as read, but it is
// never differenced: the delta would be noise rather than a rate.
const u32Sentinel = 4294967295

// apstatsRadioHeader names the radio a radio-level apstats answered for. It is checked rather than
// trusted, because -i selects the interface and a mismatch would attribute one radio's counters to
// another.
var apstatsRadioHeader = regexp.MustCompile(`^Radio Level Stats:\s*(\S+)`)

// parentheticals are stripped from a counter label before it is normalized. It is what turns
// "Chan NF (BDF averaged NF_dBm)" into chan_nf, which is the name the wedge research uses.
var parentheticals = regexp.MustCompile(`\([^)]*\)`)

var counterKeyStrip = regexp.MustCompile(`[^a-z0-9]+`)

// counterKey normalizes a counter label to snake_case so the same counter from two tools lands on
// one key. "Rx Clear Cnt" and "rx_clear_cnt" are the same counter.
func counterKey(label string) string {
	label = parentheticals.ReplaceAllString(label, " ")
	k := counterKeyStrip.ReplaceAllString(strings.ToLower(strings.TrimSpace(label)), "_")
	return strings.Trim(k, "_")
}

// parseCounterInt accepts the decimal and hex forms these tools mix within one output. A value the
// tool prints as text, such as <DISABLED>, is not a counter and must not become a zero.
func parseCounterInt(tok string) (int64, bool) {
	tok = strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(tok), ","))
	if tok == "" || strings.HasPrefix(tok, "<") {
		return 0, false
	}
	if strings.HasPrefix(tok, "0x") || strings.HasPrefix(tok, "0X") {
		v, err := strconv.ParseInt(tok[2:], 16, 64)
		return v, err == nil
	}
	if v, err := strconv.ParseInt(tok, 10, 64); err == nil {
		return v, true
	}
	if f, err := strconv.ParseFloat(tok, 64); err == nil {
		return int64(f), true
	}
	return 0, false
}

// counterSink collects counters under section prefixes, keeping the map bounded and refusing to
// let one counter silently overwrite another.
type counterSink struct {
	counters map[string]int64
	section  string
}

func newCounterSink() *counterSink {
	return &counterSink{counters: make(map[string]int64, 64)}
}

// put stores a counter under its section. Section prefixing is load-bearing: apstats prints
// "Best effort" under both a Tx and an Rx heading, and without it the second silently replaces the
// first. Where two labels in the SAME section still normalize alike, the later one takes a numbered
// suffix rather than overwriting.
func (c *counterSink) put(label, value string) {
	v, ok := parseCounterInt(value)
	if !ok {
		return
	}
	key := counterKey(label)
	if key == "" {
		return
	}
	if c.section != "" {
		key = c.section + "." + key
	}
	if existing, taken := c.counters[key]; taken && existing != v {
		for n := 2; n < 10; n++ {
			candidate := key + "_" + strconv.Itoa(n)
			if _, taken := c.counters[candidate]; !taken {
				key = candidate
				break
			}
		}
	}
	if _, exists := c.counters[key]; !exists && len(c.counters) >= maxRadioCounters {
		return
	}
	c.counters[key] = v
}

// splitCounterLine splits a counter line into label and value. "=" wins over ":" wherever both
// appear, because apstats writes "Created vap:  = 1" and "lithium_cycle_cnt: Chan NF ... = -92",
// where the colon sits inside the label rather than separating it.
func splitCounterLine(line string) (label, value string, ok bool) {
	if i := strings.LastIndex(line, "="); i >= 0 {
		return line[:i], line[i+1:], true
	}
	if i := strings.Index(line, ":"); i >= 0 {
		return line[:i], line[i+1:], true
	}
	return "", "", false
}

// isSectionHeader recognizes a heading that introduces counters rather than carrying one. athstats
// writes unindented prose ("Tx ingress stats"); apstats writes a trailing colon with no value.
func isSectionHeader(line string) bool {
	trimmed := strings.TrimSpace(line)
	if trimmed == "" {
		return false
	}
	if strings.HasSuffix(trimmed, ":") {
		return true
	}
	// An unindented line with no separator at all is athstats' section prose.
	return !strings.ContainsAny(trimmed, "=:")
}

// parseRadioCounters reads the counter shapes the radio-stats tools actually emit, measured on
// firmware 8.7.11: athstats writes "\tlabel   :\tvalue" under unindented section prose plus rate
// tables as "\t label = value", and apstats writes "label = value" with indented members under a
// "Section:" heading. Both are handled in one pass because both are section-scoped key spaces.
func parseRadioCounters(out string) map[string]int64 {
	sink := newCounterSink()

	for _, raw := range strings.Split(out, "\n") {
		line := strings.TrimRight(raw, " \t\r")
		if strings.TrimSpace(line) == "" {
			continue
		}
		indented := strings.HasPrefix(line, " ") || strings.HasPrefix(line, "\t")

		if isSectionHeader(line) {
			sink.section = counterKey(line)
			continue
		}
		// An unindented counter closes the section above it. apstats returns to top-level counters
		// straight after an indented block, and filing those under the stale heading would misname them.
		if !indented {
			sink.section = ""
		}

		label, value, ok := splitCounterLine(line)
		if !ok {
			continue
		}
		// A counter that carries its own prefix is already namespaced; keeping the prefix would
		// bury the name the wedge detector reads.
		if trimmed := strings.TrimSpace(label); strings.HasPrefix(trimmed, "lithium_cycle_cnt:") {
			label = strings.TrimPrefix(trimmed, "lithium_cycle_cnt:")
			saved := sink.section
			sink.section = ""
			sink.put(label, value)
			sink.section = saved
			continue
		}
		sink.put(label, value)
	}
	return sink.counters
}

// apstatsRadio returns the radio a radio-level apstats output is about.
func apstatsRadio(out string) string {
	for _, line := range strings.Split(out, "\n") {
		if m := apstatsRadioHeader.FindStringSubmatch(strings.TrimSpace(line)); m != nil {
			return m[1]
		}
	}
	return ""
}

// mergeCounters folds src into dst without letting the map grow past the cap.
func mergeCounters(dst, src map[string]int64) map[string]int64 {
	if len(src) == 0 {
		return dst
	}
	if dst == nil {
		dst = make(map[string]int64, len(src))
	}
	for k, v := range src {
		if _, exists := dst[k]; !exists && len(dst) >= maxRadioCounters {
			continue
		}
		dst[k] = v
	}
	return dst
}

// wedgeCounters are the counters the 6 GHz CCA wedge is read from: Rx Clear close to Cycle with a
// Tx Frame delta of zero is the fault. A zero delta is the signature, so these are reported even
// when they did not move; every other counter is omitted at zero to keep the payload bounded.
var wedgeCounters = map[string]bool{
	"rx_clear_cnt": true,
	"cycle_cnt":    true,
	"tx_frame_cnt": true,
	"rx_frame_cnt": true,
	"phy_err_cnt":  true,
	"pdev_resets":  true,
	"cu_total":     true,
	"cu_interf":    true,
	"cu_self_tx":   true,
	"cu_self_rx":   true,
}

// promoteHealthCounters copies the health counters to a bare key alongside their sectioned one.
// athstats files pdev_resets under HTT_TX_PDEV_STATS_CMN_TLV and apstats files the cycle counters
// at top level, so without this the consumer would have to know which tool and section a counter
// came from to find it.
func promoteHealthCounters(counters map[string]int64) {
	if counters == nil {
		return
	}
	for key, v := range counters {
		bare := bareCounterName(key)
		if bare == key || !wedgeCounters[bare] {
			continue
		}
		if _, taken := counters[bare]; !taken {
			counters[bare] = v
		}
	}
}

// counterDeltas is what the CCA wedge detector needs: the agent holds the previous sample so the
// server does not have to reconstruct it across polls. A counter that went backwards yields no
// delta, because a radio reset zeroes them.
func counterDeltas(prev, cur map[string]int64) map[string]int64 {
	if len(prev) == 0 || len(cur) == 0 {
		return nil
	}
	deltas := make(map[string]int64, 32)
	for k, v := range cur {
		p, ok := prev[k]
		if !ok || v < p || v == u32Sentinel || p == u32Sentinel {
			continue
		}
		if d := v - p; d != 0 || wedgeCounters[bareCounterName(k)] {
			deltas[k] = d
		}
	}
	if len(deltas) == 0 {
		return nil
	}
	return deltas
}

// bareCounterName drops the section prefix, so a wedge counter is recognized wherever a tool files it.
func bareCounterName(key string) string {
	if i := strings.LastIndex(key, "."); i >= 0 {
		return key[i+1:]
	}
	return key
}

// collectRadioCounters takes the union of what the radio-stats tools report. The counter sets
// differ between them and neither is a superset, so both are asked and the results merged.
//
// apstats is invoked -r -i <radio> for radio level. Bare -R is AP level and carries no cycle
// counters at all, which reads as a healthy empty answer rather than an error.
func collectRadioCounters(ctx context.Context, radios []string) (map[string]map[string]int64, map[string][]string) {
	counters := make(map[string]map[string]int64, len(radios))
	sources := make(map[string][]string, len(radios))

	for _, radio := range radios {
		if out, err := runCommand(ctx, 10*time.Second, "athstats", "-i", radio); err == nil {
			if found := parseRadioCounters(out); len(found) > 0 {
				counters[radio] = mergeCounters(counters[radio], found)
				sources[radio] = append(sources[radio], "athstats")
			}
		}

		out, err := runCommand(ctx, 10*time.Second, "apstats", "-r", "-i", radio)
		if err != nil {
			continue
		}
		// Attribute by what the output says it is, not by what was asked for.
		if named := apstatsRadio(out); named != "" && named != radio {
			continue
		}
		if found := parseRadioCounters(out); len(found) > 0 {
			counters[radio] = mergeCounters(counters[radio], found)
			sources[radio] = append(sources[radio], "apstats")
		}
	}
	for _, found := range counters {
		promoteHealthCounters(found)
	}
	return counters, sources
}
