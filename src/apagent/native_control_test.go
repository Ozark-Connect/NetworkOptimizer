package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

const nativeTestMac = "02:00:00:00:00:10"
const nativeTestElement = "02000000020107000000732409"
const nativeTestConfig = "ssid=TestNet\nbssid=02:00:00:00:01:01\nwpa=2\nkey_mgmt=WPA-PSK\nrsn_pairwise_cipher=CCMP\n"
const nativeTestStation = nativeTestMac + "\nflags=[AUTH][ASSOC][AUTHORIZED]\next_capab=000008\n"

// Real Unix datagram exchanges exercise reply routing, deadlines and uncertain writes. No
// executable, AP model or production socket is involved in these fixtures.
type nativeFixture struct {
	dir   string
	mu    sync.Mutex
	calls []string
	peers []string
}

func newNativeFixture(t *testing.T, reply func(vap, command string) string) *nativeFixture {
	t.Helper()
	dir, err := os.MkdirTemp("", "nr-") // Unix socket paths have a 108-byte limit.
	if err != nil {
		t.Fatal(err)
	}
	f := &nativeFixture{dir: dir}
	t.Cleanup(func() { _ = os.RemoveAll(dir) })
	for _, vap := range []string{"wifi1ap0", "wifi0ap0"} {
		conn, err := net.ListenUnixgram("unixgram", &net.UnixAddr{Name: filepath.Join(dir, vap), Net: "unixgram"})
		if err != nil {
			t.Fatal(err)
		}
		done := make(chan struct{})
		t.Cleanup(func() { _ = conn.Close(); <-done })
		go func() {
			defer close(done)
			buf := make([]byte, 8192)
			for {
				n, peer, err := conn.ReadFromUnix(buf)
				if err != nil {
					return
				}
				command := string(buf[:n])
				f.mu.Lock()
				f.calls = append(f.calls, vap+":"+command)
				f.peers = append(f.peers, peer.Name)
				f.mu.Unlock()
				response := reply(vap, command)
				if response != "" {
					_, _ = conn.WriteToUnix([]byte(response+"\n"), peer)
				}
			}
		}()
	}
	return f
}

func nativeDefaultReply(vap, command string) string {
	switch {
	case command == "GET_CONFIG":
		return nativeTestConfig
	case command == "STATUS":
		return "state=ENABLED\nfreq=5180"
	case command == "BSS_TM_REQ ":
		return "FAIL"
	case command == "STA "+nativeTestMac:
		if vap == "wifi1ap0" {
			return nativeTestStation
		}
		return "FAIL"
	case strings.HasPrefix(command, "BSS_TM_REQ "):
		return "OK"
	default:
		return "UNKNOWN COMMAND"
	}
}

func (f *nativeFixture) sent() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	var sent []string
	for _, call := range f.calls {
		if strings.Contains(call, ":BSS_TM_REQ "+nativeTestMac) {
			sent = append(sent, call)
		}
	}
	return sent
}

func nativeState(f *nativeFixture) *State {
	now := time.Now()
	s := NewState(now, PlatformInfo{})
	s.SetProbes(ProbeSet{HostapdDir: f.dir, ProbedAt: now, NativeVoluntaryVaps: []string{"wifi1ap0"}})
	s.table.ApplySlow(McaSnapshot{
		Vaps: []VapState{
			{Name: "wifi1ap0", Band: "5", Channel: 36, Essid: "TestNet", Bssid: "02:00:00:00:01:01", Up: true},
			{Name: "wifi0ap0", Band: "2.4", Channel: 1, Essid: "TestNet", Bssid: "02:00:00:00:01:02", Up: true},
		},
		Stations:    []StaSlow{{MAC: nativeTestMac, Vap: "wifi1ap0", Authorized: true, SnapshotAt: now}},
		CollectedAt: now,
	}, now)
	return s
}

func nativeBody() string {
	return `{"candidates":[{"element":"` + nativeTestElement + `","ssid":"TestNet","security":{"wpa":"2","key_mgmt":"WPA-PSK","pairwise":"CCMP"}}]}`
}

