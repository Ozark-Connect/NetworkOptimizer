package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// Event types. The collector keys on these strings, so they are a contract.
const (
	EventAssoc         = "assoc"
	EventDisassoc      = "disassoc"
	EventRoamBroadcast = "roam_broadcast"
	EventRoamToPeer    = "roam_to_peer"
	EventListenerUp    = "listener_up"
	EventListenerDown  = "listener_down"
)

const (
	// hostapdKeepalive paces the PING that proves the control socket is still attached to a live
	// hostapd. A provision cycle can restart hostapd, which leaves the old socket silent rather
	// than closed, so silence has to be probed rather than trusted.
	hostapdKeepalive = 30 * time.Second
	// hostapdStale is how long a socket may go without any traffic before it is redialed.
	hostapdStale = 95 * time.Second

	hostapdBackoffMin = 1 * time.Second
	hostapdBackoffMax = 30 * time.Second

	// maxUnknownEventKinds bounds the diagnostic tally of unrecognized control-socket lines.
	maxUnknownEventKinds = 32
)

// Event is one membership fact. EventTime is the source's own timestamp where a source provides
// one; the hostapd control socket does not, so only CollectedAt is set for its events.
type Event struct {
	Seq         uint64     `json:"seq"`
	Type        string     `json:"type"`
	Vap         string     `json:"vap"`
	MAC         string     `json:"mac,omitempty"`
	PeerBssid   string     `json:"peer_bssid,omitempty"`
	Detail      string     `json:"detail,omitempty"`
	Sta         *StaEvent  `json:"sta,omitempty"`
	EventTime   *time.Time `json:"event_time,omitempty"`
	CollectedAt time.Time  `json:"collected_at"`
}

// EventRing is a bounded in-memory replay window. A collector that restarts asks for everything
// after the last sequence it stored; the reply says whether the window it wanted still existed.
type EventRing struct {
	mu       sync.Mutex
	buf      []Event
	next     int
	filled   bool
	seq      uint64
	dropped  uint64
	received uint64
}

func NewEventRing(size int) *EventRing {
	if size <= 0 {
		size = defaultEventBufferSize
	}
	return &EventRing{buf: make([]Event, size)}
}

// Add stamps the event with the next sequence number and stores it, overwriting the oldest.
func (r *EventRing) Add(e Event) Event {
	r.mu.Lock()
	defer r.mu.Unlock()

	r.seq++
	r.received++
	e.Seq = r.seq
	if r.filled {
		r.dropped++
	}
	r.buf[r.next] = e
	r.next = (r.next + 1) % len(r.buf)
	if r.next == 0 {
		r.filled = true
	}
	return e
}

// snapshot returns the retained events in sequence order.
func (r *EventRing) snapshot() []Event {
	if !r.filled {
		return append([]Event(nil), r.buf[:r.next]...)
	}
	out := make([]Event, 0, len(r.buf))
	out = append(out, r.buf[r.next:]...)
	return append(out, r.buf[:r.next]...)
}

// Since returns every retained event after seq. Truncated reports that the window the caller asked
// for had already been overwritten, which is how a collector learns it lost events rather than
// silently believing nothing happened.
func (r *EventRing) Since(seq uint64) (events []Event, truncated bool) {
	r.mu.Lock()
	defer r.mu.Unlock()

	all := r.snapshot()
	if len(all) > 0 && seq > 0 && seq < all[0].Seq-1 {
		truncated = true
	}
	for _, e := range all {
		if e.Seq > seq {
			events = append(events, e)
		}
	}
	return events, truncated
}

// SinceTime returns every retained event collected at or after t.
func (r *EventRing) SinceTime(t time.Time) (events []Event, truncated bool) {
	r.mu.Lock()
	defer r.mu.Unlock()

	all := r.snapshot()
	if len(all) > 0 && t.Before(all[0].CollectedAt) && r.filled {
		truncated = true
	}
	for _, e := range all {
		if !e.CollectedAt.Before(t) {
			events = append(events, e)
		}
	}
	return events, truncated
}

