package main

import (
	"context"
	"log/slog"
	"sync"
	"time"
)

// tierState is one collection tier's running outcome, reported on every payload so a consumer can
// tell a field that is absent from a field the AP reported as zero.
type tierState struct {
	mu        sync.Mutex
	available bool
	interval  time.Duration
	lastAt    time.Time
	lastErr   string
	runs      uint64
	failures  uint64
}

func (s *tierState) succeeded(at time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.runs++
	s.lastAt = at
	s.lastErr = ""
}

func (s *tierState) failed(err error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.runs++
	s.failures++
	if err != nil {
		s.lastErr = err.Error()
	}
}

func (s *tierState) setAvailable(available bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.available = available
}

func (s *tierState) info() TierInfo {
	s.mu.Lock()
	defer s.mu.Unlock()
	info := TierInfo{
		Available: s.available,
		Runs:      s.runs,
		Failures:  s.failures,
		LastError: s.lastErr,
	}
	if s.interval > 0 {
		info.IntervalSeconds = s.interval.Seconds()
	}
	if !s.lastAt.IsZero() {
		at := s.lastAt
		info.LastCollectedAt = &at
	}
	return info
}

// minTierRest is the shortest gap between two passes of a tier, whatever the pass cost. It exists
// so a tier can never spin: a pass that overruns its interval still yields, rather than issuing
// work back to back and turning a struggling access point into a hammered one.
const minTierRest = 250 * time.Millisecond

// Collector drives the three-tier model: pushed membership from the hostapd control socket, a fast
// RF poll, and a slow identity poll. Every tier writes the in-memory table; nothing here is driven
// by a request.
type Collector struct {
	cfg    *Config
	table  *Table
	ring   *EventRing
	events *EventSource
	syslog *SyslogSource

	mu     sync.RWMutex
	vaps   []string
	radios []string

	// fastPass counts fast tier passes and paces the sweep of empty VAPs. Only runFast touches it.
	fastPass uint64

	fast  tierState
	slow  tierState
	bytes tierState
	evt   tierState

	wg sync.WaitGroup
}

func NewCollector(cfg *Config, table *Table, ring *EventRing) *Collector {
	c := &Collector{cfg: cfg, table: table, ring: ring}
	c.events = NewEventSource(cfg.HostapdDir, ring, table.ApplyEvent)
	// stahtd's association quality and hostapd's UBNT_ROAM peer gossip only reach syslog, never
	// the control socket, so the roam phase timing and auth_rssi need this second source.
	c.syslog = NewSyslogSource(cfg.SyslogPath, ring)
	c.fast.interval = time.Duration(cfg.FastIntervalMs) * time.Millisecond
	c.slow.interval = time.Duration(cfg.SlowIntervalSeconds) * time.Second
	c.bytes.interval = time.Duration(cfg.BytesIntervalSeconds) * time.Second
	return c
}

// Apply takes the current probe results. Capabilities are re-probed under a running agent because a
// firmware upgrade or a provision cycle can change what resolves, and VAP names change with it.
func (c *Collector) Apply(ctx context.Context, p ProbeSet) {
	c.mu.Lock()
	c.vaps = append([]string(nil), p.Vaps...)
	c.radios = append([]string(nil), p.Radios...)
	c.mu.Unlock()

	fast, _ := p.Get(ProbeWlanconfig)
	slow, _ := p.Get(ProbeMcaDump)
	ctrl, _ := p.Get(ProbeHostapdCtrl)
	c.fast.setAvailable(fast.Available)
	c.slow.setAvailable(slow.Available)
	c.bytes.setAvailable(slow.Available)
	c.evt.setAvailable(ctrl.Available)

	c.events.Reconcile(ctx, p.Vaps)
	c.publishTiers()
}

// Start launches the poll tiers. Each loop waits only after its work finishes, so a collection that
// overruns its interval delays the next one rather than starting a second alongside it.
func (c *Collector) Start(ctx context.Context) {
	c.wg.Add(3)
	go func() {
		defer c.wg.Done()
		c.loop(ctx, &c.fast, c.runFast)
	}()
	go func() {
		defer c.wg.Done()
		c.loop(ctx, &c.slow, c.runSlow)
	}()
	go func() {
		defer c.wg.Done()
		c.loop(ctx, &c.bytes, c.runBytes)
	}()

	c.wg.Add(1)
	go func() {
		defer c.wg.Done()
		c.syslog.Run(ctx)
	}()
}

// Wait blocks until the poll tiers and every control-socket listener have stopped.
func (c *Collector) Wait() {
	c.wg.Wait()
	c.events.Wait()
}

func (c *Collector) loop(ctx context.Context, state *tierState, work func(context.Context)) {
	for ctx.Err() == nil {
		start := time.Now()
		work(ctx)
		c.publishTiers()

		// A pass that overruns its interval must still yield. Clamping to zero meant a tier whose
		// work outgrew its interval ran continuously, which turns a slow access point into a
		// hammered one: the slower it answers, the harder we ask.
		wait := state.interval - time.Since(start)
		if wait < minTierRest {
			wait = minTierRest
		}
		select {
		case <-ctx.Done():
			return
		case <-time.After(wait):
		}
	}
}

func (c *Collector) currentVaps() []string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return append([]string(nil), c.vaps...)
}

func (c *Collector) currentRadios() []string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return append([]string(nil), c.radios...)
}

