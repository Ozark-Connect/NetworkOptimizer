package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

const testToken = "0123456789abcdef0123"

func TestMachineToGOARCH(t *testing.T) {
	cases := map[string]string{
		"armv7l":   "arm",
		"armv6l":   "arm",
		"armv8l":   "arm",
		"aarch64":  "arm64",
		"arm64":    "arm64",
		"x86_64":   "amd64",
		"i686":     "386",
		"  ARMv7L": "arm",
		"riscv64":  "",
		"":         "",
	}
	for machine, want := range cases {
		if got := machineToGOARCH(machine); got != want {
			t.Errorf("machineToGOARCH(%q) = %q, want %q", machine, got, want)
		}
	}
}

func TestArchGate(t *testing.T) {
	cases := []struct {
		machine, goarch string
		wantOK          bool
		wantReasonEmpty bool
	}{
		{"armv7l", "arm", true, true},
		{"armv8l", "arm", true, true},
		{"aarch64", "arm64", true, true},
		{"x86_64", "amd64", true, true},
		// An arm64 build on a measured U7 AP is the case the gate exists to catch.
		{"armv7l", "arm64", false, false},
		{"aarch64", "arm", false, false},
		// Unknown machines and a missing uname pass with a reason: the wrapper is the primary gate
		// and a new SKU must not be locked out here.
		{"riscv64", "arm", true, false},
		{"", "arm", true, false},
	}
	for _, c := range cases {
		ok, reason := archGate(c.machine, c.goarch)
		if ok != c.wantOK {
			t.Errorf("archGate(%q, %q) ok = %v, want %v (reason %q)", c.machine, c.goarch, ok, c.wantOK, reason)
		}
		if (reason == "") != c.wantReasonEmpty {
			t.Errorf("archGate(%q, %q) reason = %q, wantEmpty %v", c.machine, c.goarch, reason, c.wantReasonEmpty)
		}
		if !ok && !strings.Contains(reason, c.machine) {
			t.Errorf("archGate(%q, %q) refusal must name the host machine, got %q", c.machine, c.goarch, reason)
		}
	}
}

func TestFilterVapNames(t *testing.T) {
	// Measured names differ per AP, and back-yard also carries mesh VAPs.
	entries := []string{"wifi2ap11", "global", "wifi0ap0", "vwireap14", ".hidden", "", "vwireap10"}
	want := []string{"vwireap10", "vwireap14", "wifi0ap0", "wifi2ap11"}
	if got := filterVapNames(entries); !reflect.DeepEqual(got, want) {
		t.Errorf("filterVapNames = %v, want %v", got, want)
	}
}

func TestRadiosFromVaps(t *testing.T) {
	got := radiosFromVaps([]string{"wifi2ap10", "wifi2ap11", "wifi0ap0", "vwireap10", "wifi1ap5"})
	want := []string{"wifi0", "wifi1", "wifi2"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("radiosFromVaps = %v, want %v", got, want)
	}
	if got := radiosFromVaps([]string{"vwireap10", "vwireap14"}); len(got) != 0 {
		t.Errorf("mesh VAPs carry no radio prefix, got %v", got)
	}
}

func TestMergeRadios(t *testing.T) {
	got := mergeRadios([]string{"wifi2", "wifi0"}, []string{"wifi2", "wifi1", ""})
	want := []string{"wifi0", "wifi1", "wifi2"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("mergeRadios = %v, want %v", got, want)
	}
}

func TestDiscoverVaps(t *testing.T) {
	dir := t.TempDir()
	for _, name := range []string{"wifi2ap10", "global", "wifi0ap0"} {
		if err := os.WriteFile(filepath.Join(dir, name), nil, 0o600); err != nil {
			t.Fatal(err)
		}
	}
	got, err := discoverVaps(dir)
	if err != nil {
		t.Fatal(err)
	}
	if want := []string{"wifi0ap0", "wifi2ap10"}; !reflect.DeepEqual(got, want) {
		t.Errorf("discoverVaps = %v, want %v", got, want)
	}
	if _, err := discoverVaps(filepath.Join(dir, "absent")); err == nil {
		t.Error("discoverVaps on a missing directory must error")
	}
}