// RingStats describes the replay window for /health and the /events envelope.
type RingStats struct {
	Capacity  int    `json:"capacity"`
	Retained  int    `json:"retained"`
	OldestSeq uint64 `json:"oldest_seq"`
	NewestSeq uint64 `json:"newest_seq"`
	Received  uint64 `json:"received"`
	Dropped   uint64 `json:"dropped"`
}

func (r *EventRing) Stats() RingStats {
	r.mu.Lock()
	defer r.mu.Unlock()

	all := r.snapshot()
	s := RingStats{Capacity: len(r.buf), Retained: len(all), Received: r.received, Dropped: r.dropped}
	if len(all) > 0 {
		s.OldestSeq = all[0].Seq
		s.NewestSeq = all[len(all)-1].Seq
	}
	return s
}

// parseHostapdEvent turns one control-socket line into an event. It returns false for control
// replies and for lines this agent does not model, which are counted rather than stored.
func parseHostapdEvent(vap, line string, now time.Time) (Event, bool) {
	msg := stripPriority(line)
	if msg == "" {
		return Event{}, false
	}

	e := Event{Vap: vap, CollectedAt: now}
	fields := strings.Fields(msg)

	switch {
	case strings.HasPrefix(msg, "AP-STA-CONNECTED"):
		e.Type = EventAssoc
	case strings.HasPrefix(msg, "AP-STA-DISCONNECTED"):
		e.Type = EventDisassoc
	case strings.Contains(msg, "UBNT_ROAM received"):
		// Cross-AP gossip: this AP is told a client moved to a peer, including clients it never held.
		e.Type = EventRoamToPeer
		e.MAC = firstMACIn(fields)
		e.PeerBssid = lastMACIn(fields)
		e.Detail = msg
		if e.MAC != "" && e.MAC == e.PeerBssid {
			e.PeerBssid = ""
		}
		return e, true
	case strings.Contains(msg, "UBNT_ROAM:"):
		e.Type = EventRoamBroadcast
		e.MAC = firstMACIn(fields)
		e.PeerBssid = valueAfter(fields, "associated_ap=")
		e.Detail = msg
		return e, true
	default:
		return Event{}, false
	}

	if len(fields) < 2 || !isMAC(fields[1]) {
		return Event{}, false
	}
	e.MAC = normalizeMAC(fields[1])
	if len(fields) > 2 {
		e.Detail = strings.Join(fields[2:], " ")
	}
	return e, true
}

// stripPriority removes the syslog-style priority prefix control-socket messages carry. It is not
// part of the event, and leaving it on would split one event kind into a tally per priority.
func stripPriority(line string) string {
	msg := strings.TrimSpace(line)
	if strings.HasPrefix(msg, "<") {
		if i := strings.IndexByte(msg, '>'); i > 0 && i <= 3 {
			msg = msg[i+1:]
		}
	}
	return strings.TrimSpace(msg)
}

func firstMACIn(fields []string) string {
	for _, f := range fields {
		f = strings.TrimSuffix(strings.TrimPrefix(f, "STA="), ",")
		if isMAC(f) {
			return normalizeMAC(f)
		}
	}
	return ""
}

func lastMACIn(fields []string) string {
	found := ""
	for _, f := range fields {
		f = strings.TrimSuffix(f, ",")
		if isMAC(f) {
			found = normalizeMAC(f)
		}
	}
	return found
}

func valueAfter(fields []string, prefix string) string {
	for _, f := range fields {
		if strings.HasPrefix(f, prefix) {
			v := strings.TrimSuffix(strings.TrimPrefix(f, prefix), ",")
			if isMAC(v) {
				return normalizeMAC(v)
			}
			return v
		}
	}
	return ""
}