func nativeRequest(s *State, body string) *httptest.ResponseRecorder {
	path := "/clients/" + nativeTestMac + "/voluntary-bss-transitions"
	r := httptest.NewRequest(http.MethodPost, path, strings.NewReader(body))
	r.Header.Set("Authorization", signedHeader(testSecret, "POST", path, "fixture-nonce", []byte(body), time.Now()))
	w := httptest.NewRecorder()
	newServer(s, testSecret).Handler.ServeHTTP(w, r)
	return w
}

func TestNativeVoluntaryRequestUsesOneNonEvictingFrame(t *testing.T) {
	f := newNativeFixture(t, nativeDefaultReply)
	s := nativeState(f)
	w := nativeRequest(s, nativeBody())
	if w.Code != http.StatusOK {
		t.Fatalf("status %d: %s", w.Code, w.Body)
	}
	want := "wifi1ap0:BSS_TM_REQ " + nativeTestMac + " disassoc_imminent=0 disassoc_timer=0 valid_int=255 pref=1 abridged=0 neighbor=02:00:00:00:02:01,7,115,36,9"
	if sent := f.sent(); len(sent) != 1 || sent[0] != want {
		t.Fatalf("sent %v, want %q", sent, want)
	}
	var result RoamResult
	if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil {
		t.Fatal(err)
	}
	if result.Mac != nativeTestMac || result.Vap != "wifi1ap0" || result.Candidates != 1 {
		t.Fatalf("bad acknowledgment: %+v", result)
	}
}

func TestNativeRouteRequiresAuthenticationAndPost(t *testing.T) {
	f := newNativeFixture(t, nativeDefaultReply)
	s := nativeState(f)
	path := "/clients/" + nativeTestMac + "/voluntary-bss-transitions"
	for _, method := range []string{"POST", "GET"} {
		r := httptest.NewRequest(method, path, strings.NewReader(nativeBody()))
		if method == "GET" {
			r.Header.Set("Authorization", signedHeader(testSecret, method, path, "get-nonce", []byte(nativeBody()), time.Now()))
		}
		w := httptest.NewRecorder()
		newServer(s, testSecret).Handler.ServeHTTP(w, r)
		want := http.StatusUnauthorized
		if method == "GET" {
			want = http.StatusMethodNotAllowed
		}
		if w.Code != want {
			t.Fatalf("%s: got %d, want %d", method, w.Code, want)
		}
	}
	if len(f.sent()) != 0 {
		t.Fatal("unauthorized or GET request sent a frame")
	}
}

func TestNativeRejectsIneligibleRequestsWithoutSending(t *testing.T) {
	cases := []struct {
		name   string
		change func(*State)
		body   string
	}{
		{"unknown capability", func(s *State) { s.probes.NativeVoluntaryVaps = nil }, nativeBody()},
		{"stale capability", func(s *State) { s.probes.ProbedAt = time.Now().Add(-11 * time.Minute) }, nativeBody()},
		{"future probe", func(s *State) { s.probes.ProbedAt = time.Now().Add(time.Hour) }, nativeBody()},
		{"MLO", func(s *State) {
			s.table.ApplySlow(McaSnapshot{Vaps: s.table.Vaps(), Stations: []StaSlow{{MAC: nativeTestMac, Vap: "wifi1ap0", Authorized: true, IsMlo: true}}}, time.Now())
		}, nativeBody()},
		{"unknown security", nil, strings.Replace(nativeBody(), `"security":{"wpa":"2","key_mgmt":"WPA-PSK","pairwise":"CCMP"}`, `"security":null`, 1)},
		{"different security", nil, strings.Replace(nativeBody(), "WPA-PSK", "SAE", 1)},
		{"different SSID", nil, strings.Replace(nativeBody(), "TestNet", "OtherNet", 1)},
		{"malformed report", nil, strings.Replace(nativeBody(), nativeTestElement, nativeTestElement+"0305ff", 1)},
		{"self candidate", nil, strings.Replace(nativeBody(), nativeTestElement, "02000000010107000000732409", 1)},
		{"2 GHz candidate", nil, strings.Replace(nativeBody(), nativeTestElement, "02000000020107000000510109", 1)},
		{"no candidates", nil, `{"candidates":[]}`},
		{"legacy timer even zero", nil, strings.TrimSuffix(nativeBody(), "}") + `,"duration_tbtt":0}`},
		{"ban field", nil, strings.TrimSuffix(nativeBody(), "}") + `,"ban_ms":20}`},
		{"trailing body", nil, nativeBody() + ` {}`},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			f := newNativeFixture(t, nativeDefaultReply)
			s := nativeState(f)
			if tc.change != nil {
				tc.change(s)
			}
			w := nativeRequest(s, tc.body)
			if w.Code < 400 || len(f.sent()) != 0 {
				t.Fatalf("status %d, sent %v: %s", w.Code, f.sent(), w.Body)
			}
		})
	}
}

