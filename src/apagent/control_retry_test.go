//go:build !windows

package main

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

const retryMac = "02:00:00:00:00:20"

// fakeUbus puts a ubus on PATH that answers "Not found" for a BTM on staleVap, reports the client
// on holdingVap through get_clients, and records every call.
func fakeUbus(t *testing.T, staleVap, holdingVap string) func() []string {
	t.Helper()
	dir := t.TempDir()
	log := filepath.Join(dir, "calls")
	script := fmt.Sprintf(`#!/bin/sh
echo "$2 $3" >> %q
case "$2 $3" in
  "hostapd.%s wnm_disassoc_imminent") echo "Command failed: ubus call $2 $3 (Not found)" >&2; exit 4 ;;
  "hostapd.%s get_clients") echo '{"clients":{"%s":{"auth":true}}}' ;;
  *get_clients) echo '{"clients":{}}' ;;
esac
`, log, staleVap, holdingVap, retryMac)
	if err := os.WriteFile(filepath.Join(dir, "ubus"), []byte(script), 0700); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", dir+string(os.PathListSeparator)+os.Getenv("PATH"))
	return func() []string {
		b, err := os.ReadFile(log)
		if err != nil {
			return nil
		}
		return strings.Split(strings.TrimSpace(string(b)), "\n")
	}
}

func retryTable(vap string) *Table {
	now := time.Now()
	table := NewTable(64, time.Minute)
	table.ApplySlow(McaSnapshot{
		Vaps: []VapState{
			{Name: "wifi1ap5", Band: "5", Channel: 36, Essid: "TestNet", Bssid: "02:00:00:00:01:05", Up: true},
			{Name: "wifi2ap10", Band: "6", Channel: 37, Essid: "TestNet", Bssid: "02:00:00:00:01:10", Up: true},
		},
		Stations:    []StaSlow{{MAC: retryMac, Vap: vap, Authorized: true, SnapshotAt: now}},
		CollectedAt: now,
	}, now)
	return table
}

var retryReq = RoamRequest{Mac: retryMac, Candidates: []string{"0200000002010700000073240900"}}

func TestRoamOnAStaleVapRetriesOnTheVapHoldingTheClient(t *testing.T) {
	calls := fakeUbus(t, "wifi1ap5", "wifi2ap10")
	result, err := sendRoam(context.Background(), retryTable("wifi1ap5"), []string{"wifi1ap5", "wifi2ap10"}, retryReq)
	if err != nil {
		t.Fatalf("retry failed: %v (calls %v)", err, calls())
	}
	if result.Vap != "wifi2ap10" {
		t.Fatalf("sent on %s, want wifi2ap10", result.Vap)
	}
	var btms []string
	for _, c := range calls() {
		if strings.HasSuffix(c, "wnm_disassoc_imminent") {
			btms = append(btms, c)
		}
	}
	if len(btms) != 2 || btms[1] != "hostapd.wifi2ap10 wnm_disassoc_imminent" {
		t.Fatalf("want one refused BTM then one on wifi2ap10, got %v", btms)
	}
}

func TestRoamForAClientNoVapHoldsIsNotFound(t *testing.T) {
	calls := fakeUbus(t, "wifi1ap5", "wifi0ap0")
	_, err := sendRoam(context.Background(), retryTable("wifi1ap5"), []string{"wifi1ap5", "wifi2ap10"}, retryReq)
	var he httpError
	if !errors.As(err, &he) || he.status != http.StatusNotFound {
		t.Fatalf("want 404, got %v (calls %v)", err, calls())
	}
	for _, c := range calls() {
		if c == "hostapd.wifi2ap10 wnm_disassoc_imminent" {
			t.Fatal("sent a BTM on a VAP that does not hold the client")
		}
	}
}