// isControlReply reports whether a line is an answer to something this agent sent, rather than a
// pushed event.
func isControlReply(line string) bool {
	switch strings.TrimSpace(line) {
	case "OK", "PONG", "FAIL", "UNKNOWN COMMAND":
		return true
	}
	return false
}

// EventSource keeps one attached control socket per VAP and pushes what they report into the ring.
// It never exits on failure: a provision cycle restarting hostapd must cost a reconnect, not the
// agent.
type EventSource struct {
	dir     string
	ring    *EventRing
	observe func(Event)

	mu        sync.Mutex
	listeners map[string]context.CancelFunc
	attached  map[string]bool
	unknown   map[string]uint64

	reconnects atomic.Uint64
	ignored    atomic.Uint64
	wg         sync.WaitGroup
}

func NewEventSource(dir string, ring *EventRing, observe func(Event)) *EventSource {
	return &EventSource{
		dir:       dir,
		ring:      ring,
		observe:   observe,
		listeners: map[string]context.CancelFunc{},
		attached:  map[string]bool{},
		unknown:   map[string]uint64{},
	}
}

// Reconcile starts listeners for VAPs that appeared and stops the ones that went away. VAP names
// change across a provision cycle, so the set is re-read rather than fixed at startup.
func (s *EventSource) Reconcile(ctx context.Context, vaps []string) {
	want := make(map[string]bool, len(vaps))
	for _, v := range vaps {
		want[v] = true
	}

	s.mu.Lock()
	for vap, cancel := range s.listeners {
		if !want[vap] {
			cancel()
			delete(s.listeners, vap)
			delete(s.attached, vap)
		}
	}
	var starting []string
	for vap := range want {
		if _, running := s.listeners[vap]; running {
			continue
		}
		vapCtx, cancel := context.WithCancel(ctx)
		s.listeners[vap] = cancel
		starting = append(starting, vap)
		s.wg.Add(1)
		go func(name string) {
			defer s.wg.Done()
			s.listen(vapCtx, name)
		}(vap)
	}
	s.mu.Unlock()

	for _, vap := range starting {
		slog.Info("attaching to hostapd control socket", "vap", vap)
	}
}

// Wait blocks until every listener has stopped, so shutdown does not leave sockets behind.
func (s *EventSource) Wait() { s.wg.Wait() }

// listenerCount is how many VAPs currently have a listener goroutine, attached or reconnecting.
func (s *EventSource) listenerCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.listeners)
}

// Attached reports which VAPs currently hold a live control socket.
func (s *EventSource) Attached() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.attached))
	for vap, ok := range s.attached {
		if ok {
			out = append(out, vap)
		}
	}
	return out
}

// UnknownKinds is the diagnostic tally of control-socket lines this agent does not model, so an
// event shape that appears on new firmware is visible rather than silently discarded.
func (s *EventSource) UnknownKinds() map[string]uint64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make(map[string]uint64, len(s.unknown))
	for k, v := range s.unknown {
		out[k] = v
	}
	return out
}

func (s *EventSource) Reconnects() uint64 { return s.reconnects.Load() }
func (s *EventSource) Ignored() uint64    { return s.ignored.Load() }

func (s *EventSource) setAttached(vap string, up bool) {
	s.mu.Lock()
	changed := s.attached[vap] != up
	s.attached[vap] = up
	s.mu.Unlock()

	if !changed {
		return
	}
	kind := EventListenerDown
	if up {
		kind = EventListenerUp
	}
	s.emit(Event{Type: kind, Vap: vap, CollectedAt: time.Now().UTC()})
}

func (s *EventSource) emit(e Event) {
	stored := s.ring.Add(e)
	if s.observe != nil {
		s.observe(stored)
	}
}

func (s *EventSource) noteUnknown(line string) {
	s.ignored.Add(1)
	kind := stripPriority(line)
	if fields := strings.Fields(kind); len(fields) > 0 {
		kind = fields[0]
	}
	if len(kind) > 48 {
		kind = kind[:48]
	}
	s.mu.Lock()
	if _, seen := s.unknown[kind]; seen || len(s.unknown) < maxUnknownEventKinds {
		s.unknown[kind]++
	}
	s.mu.Unlock()
}