func TestNativeAssociationRacesAndUnknownReadsNeverSend(t *testing.T) {
	for _, mode := range []string{"left", "final departure", "unknown other VAP", "multiple association", "not authorized", "no BTM", "live MLO", "band changed", "security changed"} {
		t.Run(mode, func(t *testing.T) {
			stationReads, configReads := 0, 0
			f := newNativeFixture(t, func(vap, command string) string {
				if vap == "wifi1ap0" && command == "GET_CONFIG" {
					configReads++
					if mode == "security changed" && configReads == 2 {
						return strings.Replace(nativeTestConfig, "WPA-PSK", "SAE", 1)
					}
				}
				if command == "STATUS" && mode == "band changed" {
					return "freq=2412"
				}
				if command == "STA "+nativeTestMac {
					if vap == "wifi0ap0" {
						if mode == "unknown other VAP" {
							return "UNKNOWN COMMAND"
						}
						if mode == "multiple association" {
							return nativeTestStation
						}
					} else {
						stationReads++
						if mode == "left" || mode == "final departure" && stationReads == 2 {
							return "FAIL"
						}
						if mode == "not authorized" {
							return strings.Replace(nativeTestStation, "[AUTHORIZED]", "", 1)
						}
						if mode == "no BTM" {
							return strings.Replace(nativeTestStation, "000008", "000000", 1)
						}
						if mode == "live MLO" {
							return nativeTestStation + "mld_addr=02:00:00:00:00:11\n"
						}
					}
				}
				return nativeDefaultReply(vap, command)
			})
			w := nativeRequest(nativeState(f), nativeBody())
			if w.Code < 400 || len(f.sent()) != 0 {
				t.Fatalf("status %d, sent %v", w.Code, f.sent())
			}
		})
	}
}

func TestNativeCandidateBufferLimitRefusesBeforeSending(t *testing.T) {
	f := newNativeFixture(t, nativeDefaultReply)
	var candidates []NativeCandidate
	for i := 1; i <= 4; i++ {
		candidates = append(candidates, NativeCandidate{
			Element: fmt.Sprintf("0200000002%02x07000000732409ddf0", i) + strings.Repeat("00", 240),
			Ssid:    "TestNet", Security: &BssSecurity{"2", "WPA-PSK", "CCMP"},
		})
	}
	data, err := json.Marshal(NativeRoamRequest{Candidates: candidates})
	if err != nil {
		t.Fatal(err)
	}
	w := nativeRequest(nativeState(f), string(data))
	if w.Code != http.StatusBadRequest || len(f.sent()) != 0 {
		t.Fatalf("status %d, sent %v", w.Code, f.sent())
	}
}

