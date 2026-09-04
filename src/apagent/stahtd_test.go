package main

import (
	"testing"
	"time"
)

// Lines captured verbatim from a U7-Pro-XGS-B on 8.7.11, with MACs anonymized. Every quirk these
// exercise is real: values arrive as strings, keys carry a space after the colon, and a deny line
// carries trailing text after the closing brace.
const (
	lineAssociation = `Mon Aug 24 15:54:38 2026 user.info stahtd[5335]: [STA-TRACKER].stahtd_dump_event(): {"op":"event","message_type":"STA_ASSOC_TRACKER","event_type":"association","mac":"aa:bb:cc:dd:ee:ff","vap":"wifi1ap5","assoc_status":"0","event_id": "1","assoc_delta": "10000","auth_utc": "1787604878.203416","auth_algo": "open","auth_rssi": "-52"}`
	lineSuccess     = `Mon Aug 24 15:54:38 2026 user.info stahtd[5335]: [STA-TRACKER].stahtd_dump_event(): {"op":"event","message_type":"STA_ASSOC_TRACKER","event_type":"success","mac":"aa:bb:cc:dd:ee:ff","vap":"wifi1ap5","assoc_status":"0","traffic_delta": "190000","dns_responses": "1","dns_timeouts": "0","ip_delta": "1229992976","ip_assign_type": "N/A","wpa_auth_delta": "40000","assoc_delta": "10000","auth_delta": "0","event_id": "2","auth_ts": "883842.889210","auth_utc": "1787604878.203416","dns_resp_seen": "yes","auth_rssi": "-52","auth_algo": "open"}`
	lineDeny        = `Mon Aug 24 16:53:38 2026 user.info stahtd[5335]: [STA-TRACKER].stahtd_dump_event(): {"op":"event","message_type":"STA_ASSOC_TRACKER","event_type":"deny","mac":"00:11:22:33:44:55","vap":"wifi0ap3","assoc_status":"4096","auth_failures": "10","event_id": "1","auth_ts": "887367.523120","auth_utc": "1787608402.799004","sta_dc_reason": "sta left","auth_rssi": "-72","auth_algo": "open"} - lock2ap, flags: 0x1000`
	lineRoam        = `Mon Aug 24 10:30:30 2026 user.info stahtd[5335]: [STA-TRACKER].stahtd_dump_event(): {"op":"event","message_type":"STA_ASSOC_TRACKER","event_type":"sta_roam","mac":"aa:bb:cc:dd:ee:ff","vap":"wifi1ap5","assoc_status":"0","event_id": "4","avg_rssi": "-46"}`
	lineUbntRoam    = `Mon Aug 24 18:30:32 2026 user.info hostapd[5343]: wifi1ap6: STA aa:bb:cc:dd:ee:ff WPA: UBNT_ROAM received: STA roamed to peer AP 00:11:22:33:44:55`
)

func TestParseStahtdSuccessCarriesPhaseTiming(t *testing.T) {
	ev, ok := parseStahtdLine(lineSuccess, time.Now())
	if !ok {
		t.Fatal("the measured success line must parse")
	}
	if ev.Type != "sta_success" || ev.MAC != "aa:bb:cc:dd:ee:ff" || ev.Vap != "wifi1ap5" {
		t.Fatalf("unexpected envelope: %+v", ev)
	}
	if ev.Sta == nil {
		t.Fatal("success must carry a StaEvent")
	}
	if ev.Sta.AuthRssi == nil || *ev.Sta.AuthRssi != -52 {
		t.Errorf("auth_rssi = %v, want -52", ev.Sta.AuthRssi)
	}
	// The three deltas are the reason this source exists; the console reports none of them.
	if ev.Sta.AuthDeltaUs == nil || *ev.Sta.AuthDeltaUs != 0 {
		t.Errorf("auth_delta = %v, want 0", ev.Sta.AuthDeltaUs)
	}
	if ev.Sta.AssocDeltaUs == nil || *ev.Sta.AssocDeltaUs != 10000 {
		t.Errorf("assoc_delta = %v, want 10000", ev.Sta.AssocDeltaUs)
	}
	if ev.Sta.WpaAuthDeltaUs == nil || *ev.Sta.WpaAuthDeltaUs != 40000 {
		t.Errorf("wpa_auth_delta = %v, want 40000", ev.Sta.WpaAuthDeltaUs)
	}
	if ev.Sta.AuthAlgo != "open" {
		t.Errorf("auth_algo = %q, want open", ev.Sta.AuthAlgo)
	}
	// auth_utc is stahtd's own clock and must win over our read time.
	if ev.EventTime == nil {
		t.Fatal("auth_utc must become EventTime")
	}
	if got := ev.EventTime.Unix(); got != 1787604878 {
		t.Errorf("EventTime = %d, want 1787604878", got)
	}
}