func TestParseWlanconfigHeader(t *testing.T) {
	out := "ADDR               AID CHAN TXRATE RXRATE RSSI MINRSSI MAXRSSI IDLE  TXSEQ  RXSEQ  CAPS ACAPS ERP    STATE MAXRATE(DOT11) HTCAPS ASSOCTIME    IEs   MODE PSMODE RXNSS TXNSS\n" +
		"10:a2:d3:1f:ec:32    1  149  1200M  1080M   -54      -60     -48    0      0      0   EPS    0    0        0              0     A     00:12:34    WME  11BE     0     2     2\n"
	cols, ok := parseWlanconfigHeader(out)
	if !ok {
		t.Fatal("measured wlanconfig header must validate")
	}
	if cols[0] != "ADDR" || len(cols) < 10 {
		t.Errorf("unexpected columns: %v", cols)
	}
	if _, ok := parseWlanconfigHeader("wlanconfig: ioctl: No such device\n"); ok {
		t.Error("an error message must not validate as a header")
	}
	if _, ok := parseWlanconfigHeader(""); ok {
		t.Error("empty output must not validate as a header")
	}
}

func TestParseMcaDump(t *testing.T) {
	good := []byte(`{"version":"8.7.11","model":"U7PROXGSB",
	  "radio_table":[{"name":"wifi0"},{"name":"wifi1"},{"name":"wifi2"}],
	  "vap_table":[{"name":"wifi2ap10"},{"name":"wifi0ap0"}]}`)
	s, err := parseMcaDump(good)
	if err != nil {
		t.Fatal(err)
	}
	if s.Version != "8.7.11" || s.RadioCount != 3 || s.VapCount != 2 {
		t.Errorf("unexpected summary: %+v", s)
	}
	if want := []string{"wifi0", "wifi1", "wifi2"}; !reflect.DeepEqual(s.RadioNames, want) {
		t.Errorf("radio names = %v, want %v", s.RadioNames, want)
	}

	// An empty radio_table is a valid shape; a missing one is not.
	if _, err := parseMcaDump([]byte(`{"radio_table":[]}`)); err != nil {
		t.Errorf("empty radio_table must be accepted: %v", err)
	}
	if _, err := parseMcaDump([]byte(`{"vap_table":[]}`)); err == nil {
		t.Error("missing radio_table must fail the shape check")
	}
	if _, err := parseMcaDump([]byte(`not json`)); err == nil {
		t.Error("non-JSON must fail the shape check")
	}
}

func TestParseUbusObjects(t *testing.T) {
	out := "hostapd\nhostapd.wifi2ap10\nhostapd.wifi0ap0\nnetwork.interface\nsystem\n"
	got := parseUbusObjects(out, "hostapd")
	want := []string{"hostapd", "hostapd.wifi0ap0", "hostapd.wifi2ap10"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("parseUbusObjects = %v, want %v", got, want)
	}
	if got := parseUbusObjects("network.interface\nsystem\n", "hostapd"); len(got) != 0 {
		t.Errorf("no hostapd objects expected, got %v", got)
	}
}

func TestParseUbusMethodsAndControlInventory(t *testing.T) {
	out := strings.Join([]string{
		"'hostapd.wifi2ap10' @a1b2c3d4",
		"\t\"get_clients\":{}",
		"\t\"del_client\":{\"addr\":\"String\",\"reason\":\"Integer\",\"deauth\":\"Boolean\"}",
		"\t\"switch_chan\":{\"freq\":\"Integer\",\"bcn_count\":\"Integer\"}",
		"\t\"rrm_nr_list\":{}",
		"\t\"rrm_beacon_req\":{\"addr\":\"String\",\"mode\":\"Integer\"}",
		"\t\"wnm_disassoc_imminent\":{\"addr\":\"String\",\"duration\":\"Integer\"}",
	}, "\n")

	methods := parseUbusMethods(out)
	for _, want := range []string{"get_clients", "del_client", "switch_chan", "rrm_nr_list"} {
		if !contains(methods, want) {
			t.Errorf("method %q missing from %v", want, methods)
		}
	}
	// Nested argument names must not be mistaken for methods.
	for _, unwanted := range []string{"addr", "reason", "freq", "String"} {
		if contains(methods, unwanted) {
			t.Errorf("argument %q was parsed as a method: %v", unwanted, methods)
		}
	}

	watched := watchedMethods(methods)
	if len(watched) != len(controlMethodsOfInterest) {
		t.Fatalf("watched map must cover every method of interest, got %v", watched)
	}
	for _, m := range controlMethodsOfInterest {
		if !watched[m] {
			t.Errorf("watched[%q] = false, expected present in the measured method set", m)
		}
	}

	// An AP that exposes none of them must report false rather than omitting the key.
	partial := watchedMethods([]string{"get_clients"})
	for _, m := range controlMethodsOfInterest {
		if partial[m] {
			t.Errorf("watched[%q] = true against a method set that lacks it", m)
		}
	}
	if _, ok := partial["switch_chan"]; !ok {
		t.Error("an absent control method must still be reported as a key")
	}
}

