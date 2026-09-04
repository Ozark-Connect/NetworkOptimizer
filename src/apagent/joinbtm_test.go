package main

import (
	"testing"
	"time"
)

func TestParseHostapdBtmResponse(t *testing.T) {
	now := time.Now().UTC()

	e, ok := parseHostapdEvent("wifi1ap5",
		"<3>BSS-TM-RESP aa:bb:cc:dd:ee:ff status_code=6 bss_termination_delay=0 target_bssid=00:11:22:33:44:55", now)
	if !ok || e.Type != EventBtmResponse || e.MAC != "aa:bb:cc:dd:ee:ff" || e.Detail != "6" {
		t.Errorf("BTM response not parsed: %+v ok=%v", e, ok)
	}
}

func TestJoinRssiAndBtmFollowTheAssociation(t *testing.T) {
	table := NewTable(64, time.Minute)
	now := time.Now().UTC()
	rssi := -71

	table.ApplyEvent(Event{Seq: 1, Type: EventAssoc, Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:01", CollectedAt: now})
	table.ApplyEvent(Event{Seq: 2, Type: "sta_success", Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:01", Sta: &StaEvent{AuthRssi: &rssi}, CollectedAt: now})
	table.ApplyEvent(Event{Seq: 3, Type: EventBtmResponse, Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:01", Detail: "6", CollectedAt: now})
	table.ApplyEvent(Event{Seq: 4, Type: EventBtmResponse, Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:01", Detail: "0", CollectedAt: now})

	link := table.Clients(now)[0].Links[0]
	if link.JoinRssi == nil || *link.JoinRssi != -71 {
		t.Errorf("join_rssi = %v, want -71", link.JoinRssi)
	}
	if link.BtmRequests != 2 || link.BtmAccepted != 1 {
		t.Errorf("btm = %d/%d, want 2/1", link.BtmRequests, link.BtmAccepted)
	}

	// A new association learns afresh.
	table.ApplyEvent(Event{Seq: 5, Type: EventAssoc, Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:01", CollectedAt: now.Add(time.Minute)})
	link = table.Clients(now.Add(time.Minute))[0].Links[0]
	if link.JoinRssi != nil || link.BtmRequests != 0 || link.BtmAccepted != 0 {
		t.Errorf("a new assoc must reset: %+v", link)
	}
}

func TestJoinRssiArrivingBeforeTheAssocWaitsForIt(t *testing.T) {
	table := NewTable(64, time.Minute)
	now := time.Now().UTC()
	rssi := -80

	table.ApplyEvent(Event{Seq: 1, Type: "sta_association", Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:02", Sta: &StaEvent{AuthRssi: &rssi}, CollectedAt: now})
	if len(table.Clients(now)) != 0 {
		t.Fatal("a stahtd record alone must not create a member")
	}
	table.ApplyEvent(Event{Seq: 2, Type: EventAssoc, Vap: "wifi1ap5", MAC: "aa:bb:cc:dd:ee:02", CollectedAt: now.Add(time.Second)})

	link := table.Clients(now.Add(time.Second))[0].Links[0]
	if link.JoinRssi == nil || *link.JoinRssi != -80 {
		t.Errorf("join_rssi = %v, want -80 (adopted from the pending record)", link.JoinRssi)
	}
}
