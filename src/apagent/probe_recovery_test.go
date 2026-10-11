package main

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

func mcaProbeFixture(t *testing.T, failures int, hang bool) func() int {
	t.Helper()
	dir := t.TempDir()
	count := filepath.Join(dir, "count")
	script := fmt.Sprintf("#!/bin/sh\nn=0\ntest ! -f %q || n=$(cat %q)\nn=$((n+1))\necho $n > %q\n", count, count, count)
	if hang {
		script += "exec sleep 5\n"
	} else {
		script += fmt.Sprintf("if test $n -le %d; then echo '{\"version\":\"fixture\"}'; else echo '{\"version\":\"fixture\",\"radio_table\":[{\"name\":\"wifi1\",\"radio\":\"na\"}],\"vap_table\":[]}'; fi\n", failures)
	}
	if err := os.WriteFile(filepath.Join(dir, "mca-dump"), []byte(script), 0700); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", dir+string(os.PathListSeparator)+os.Getenv("PATH"))
	return func() int {
		b, err := os.ReadFile(count)
		if os.IsNotExist(err) {
			return 0
		}
		if err != nil {
			t.Fatal(err)
		}
		n, err := strconv.Atoi(strings.TrimSpace(string(b)))
		if err != nil {
			t.Fatal(err)
		}
		return n
	}
}

func previousMca(available bool) ProbeSet {
	return ProbeSet{Results: []ProbeResult{{Name: ProbeMcaDump, Available: available}}}
}

func TestMcaProbeTransientFailureDoesNotPauseWorkingCollector(t *testing.T) {
	calls := mcaProbeFixture(t, 1, false)
	_, result := probeMcaDumpAfter(context.Background(), time.Now().UTC(), previousMca(true))
	if !result.Available || calls() != 2 {
		t.Fatalf("working source must recover with one confirmation: %+v, calls=%d", result, calls())
	}
	// Exercise the real availability gate and collection, not just the probe's return value.
	cfg := &Config{HostapdDir: t.TempDir(), SyslogPath: filepath.Join(t.TempDir(), "missing"), BytesIntervalSeconds: 10}
	c := NewCollector(cfg, NewTable(64, time.Minute), NewEventRing(64))
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	c.Apply(ctx, ProbeSet{Results: []ProbeResult{result}})
	c.runBytes(ctx)
	if !c.bytes.info().Available || c.bytes.info().Runs != 1 || c.bytes.info().LastCollectedAt == nil || calls() != 3 {
		t.Fatalf("collector did not produce fresh bytes after the recovered probe: %+v", c.bytes.info())
	}
}

func TestMcaProbeRetryIsConditionalAndBounded(t *testing.T) {
	for _, tc := range []struct {
		name       string
		previous   ProbeSet
		failures   int
		wantCalls  int
		wantActive bool
	}{
		{"startup", ProbeSet{}, 1, 1, false},
		{"already unavailable", previousMca(false), 1, 1, false},
		{"persistent failure", previousMca(true), 10, 2, false},
		{"healthy", previousMca(true), 0, 1, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			calls := mcaProbeFixture(t, tc.failures, false)
			_, result := probeMcaDumpAfter(context.Background(), time.Now().UTC(), tc.previous)
			if calls() != tc.wantCalls || result.Available != tc.wantActive {
				t.Fatalf("calls=%d, available=%v, want %d/%v", calls(), result.Available, tc.wantCalls, tc.wantActive)
			}
		})
	}
}

func TestMcaProbeCancellationDuringRestDoesNotRetry(t *testing.T) {
	calls := mcaProbeFixture(t, 1, false)
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	_, result := probeMcaDumpAfter(ctx, time.Now().UTC(), previousMca(true))
	if result.Available || calls() != 1 {
		t.Fatalf("cancelled confirmation retried: %+v, calls=%d", result, calls())
	}
}

func TestMcaProbeHungUtilityRespectsParentDeadline(t *testing.T) {
	calls := mcaProbeFixture(t, 0, true)
	ctx, cancel := context.WithTimeout(context.Background(), 100*time.Millisecond)
	defer cancel()
	start := time.Now()
	_, result := probeMcaDumpAfter(ctx, start.UTC(), previousMca(true))
	if result.Available || calls() != 1 || time.Since(start) > time.Second {
		t.Fatalf("hung probe escaped deadline or retried: %+v, calls=%d", result, calls())
	}
}