func TestContainsStahtdAndTailFile(t *testing.T) {
	line := `{"op":"event","message_type":"STA_ASSOC_TRACKER","event_type":"association"}`
	if !containsStahtd([]byte("noise\n" + line + "\n")) {
		t.Error("stahtd line must be detected")
	}
	if containsStahtd([]byte("daemon.info hostapd: wifi2ap10: AP-STA-CONNECTED aa:bb:cc:dd:ee:ff\n")) {
		t.Error("a hostapd line is not a stahtd line")
	}

	path := filepath.Join(t.TempDir(), "messages")
	body := strings.Repeat("filler line\n", 4000) + line + "\n"
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	data, err := tailFile(path, 4096)
	if err != nil {
		t.Fatal(err)
	}
	if len(data) > 4096 {
		t.Errorf("tailFile read %d bytes, cap is 4096", len(data))
	}
	if !containsStahtd(data) {
		t.Error("tailFile must return the end of the file, where the newest line is")
	}
	if _, err := tailFile(filepath.Join(t.TempDir(), "absent"), 4096); err == nil {
		t.Error("tailFile on a missing file must error")
	}
}

func TestMatchedRadioCounters(t *testing.T) {
	got := matchedRadioCounters("pdev_resets            0\ncu_total  41\ncu_interf 12\n")
	want := []string{"pdev_resets", "cu_total", "cu_interf"}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("matchedRadioCounters = %v, want %v", got, want)
	}
	if got := matchedRadioCounters("athstats: not found"); len(got) != 0 {
		t.Errorf("no counters expected, got %v", got)
	}
}

func TestParseKeyValueFile(t *testing.T) {
	kv := parseKeyValueFile("# comment\nboard.name=U7-Pro-XGS-B\nboard.shortname=\"U7PROXGSB\"\njunk\n")
	if kv["board.name"] != "U7-Pro-XGS-B" || kv["board.shortname"] != "U7PROXGSB" {
		t.Errorf("unexpected board info: %v", kv)
	}
}

func TestLoadConfigDefaultsAndOverrides(t *testing.T) {
	t.Setenv(tokenEnvVar, testToken)

	cfg, err := loadConfig(filepath.Join(t.TempDir(), "absent.json"), Overrides{})
	if err != nil {
		t.Fatalf("an absent config file plus the token env var is a valid configuration: %v", err)
	}
	if cfg.Port != defaultPort {
		t.Errorf("port = %d, want %d", cfg.Port, defaultPort)
	}
	if cfg.ListenInterface != defaultListenInterface {
		t.Errorf("listen interface = %q, want %q", cfg.ListenInterface, defaultListenInterface)
	}
	if cfg.HostapdDir != defaultHostapdDir || cfg.MemoryLimitMB != defaultMemoryLimitMB {
		t.Errorf("unexpected defaults: %+v", cfg)
	}

	path := filepath.Join(t.TempDir(), "config.json")
	body := `{"listen_interface":"br0.10","port":9100,"memory_limit_mb":32,"hostapd_dir":"/run/hostapd"}`
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, err = loadConfig(path, Overrides{})
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Port != 9100 || cfg.ListenInterface != "br0.10" || cfg.MemoryLimitMB != 32 || cfg.HostapdDir != "/run/hostapd" {
		t.Errorf("config file not applied: %+v", cfg)
	}

	cfg, err = loadConfig(path, Overrides{Port: 8899, ListenInterface: "eth0", HostapdDir: "/tmp/h"})
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Port != 8899 || cfg.ListenInterface != "eth0" || cfg.HostapdDir != "/tmp/h" {
		t.Errorf("flags must win over the config file: %+v", cfg)
	}
}

