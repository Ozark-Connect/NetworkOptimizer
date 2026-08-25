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
	stats, _ := p.Get(ProbeApstatsSta)
	c.fast.setAvailable(fast.Available)
	c.slow.setAvailable(slow.Available)
	c.bytes.setAvailable(stats.Available)
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

		wait := state.interval - time.Since(start)
		if wait < 0 {
			wait = 0
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

func (c *Collector) runFast(ctx context.Context) {
	if !c.fast.info().Available {
		return
	}
	vaps := c.currentVaps()
	if len(vaps) == 0 {
		return
	}
	now := time.Now().UTC()
	stations, covered := collectFast(ctx, vaps, now)
	if len(covered) == 0 {
		c.fast.failed(nil)
		return
	}
	c.table.ApplyFast(stations, covered, now)
	c.fast.succeeded(now)
}

// runBytes refreshes per-station counters on their own tier. They used to arrive only with the
// identity poll, which is slow because mca-dump costs a few hundred milliseconds; one apstats call
// per station is under a millisecond, so throughput can be resolved per poll instead of per write
// window.
func (c *Collector) runBytes(ctx context.Context) {
	targets := c.table.StaTargets()
	if len(targets) == 0 {
		// Nothing associated is a successful pass, not a failure: reporting it as failed would
		// read as a broken tier on an idle access point.
		c.bytes.succeeded(time.Now().UTC())
		return
	}

	// Deliberately not gated on the probe. The probe can only resolve this by asking about a real
	// station, so an access point with nobody on it at probe time reports the tier unavailable -
	// and probes are minutes apart, which would leave a client that just joined without throughput
	// for the rest of the interval. Having targets is the same evidence the probe would use, so
	// the tier settles itself on the first pass that gets an answer.
	now := time.Now().UTC()
	readings := collectStaBytes(ctx, targets)
	if len(readings) == 0 {
		c.bytes.setAvailable(false)
		c.bytes.failed(nil)
		return
	}
	c.bytes.setAvailable(true)
	c.table.ApplyBytes(readings, now)
	c.bytes.succeeded(now)
}

func (c *Collector) runSlow(ctx context.Context) {
	if !c.slow.info().Available {
		return
	}
	now := time.Now().UTC()
	snap, err := collectSlow(ctx, now)
	if err != nil {
		c.slow.failed(err)
		slog.Warn("slow tier collection failed", "error", err)
		return
	}
	c.table.ApplySlow(snap, now)
	c.slow.succeeded(now)

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