func TestNativeCapabilityClearsOnTheNextProbe(t *testing.T) {
	f := newNativeFixture(t, nativeDefaultReply)
	s := nativeState(f)
	if len(s.Health().NativeVoluntaryVaps) != 1 || len(s.Capabilities().NativeVoluntaryVaps) != 1 {
		t.Fatal("capability missing")
	}
	s.SetProbes(ProbeSet{HostapdDir: f.dir, ProbedAt: time.Now()})
	if len(s.Health().NativeVoluntaryVaps) != 0 || len(s.Capabilities().NativeVoluntaryVaps) != 0 {
		t.Fatal("capability survived a failed or changed probe")
	}
	w := nativeRequest(s, nativeBody())
	if w.Code != http.StatusNotImplemented || len(f.sent()) != 0 {
		t.Fatalf("status %d, sent %v", w.Code, f.sent())
	}
}

func TestNativeLostAcknowledgmentNeverRetries(t *testing.T) {
	f := newNativeFixture(t, func(vap, command string) string {
		if strings.HasPrefix(command, "BSS_TM_REQ "+nativeTestMac) {
			return ""
		}
		return nativeDefaultReply(vap, command)
	})
	w := nativeRequest(nativeState(f), nativeBody())
	if w.Code != http.StatusInternalServerError || len(f.sent()) != 1 {
		t.Fatalf("status %d, sent %v", w.Code, f.sent())
	}
}

func TestNativeProbeRequiresActualCommandAndFiveGHz(t *testing.T) {
	for _, mode := range []string{"supported", "unknown", "two GHz", "unknown security"} {
		t.Run(mode, func(t *testing.T) {
			f := newNativeFixture(t, func(vap, command string) string {
				if command == "BSS_TM_REQ " && mode == "unknown" {
					return "UNKNOWN COMMAND"
				}
				if command == "STATUS" && mode == "two GHz" {
					return "freq=2412"
				}
				if command == "GET_CONFIG" && mode == "unknown security" {
					return "ssid=TestNet"
				}
				return nativeDefaultReply(vap, command)
			})
			supported := probeNativeVoluntary(context.Background(), f.dir, []string{"wifi1ap0"})
			if (len(supported) == 1) != (mode == "supported") {
				t.Fatalf("supported %v for %s", supported, mode)
			}
			if len(f.sent()) != 0 {
				t.Fatal("capability probe addressed a station")
			}
		})
	}
}

func TestNativeControlConcurrentRepliesAndBounds(t *testing.T) {
	f := newNativeFixture(t, func(vap, command string) string { return command })
	var wg sync.WaitGroup
	for i := range 20 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			command := fmt.Sprintf("request-%d", i)
			reply, err := nativeControl(context.Background(), f.dir, "wifi1ap0", command)
			if err != nil || reply != command {
				t.Errorf("got %q, %v; want %q", reply, err, command)
			}
		}()
	}
	wg.Wait()
	f.mu.Lock()
	peers := append([]string{}, f.peers...)
	f.mu.Unlock()
	seen := map[string]bool{}
	for _, peer := range peers {
		if seen[peer] {
			t.Fatalf("shared socket %s", peer)
		}
		seen[peer] = true
		if _, err := os.Stat(peer); !os.IsNotExist(err) {
			t.Fatalf("local socket not removed: %s", peer)
		}
	}
	for _, command := range []string{"STATUS\nDEAUTHENTICATE " + nativeTestMac, strings.Repeat("x", maxNativeCommand+1)} {
		if _, err := nativeControl(context.Background(), f.dir, "wifi1ap0", command); err == nil {
			t.Fatal("invalid command accepted")
		}
	}
	if _, err := nativeControl(context.Background(), f.dir, "../wifi1ap0", "STATUS"); err == nil {
		t.Fatal("invalid path accepted")
	}
	f2 := newNativeFixture(t, func(_, _ string) string { return strings.Repeat("x", maxNativeReply+1) })
	if _, err := nativeControl(context.Background(), f2.dir, "wifi1ap0", "STATUS"); err == nil {
		t.Fatal("oversized reply accepted")
	}
}