func TestLoadConfigRefusesUnauthenticated(t *testing.T) {
	t.Setenv(tokenEnvVar, "")
	dir := t.TempDir()

	if _, err := loadConfig(filepath.Join(dir, "absent.json"), Overrides{}); err == nil {
		t.Fatal("a configuration with no bearer token must be refused")
	} else if !strings.Contains(err.Error(), tokenEnvVar) {
		t.Errorf("the refusal must name how to set a token, got %q", err)
	}

	short := filepath.Join(dir, "short.json")
	if err := os.WriteFile(short, []byte(`{"token":"tooshort"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadConfig(short, Overrides{}); err == nil {
		t.Error("a short token must be refused")
	}

	// A port in the AP's ephemeral range can collide with an outbound socket.
	eph := filepath.Join(dir, "eph.json")
	if err := os.WriteFile(eph, []byte(`{"token":"`+testToken+`","port":40000}`), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadConfig(eph, Overrides{}); err == nil {
		t.Error("a port in the ephemeral range must be refused")
	}
}

func TestResolveTokenPrecedence(t *testing.T) {
	dir := t.TempDir()
	tokenPath := filepath.Join(dir, "token")
	if err := os.WriteFile(tokenPath, []byte("filetoken0123456789\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	cfgPath := filepath.Join(dir, "config.json")
	cfgBody, err := json.Marshal(map[string]string{
		"token":      "configtoken0123456789",
		"token_file": tokenPath,
	})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(cfgPath, cfgBody, 0o600); err != nil {
		t.Fatal(err)
	}

	t.Setenv(tokenEnvVar, "envtoken01234567890")
	cfg, err := loadConfig(cfgPath, Overrides{})
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Token != "envtoken01234567890" {
		t.Errorf("environment must win, got %q", cfg.Token)
	}

	t.Setenv(tokenEnvVar, "")
	cfg, err = loadConfig(cfgPath, Overrides{})
	if err != nil {
		t.Fatal(err)
	}
	if cfg.Token != "filetoken0123456789" {
		t.Errorf("token file must win over the config file value, got %q", cfg.Token)
	}
}

func TestAddressForInterface(t *testing.T) {
	ifaces := []InterfaceInfo{
		{Name: "lo", Addresses: []string{"127.0.0.1/8", "::1/128"}},
		{Name: "br0", Addresses: []string{"fe80::1/64", "192.168.1.20/24"}},
		{Name: "br0.64", Addresses: []string{"192.168.64.1/24"}},
		{Name: "eth1", Addresses: []string{"fe80::2/64"}},
	}
	got, err := addressForInterface(ifaces, "br0")
	if err != nil {
		t.Fatal(err)
	}
	if got != "192.168.1.20" {
		t.Errorf("addressForInterface(br0) = %q, want 192.168.1.20", got)
	}
	if _, err := addressForInterface(ifaces, "eth1"); err == nil {
		t.Error("an interface with no IPv4 address must error")
	}
	_, err = addressForInterface(ifaces, "br9")
	if err == nil || !strings.Contains(err.Error(), "br0.64") {
		t.Errorf("a missing interface must list what is present, got %v", err)
	}
}

func TestResolveBindAddressPrefersExplicit(t *testing.T) {
	ifaces := []InterfaceInfo{{Name: "br0", Addresses: []string{"192.168.1.20/24"}}}
	cfg := &Config{ListenInterface: "br0", ListenAddress: "127.0.0.1"}
	got, err := resolveBindAddress(cfg, ifaces)
	if err != nil || got != "127.0.0.1" {
		t.Errorf("explicit address must win, got %q err %v", got, err)
	}
	if !isWildcardAddress("0.0.0.0") || isWildcardAddress("192.168.1.20") {
		t.Error("wildcard detection is wrong")
	}
}

func TestProbeSetHelpers(t *testing.T) {
	set := ProbeSet{Results: []ProbeResult{
		{Name: ProbeHostapdCtrl, Fatal: true, Available: true},
		{Name: ProbeWlanconfig, Available: false},
		{Name: ProbeMcaDump, Available: true},
	}}
	if _, isFatal := set.FatalFailure(); isFatal {
		t.Error("a resolved fatal probe is not a failure")
	}
	if got := set.Unavailable(); !reflect.DeepEqual(got, []string{ProbeWlanconfig}) {
		t.Errorf("Unavailable = %v", got)
	}
	if _, ok := set.Get(ProbeMcaDump); !ok {
		t.Error("Get must find a present probe")
	}
	if _, ok := set.Get(ProbeStahtd); ok {
		t.Error("Get must not invent an absent probe")
	}

	set.Results[0].Available = false
	failed, isFatal := set.FatalFailure()
	if !isFatal || failed.Name != ProbeHostapdCtrl {
		t.Errorf("an unresolved hostapd socket must be fatal, got %v %v", failed, isFatal)
	}
}

func TestProbeHostapdCtrlReportsMissingSocketReadably(t *testing.T) {
	cfg := &Config{HostapdDir: filepath.Join(t.TempDir(), "absent")}
	_, err := discoverVaps(cfg.HostapdDir)
	r := probeHostapdCtrl(cfg, nil, err, time.Now().UTC())
	if r.Available || !r.Fatal {
		t.Fatalf("a missing control directory must be a fatal miss: %+v", r)
	}
	if !strings.Contains(r.Detail, cfg.HostapdDir) {
		t.Errorf("the detail must name the directory it looked in, got %q", r.Detail)
	}

	empty := &Config{HostapdDir: t.TempDir()}
	r = probeHostapdCtrl(empty, nil, nil, time.Now().UTC())
	if r.Available || !strings.Contains(r.Detail, "no VAP sockets") {
		t.Errorf("an empty control directory must report no VAP sockets, got %q", r.Detail)
	}
}

func TestCapabilitiesSerialization(t *testing.T) {
	state := NewState(time.Now().UTC().Add(-time.Minute), PlatformInfo{Machine: "armv7l", GOARCH: "arm"})
	state.SetListener(ListenerInfo{Interface: "br0", Address: "192.168.1.20", Port: 8899, Auth: "bearer"})
	state.SetProbes(ProbeSet{
		Results:  []ProbeResult{{Name: ProbeHostapdCtrl, Fatal: true, Available: true, CheckedAt: time.Now().UTC()}},
		Vaps:     []string{"wifi2ap10"},
		Radios:   []string{"wifi2"},
		Firmware: "8.7.11",
		ControlSurface: []ControlSurface{{
			Vap: "wifi2ap10", Watched: map[string]bool{"switch_chan": true}, AllMethods: []string{"switch_chan"},
		}},
		ProbedAt: time.Now().UTC(),
	})

	data, err := json.Marshal(state.Capabilities())
	if err != nil {
		t.Fatal(err)
	}
	var round map[string]any
	if err := json.Unmarshal(data, &round); err != nil {
		t.Fatal(err)
	}
	// The collector keys on these, so the names are a contract.
	for _, key := range []string{"agent", "platform", "listener", "vaps", "radios", "probes",
		"control_surface", "interfaces", "probed_at", "collected_at"} {
		if _, ok := round[key]; !ok {
			t.Errorf("capabilities payload is missing %q", key)
		}
	}
	agent, ok := round["agent"].(map[string]any)
	if !ok {
		t.Fatal("agent block is not an object")
	}
	if agent["binary_version"].(float64) != float64(binaryVersion()) {
		t.Error("capabilities must carry the embedded contract version")
	}
	// The firmware read off mca-dump must reach the platform block.
	platform, ok := round["platform"].(map[string]any)
	if !ok {
		t.Fatal("platform block is not an object")
	}
	if platform["firmware"] != "8.7.11" {
		t.Errorf("firmware not folded into platform: %v", platform)
	}
}

func TestHealthPayload(t *testing.T) {
	state := NewState(time.Now().UTC().Add(-90*time.Second), PlatformInfo{})
	state.SetProbes(ProbeSet{
		Results: []ProbeResult{
			{Name: ProbeHostapdCtrl, Fatal: true, Available: true, CheckedAt: time.Now().UTC()},
			{Name: ProbeStahtd, Available: false, CheckedAt: time.Now().UTC()},
		},
		ProbedAt: time.Now().UTC(),
	})

	h := state.Health()
	if h.UptimeSeconds < 89 {
		t.Errorf("uptime = %d, want about 90", h.UptimeSeconds)
	}
	if !h.Degraded || !reflect.DeepEqual(h.Unavailable, []string{ProbeStahtd}) {
		t.Errorf("a missing non-fatal probe must read as degraded: %+v", h)
	}
	if h.ProbeRuns != 1 || h.ProbeFailures != 1 {
		t.Errorf("counters = runs %d failures %d, want 1 and 1", h.ProbeRuns, h.ProbeFailures)
	}
	if _, ok := h.Probes[ProbeHostapdCtrl]; !ok {
		t.Error("health must carry a per-probe timestamp")
	}
}

func newTestServer(t *testing.T) (*httptest.Server, *State) {
	t.Helper()
	state := NewState(time.Now().UTC(), PlatformInfo{Machine: "armv7l", GOARCH: "arm"})
	state.SetProbes(ProbeSet{
		Results:  []ProbeResult{{Name: ProbeHostapdCtrl, Fatal: true, Available: true, CheckedAt: time.Now().UTC()}},
		Vaps:     []string{"wifi2ap10"},
		ProbedAt: time.Now().UTC(),
	})
	srv := httptest.NewServer(authMiddleware(state, testToken, testMux(state)))
	t.Cleanup(srv.Close)
	return srv, state
}

func testMux(state *State) *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/capabilities", jsonHandler(func() any { return state.Capabilities() }))
	mux.HandleFunc("/health", jsonHandler(func() any { return state.Health() }))
	return mux
}

func doRequest(t *testing.T, srv *httptest.Server, method, path, auth string) *http.Response {
	t.Helper()
	req, err := http.NewRequest(method, srv.URL+path, nil)
	if err != nil {
		t.Fatal(err)
	}
	if auth != "" {
		req.Header.Set("Authorization", auth)
	}
	resp, err := srv.Client().Do(req)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { resp.Body.Close() })
	return resp
}

func TestServerRefusesUnauthenticated(t *testing.T) {
	srv, state := newTestServer(t)

	for _, auth := range []string{"", "Bearer wrong", "Basic " + testToken, testToken} {
		resp := doRequest(t, srv, http.MethodGet, "/capabilities", auth)
		if resp.StatusCode != http.StatusUnauthorized {
			t.Errorf("auth %q got %d, want 401", auth, resp.StatusCode)
		}
		if resp.Header.Get("WWW-Authenticate") == "" {
			t.Errorf("auth %q: a 401 must carry WWW-Authenticate", auth)
		}
	}
	if state.counters.AuthFailures.Load() != 4 {
		t.Errorf("auth failures = %d, want 4", state.counters.AuthFailures.Load())
	}
}

func TestServerServesBothEndpoints(t *testing.T) {
	srv, _ := newTestServer(t)
	auth := "Bearer " + testToken

	resp := doRequest(t, srv, http.MethodGet, "/capabilities", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/capabilities got %d", resp.StatusCode)
	}
	if ct := resp.Header.Get("Content-Type"); ct != "application/json" {
		t.Errorf("content type = %q", ct)
	}
	var caps Capabilities
	if err := json.NewDecoder(resp.Body).Decode(&caps); err != nil {
		t.Fatal(err)
	}
	if caps.Platform.Machine != "armv7l" || len(caps.Vaps) != 1 {
		t.Errorf("unexpected capabilities: %+v", caps)
	}

	resp = doRequest(t, srv, http.MethodGet, "/health", auth)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("/health got %d", resp.StatusCode)
	}
	var health Health
	if err := json.NewDecoder(resp.Body).Decode(&health); err != nil {
		t.Fatal(err)
	}
	if health.BinaryVersion != binaryVersion() {
		t.Errorf("health binary version = %d", health.BinaryVersion)
	}

	if resp := doRequest(t, srv, http.MethodGet, "/clients", auth); resp.StatusCode != http.StatusNotFound {
		t.Errorf("/clients is W5 and must not exist yet, got %d", resp.StatusCode)
	}
	if resp := doRequest(t, srv, http.MethodPost, "/health", auth); resp.StatusCode != http.StatusMethodNotAllowed {
		t.Errorf("POST /health got %d, want 405", resp.StatusCode)
	}
}

func contains(items []string, want string) bool {
	for _, s := range items {
		if s == want {
			return true
		}
	}
	return false
}
