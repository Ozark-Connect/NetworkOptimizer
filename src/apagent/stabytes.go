package main

import "time"

// Per-station byte counters, as the access point reports them.
//
// Sourced from mca-dump, which carries every station in one call. An earlier version read them per
// station with `apstats -s -m <mac>`: correct, and byte-identical to these, but it cost a process
// spawn and a firmware round-trip PER CLIENT every pass, so the load scaled with the client count
// without bound. Never reintroduce a per-station poll on a hot path for this reason.
//
// Direction is the access point's: Tx is to the client (a download), Rx is from it. The server's
// TxThroughputBps means the same thing and depends on it.
type StaBytes struct {
	TxBytes int64
	RxBytes int64
	At      time.Time
}