// listen holds one VAP's control socket open for the life of the agent, reconnecting with backoff.
func (s *EventSource) listen(ctx context.Context, vap string) {
	backoff := hostapdBackoffMin
	for ctx.Err() == nil {
		err := s.attachAndRead(ctx, vap)
		s.setAttached(vap, false)
		if ctx.Err() != nil {
			return
		}
		s.reconnects.Add(1)
		slog.Warn("hostapd control socket dropped, reconnecting",
			"vap", vap, "error", err, "retry_in", backoff.String())

		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		if backoff *= 2; backoff > hostapdBackoffMax {
			backoff = hostapdBackoffMax
		}
	}
}

// attachAndRead attaches to one control socket and reads until it fails. hostapd's control
// interface is a unix datagram socket, so the client binds a local socket of its own.
func (s *EventSource) attachAndRead(ctx context.Context, vap string) error {
	remote := filepath.Join(s.dir, vap)
	local := filepath.Join(os.TempDir(), fmt.Sprintf("apagent-ev-%d-%s", os.Getpid(), vap))

	_ = os.Remove(local)
	conn, err := net.DialUnix("unixgram",
		&net.UnixAddr{Name: local, Net: "unixgram"},
		&net.UnixAddr{Name: remote, Net: "unixgram"})
	if err != nil {
		return fmt.Errorf("dial %s: %w", remote, err)
	}
	// Close on cancellation so a blocked Read returns at once. Without this, shutdown waits out
	// the keepalive deadline on every idle VAP, and the server stops and starts these constantly.
	readDone := make(chan struct{})
	go func() {
		select {
		case <-ctx.Done():
			conn.SetReadDeadline(time.Now())
		case <-readDone:
		}
	}()

	defer func() {
		close(readDone)
		// Best effort: a hostapd that has already gone will not answer, and that is the common exit.
		_ = conn.SetWriteDeadline(time.Now().Add(time.Second))
		_, _ = conn.Write([]byte("DETACH"))
		conn.Close()
		os.Remove(local)
	}()

	if err := conn.SetWriteDeadline(time.Now().Add(2 * time.Second)); err != nil {
		return err
	}
	if _, err := conn.Write([]byte("ATTACH")); err != nil {
		return fmt.Errorf("write ATTACH to %s: %w", remote, err)
	}

	buf := make([]byte, 4096)
	lastTraffic := time.Now()
	attached := false

	for ctx.Err() == nil {
		if err := conn.SetReadDeadline(time.Now().Add(hostapdKeepalive)); err != nil {
			return err
		}
		n, err := conn.Read(buf)
		switch {
		case err == nil:
			lastTraffic = time.Now()
			line := string(buf[:n])
			if isControlReply(line) {
				if !attached {
					attached = true
					s.setAttached(vap, true)
				}
				continue
			}
			if e, ok := parseHostapdEvent(vap, line, time.Now().UTC()); ok {
				s.emit(e)
			} else {
				s.noteUnknown(line)
			}

		case isTimeout(err):
			if time.Since(lastTraffic) > hostapdStale {
				return fmt.Errorf("no traffic from %s for %s", remote, hostapdStale)
			}
			// A silent socket is the normal case on a quiet AP, so liveness is probed rather than
			// assumed: a restarted hostapd leaves the old socket silent, not closed.
			if err := conn.SetWriteDeadline(time.Now().Add(2 * time.Second)); err != nil {
				return err
			}
			if _, err := conn.Write([]byte("PING")); err != nil {
				return fmt.Errorf("ping %s: %w", remote, err)
			}

		default:
			return fmt.Errorf("read %s: %w", remote, err)
		}
	}
	return ctx.Err()
}

func isTimeout(err error) bool {
	var netErr net.Error
	return errors.As(err, &netErr) && netErr.Timeout()
}
