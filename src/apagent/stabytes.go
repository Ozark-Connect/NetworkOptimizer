package main

import (
	"context"
	"regexp"
	"strconv"
	"time"
)

// Per-station byte counters, read one station at a time.
//
// apstats at STA level reports exactly the counters mca-dump carries for the same station -
// verified byte for byte on hardware - so the direction convention is inherited rather than
// re-derived: Tx is AP to client (a download), Rx is client to AP (an upload). Do not "fix" these
// to read the other way; the server's TxThroughputBps means AP to client and depends on it.
//
// This exists because mca-dump is the only other source of these counters and costs ~400 ms a call,
// which is why it runs at 30 s. One apstats call is under a millisecond, so throughput can be
// resolved per poll instead of per write window.

const staBytesTimeout = 3 * time.Second

var (
	apstatsTxDataBytes = regexp.MustCompile(`(?m)^Tx Data Bytes\s+=\s+(\d+)`)
	apstatsRxDataBytes = regexp.MustCompile(`(?m)^Rx Data Bytes\s+=\s+(\d+)`)
)

// StaBytes is one station's cumulative counters at a moment.
type StaBytes struct {
	TxBytes int64
	RxBytes int64
	At      time.Time
}

// StaTarget names a station to read, by table key and MAC.
type StaTarget struct {
	Key string
	MAC string
}

// collectStaBytes reads each station's counters. A station that answers with neither counter is
// omitted rather than recorded as zero: zero is a real reading, and a client that has moved nothing
// must not be confused with one apstats could not answer for.
func collectStaBytes(ctx context.Context, targets []StaTarget) map[string]StaBytes {
	out := make(map[string]StaBytes, len(targets))

	for _, t := range targets {
		if ctx.Err() != nil {
			return out
		}
		raw, err := runCommand(ctx, staBytesTimeout, "apstats", "-s", "-m", t.MAC)
		if err != nil {
			continue
		}
		tx, txOK := firstInt64(apstatsTxDataBytes, raw)
		rx, rxOK := firstInt64(apstatsRxDataBytes, raw)
		if !txOK && !rxOK {
			continue
		}
		out[t.Key] = StaBytes{TxBytes: tx, RxBytes: rx, At: time.Now().UTC()}
	}

	return out
}

func firstInt64(re *regexp.Regexp, s string) (int64, bool) {
	m := re.FindStringSubmatch(s)
	if len(m) < 2 {
		return 0, false
	}
	v, err := strconv.ParseInt(m[1], 10, 64)
	if err != nil {
		return 0, false
	}
	return v, true
}