func TestNativeQueueHonorsCancellation(t *testing.T) {
	f := newNativeFixture(t, nativeDefaultReply)
	s := nativeState(f)
	s.nativeGate <- struct{}{}
	defer func() { <-s.nativeGate }()
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	r := httptest.NewRequest("POST", "/", bytes.NewBufferString(nativeBody())).WithContext(ctx)
	r.SetPathValue("mac", nativeTestMac)
	if _, err := s.nativeRoamPayload(r); err == nil {
		t.Fatal("canceled queued request accepted")
	}
	if len(f.sent()) != 0 {
		t.Fatal("canceled request sent a frame")
	}
}

func TestPublicBssConfigNeverSerializesCredentials(t *testing.T) {
	_, _, security := publicBssConfig(nativeTestConfig + "passphrase=fixture-private-value\npsk=fixture-private-key\n")
	data, err := json.Marshal(security)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(data), "fixture-private") {
		t.Fatalf("credentials escaped the allowlist: %s", data)
	}
	neighbor, _, err := nativeNeighbor(nativeTestElement + "0301ff")
	if err != nil || neighbor != "neighbor=02:00:00:00:02:01,7,115,36,9,0301ff" {
		t.Fatalf("subelement not preserved: %q %v", neighbor, err)
	}
}

func TestNativeConfigSSIDsDecodeBeforeMatching(t *testing.T) {
	for _, tc := range []struct{ encoded, want string }{
		{`Caf\xc3\xa9`, "Café"}, {`Test\\name`, `Test\name`}, {`Test\"name\"`, `Test"name"`},
		{`Test\e`, "Test\x1b"}, {`Test\\e`, `Test\e`},
		{`broken\xzz`, ""}, {`binary\xff`, ""}, {strings.Repeat("a", 33), ""},
	} {
		ssid, _, security := publicBssConfig(strings.Replace(nativeTestConfig, "TestNet", tc.encoded, 1))
		if ssid != tc.want || (security != nil) != (tc.want != "") {
			t.Errorf("%q: got %q, %v; want %q", tc.encoded, ssid, security, tc.want)
		}
	}
}

func TestNeighborReportsKeepLegacyDataAndAddOnlyMatchingPublicSecurity(t *testing.T) {
	bin := t.TempDir()
	ubus := "#!/bin/sh\nprintf '%s\\n' '{\"value\":[\"02:00:00:00:01:01\",\"TestNet\",\"02000000010107000000732409\"]}'\n"
	if err := os.WriteFile(filepath.Join(bin, "ubus"), []byte(ubus), 0700); err != nil {
		t.Fatal(err)
	}
	t.Setenv("PATH", bin+string(os.PathListSeparator)+os.Getenv("PATH"))
	for _, mode := range []string{"matching", "SSID changed", "BSSID changed", "unknown"} {
		t.Run(mode, func(t *testing.T) {
			f := newNativeFixture(t, func(_, command string) string {
				if command != "GET_CONFIG" {
					return "UNKNOWN COMMAND"
				}
				config := nativeTestConfig + "passphrase=fixture-private-value\n"
				switch mode {
				case "SSID changed":
					return strings.Replace(config, "TestNet", "OtherNet", 1)
				case "BSSID changed":
					return strings.Replace(config, "02:00:00:00:01:01", "02:00:00:00:01:02", 1)
				case "unknown":
					return "UNKNOWN COMMAND"
				default:
					return config
				}
			})
			reports := neighborReports(context.Background(), []string{"wifi1ap0"}, f.dir)
			if len(reports) != 1 || reports[0].Element != "02000000010107000000732409" {
				t.Fatalf("legacy report changed: %v", reports)
			}
			if (reports[0].Security != nil) != (mode == "matching") {
				t.Fatalf("security %v for %s", reports[0].Security, mode)
			}
			data, err := json.Marshal(reports)
			if err != nil || strings.Contains(string(data), "fixture-private") {
				t.Fatalf("unsafe report: %s, %v", data, err)
			}
			legacy := neighborReports(context.Background(), []string{"wifi1ap0"})
			if len(legacy) != 1 || legacy[0].Security != nil {
				t.Fatalf("legacy control callers changed: %v", legacy)
			}
		})
	}
}