// sweepDivisor paces VAPs holding no clients. Reading them every fifth pass bounds a missed
// association to five seconds, inside absentGrace, while removing most of the per-VAP fanout on an
// access point carrying many SSIDs - VAP count is SSIDs times bands and is unrelated to client count.
const sweepDivisor = 5

// dueVaps returns the VAPs to read this pass: every occupied one, plus the empty ones whose turn it
// is. The index staggers them so they do not all land on the same tick.
func dueVaps(vaps []string, occupied map[string]bool, pass uint64) []string {
	due := make([]string, 0, len(vaps))
	for i, v := range vaps {
		if occupied[v] || (pass+uint64(i))%sweepDivisor == 0 {
			due = append(due, v)
		}
	}
	return due
}

func (c *Collector) runFast(ctx context.Context) {
	if !c.fast.info().Available {
		return
	}
	vaps := c.currentVaps()
	if len(vaps) == 0 {
		return
	}
	now := time.Now().UTC()
	c.fastPass++
	due := dueVaps(vaps, c.table.OccupiedVaps(), c.fastPass)
	if len(due) == 0 {
		// Nothing due is not a failure. An access point with no clients would otherwise record one
		// every second and read as broken while being perfectly healthy.
		c.fast.succeeded(now)
		return
	}
	stations, covered := collectFast(ctx, due, now)
	if len(covered) == 0 {
		c.fast.failed(nil)
		return
	}
	c.table.ApplyFast(stations, covered, now)
	c.fast.succeeded(now)
}

// runBytes is the only tier that reads mca-dump, and it applies the whole snapshot. Never split the
// quality fields back onto their own pass: it costs a second dump and dates them behind counters
// from the same read.
//
// One dump for the access point, never one apstats per station - per-station calls scale with the
// client count, mca-dump is flat at ~400 ms.
func (c *Collector) runBytes(ctx context.Context) {
	if !c.bytes.info().Available {
		return
	}
	now := time.Now().UTC()
	snap, err := collectSlow(ctx, now)
	if err != nil {
		c.bytes.failed(err)
		c.slow.failed(err)
		slog.Warn("mca-dump collection failed", "error", err)
		return
	}

	readings := make(map[string]StaBytes, len(snap.Stations))
	for _, s := range snap.Stations {
		if s.TxBytes == 0 && s.RxBytes == 0 {
			continue
		}
		readings[stationKey(s.Vap, s.MAC)] = StaBytes{TxBytes: s.TxBytes, RxBytes: s.RxBytes, At: now}
	}

	previous := c.table.Radios()
	c.table.ApplySlow(snap, now)
	c.table.ApplyBytes(readings, now)
	// A channel change leaves the held iw answer a pass stale, and the slow tier's counter
	// tools can push its next read minutes out during a reprovision. Re-read here, on this
	// tier's cadence, whenever a serving radio has a channel but no center.
	if c.table.CentersStale() {
		c.table.SetRadioCenters(collectRadioCenters(ctx), now)
	}
	// After the center refresh, so a move's destination carries its block when iw answered.
	// Straight onto the ring: ApplyEvent ignores an event with no client, which this has none.
	for _, e := range channelChanges(previous, c.table.Radios(), now) {
		c.ring.Add(e)
	}
	c.bytes.succeeded(now)
	c.slow.succeeded(now)
}

// runSlow reads no mca-dump; the bytes tier does that. Only the radio counters are left, and they
// keep the wider interval because their deltas need it.
func (c *Collector) runSlow(ctx context.Context) {
	if !c.slow.info().Available {
		return
	}
	now := time.Now().UTC()

	// The block center changes only with the channel, so it rides this tier. One netlink dump
	// per pass, well under the mca-dump cost, and read before the counter tools so their
	// timeouts on a reprovisioning access point cannot hold it back.
	c.table.SetRadioCenters(collectRadioCenters(ctx), now)

	// Radio counters ride the slow tier: the CCA wedge is read from deltas, and a delta needs two
	// samples an interval apart rather than two samples a second apart. This runs even when the
	// tools give nothing, because mca-dump's own cu_* counters still want their deltas computed.
	counters, sources := map[string]map[string]int64{}, map[string][]string{}
	if radios := c.currentRadios(); len(radios) > 0 {
		counters, sources = collectRadioCounters(ctx, radios)
	}
	c.table.SetRadioCounters(counters, sources, now)
}

func (c *Collector) publishTiers() {
	c.table.SetTiers(TierStatus{
		Events: c.evt.info(),
		Fast:   c.fast.info(),
		Slow:   c.slow.info(),
		Bytes:  c.bytes.info(),
	})
}

// EventStats is the control-socket tier's own health, which /health reports alongside the ring.
type EventStats struct {
	AttachedVaps []string          `json:"attached_vaps"`
	Reconnects   uint64            `json:"reconnects"`
	Ignored      uint64            `json:"ignored_lines"`
	UnknownKinds map[string]uint64 `json:"unknown_kinds,omitempty"`
	Ring         RingStats         `json:"ring"`
}

func (c *Collector) EventStats() EventStats {
	return EventStats{
		AttachedVaps: c.events.Attached(),
		Reconnects:   c.events.Reconnects(),
		Ignored:      c.events.Ignored(),
		UnknownKinds: c.events.UnknownKinds(),
		Ring:         c.ring.Stats(),
	}
}
