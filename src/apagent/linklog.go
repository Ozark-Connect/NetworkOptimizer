package main

import (
	"bufio"
	"context"
	"os"
	"strconv"
	"strings"
	"time"
)

const (
	// linkReplayWindow is how far back the start-up replay reaches: LAN Health's backfill window.
	linkReplayWindow = 14 * 24 * time.Hour
	// linkReplayMax bounds the replay so it can never crowd client events out of the ring.
	linkReplayMax = 200
	// syslogStampLayout is busybox syslogd's line prefix, e.g. "Sun Nov 22 21:58:51 2026".
	syslogStampLayout = "Mon Jan _2 15:04:05 2006"
)

// syslogWallClock reads a line's local wall-clock time as "2006-01-02 15:04:05", the form
// busybox date -d accepts. The AP logs in local time.
func syslogWallClock(line string) (string, bool) {
	if len(line) < len(syslogStampLayout) {
		return "", false
	}
	t, err := time.Parse(syslogStampLayout, line[:len(syslogStampLayout)])
	if err != nil {
		return "", false
	}
	return t.Format("2006-01-02 15:04:05"), true
}

// localToUTC converts AP-local wall-clock stamps to UTC with busybox date. The AP's zone is a POSIX
// rule in /etc/TZ, which Go cannot read (time.Local is UTC here), and date applies it, DST included.
// A stamp date cannot read is left out.
var localToUTC = func(ctx context.Context, stamps []string) map[string]time.Time {
	out := make(map[string]time.Time, len(stamps))
	if len(stamps) == 0 {
		return out
	}
	args := append([]string{"-c", `for s in "$@"; do date -d "$s" +%s 2>/dev/null || echo x; done`, "sh"}, stamps...)
	text, err := runCommand(ctx, 10*time.Second, "sh", args...)
	if err != nil {
		return out
	}
	lines := strings.Split(strings.TrimSpace(text), "\n")
	for i, s := range stamps {
		if i >= len(lines) {
			break
		}
		if secs, err := strconv.ParseInt(strings.TrimSpace(lines[i]), 10, 64); err == nil {
			out[s] = time.Unix(secs, 0).UTC()
		}
	}
	return out
}

// parseLanHealthLine is a link line or an uplink context line: the events LAN Health takes.
func parseLanHealthLine(line string, now time.Time) (Event, bool) {
	if ev, ok := parseLinkLine(line, now); ok {
		return ev, true
	}
	return parseUplinkContextLine(line, now)
}

// stampLineTime sets a LAN Health event's time from its own log line, so a line read live and the
// same line replayed after a restart carry the same instant (the server stores events by time).
func stampLineTime(ctx context.Context, ev *Event, line string) {
	stamp, ok := syslogWallClock(line)
	if !ok {
		return
	}
	if t, ok := localToUTC(ctx, []string{stamp})[stamp]; ok {
		ev.EventTime = &t
	}
}

// replayLinkLines returns the LAN Health events already in the log from the last 14 days, oldest
// first and at most linkReplayMax, each stamped with its line's time. Lines with no readable time
// are skipped: an event at the replay instant would be wrong.
func replayLinkLines(ctx context.Context, path string, now time.Time) []Event {
	f, err := os.Open(path)
	if err != nil {
		return nil
	}
	defer f.Close()

	type found struct {
		ev    Event
		stamp string
	}
	var hits []found
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 64*1024), 1024*1024)
	for sc.Scan() {
		line := sc.Text()
		ev, ok := parseLanHealthLine(line, now)
		if !ok {
			continue
		}
		stamp, ok := syslogWallClock(line)
		if !ok {
			continue
		}
		hits = append(hits, found{ev, stamp})
	}
	// The stamp pre-filter allows a day of zone slack; the exact cut is applied in UTC below.
	cutoff := now.Add(-linkReplayWindow - 24*time.Hour).Format("2006-01-02 15:04:05")
	var recent []found
	for _, h := range hits {
		if h.stamp >= cutoff {
			recent = append(recent, h)
		}
	}
	if len(recent) > linkReplayMax {
		recent = recent[len(recent)-linkReplayMax:]
	}
	stamps := make([]string, 0, len(recent))
	for _, h := range recent {
		stamps = append(stamps, h.stamp)
	}
	times := localToUTC(ctx, stamps)
	var out []Event
	for _, h := range recent {
		t, ok := times[h.stamp]
		if !ok || t.Before(now.Add(-linkReplayWindow)) {
			continue
		}
		ev := h.ev
		ev.EventTime = &t
		out = append(out, ev)
	}
	return out
}