func TestParseStahtdDenyWithTrailingText(t *testing.T) {
	// The deny line carries " - lock2ap, flags: 0x1000" after the closing brace. Unmarshalling the
	// rest of the line fails here, which is why the object is brace-matched out first.
	ev, ok := parseStahtdLine(lineDeny, time.Now())
	if !ok {
		t.Fatal("a deny line with trailing text must still parse")
	}
	if ev.Type != "sta_deny" {
		t.Errorf("type = %q, want sta_deny", ev.Type)
	}
	if ev.Sta.AssocStatus != "4096" {
		t.Errorf("assoc_status = %q, want 4096", ev.Sta.AssocStatus)
	}
	if ev.Sta.AuthFailures == nil || *ev.Sta.AuthFailures != 10 {
		t.Errorf("auth_failures = %v, want 10", ev.Sta.AuthFailures)
	}
	if ev.Sta.DcReason != "sta left" {
		t.Errorf("sta_dc_reason = %q, want 'sta left'", ev.Sta.DcReason)
	}
}

func TestParseStahtdRoamUsesAvgRssi(t *testing.T) {
	ev, ok := parseStahtdLine(lineRoam, time.Now())
	if !ok {
		t.Fatal("sta_roam must parse")
	}
	// A roam line reports avg_rssi and no auth_rssi; conflating them would invent a join RSSI.
	if ev.Sta.AvgRssi == nil || *ev.Sta.AvgRssi != -46 {
		t.Errorf("avg_rssi = %v, want -46", ev.Sta.AvgRssi)
	}
	if ev.Sta.AuthRssi != nil {
		t.Errorf("auth_rssi must stay absent on a roam line, got %v", *ev.Sta.AuthRssi)
	}
	if ev.EventTime != nil {
		t.Error("no auth_utc on a roam line, so EventTime must stay absent")
	}
}

func TestParseStahtdAssociationHasNoWpaDelta(t *testing.T) {
	ev, ok := parseStahtdLine(lineAssociation, time.Now())
	if !ok {
		t.Fatal("association must parse")
	}
	// Absent is not zero: the association record simply has not reached WPA auth yet.
	if ev.Sta.WpaAuthDeltaUs != nil {
		t.Errorf("wpa_auth_delta must be absent, got %v", *ev.Sta.WpaAuthDeltaUs)
	}
	if ev.Sta.AuthDeltaUs != nil {
		t.Errorf("auth_delta must be absent, got %v", *ev.Sta.AuthDeltaUs)
	}
}

func TestParseUbntRoamPeerGossip(t *testing.T) {
	ev, ok := parseUbntRoamLine(lineUbntRoam, time.Now())
	if !ok {
		t.Fatal("the measured UBNT_ROAM line must parse")
	}
	if ev.Type != "roam_to_peer" {
		t.Errorf("type = %q, want roam_to_peer", ev.Type)
	}
	if ev.MAC != "aa:bb:cc:dd:ee:ff" {
		t.Errorf("client = %q", ev.MAC)
	}
	if ev.PeerBssid != "00:11:22:33:44:55" {
		t.Errorf("peer = %q", ev.PeerBssid)
	}
	if ev.Vap != "wifi1ap6" {
		t.Errorf("vap = %q, want wifi1ap6", ev.Vap)
	}
}

func TestParseRejectsUnrelatedLines(t *testing.T) {
	for _, line := range []string{
		"",
		"Mon Aug 24 10:30:30 2026 user.info kernel: something else entirely",
		`{"message_type":"OTHER","mac":"aa:bb:cc:dd:ee:ff"}`,
		"Mon Aug 24 10:30:30 2026 stahtd[1]: STA_ASSOC_TRACKER but no json",
	} {
		if _, ok := parseStahtdLine(line, time.Now()); ok {
			t.Errorf("must not parse as stahtd: %q", line)
		}
		if _, ok := parseUbntRoamLine(line, time.Now()); ok {
			t.Errorf("must not parse as UBNT_ROAM: %q", line)
		}
	}
}

func TestExtractJSONObjectHandlesBracesInStrings(t *testing.T) {
	in := `prefix {"a":"has } brace","b":"x"} trailing`
	got, ok := extractJSONObject(in)
	if !ok {
		t.Fatal("must extract")
	}
	if want := `{"a":"has } brace","b":"x"}`; got != want {
		t.Errorf("got %q, want %q", got, want)
	}
}
