package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	nativeControlTimeout = 500 * time.Millisecond
	maxNativeReply       = 16 * 1024
	maxNativeCommand     = 4095
	maxNativeCandidates  = 8
)

var nativeVapName = regexp.MustCompile(`^[a-zA-Z0-9_.-]+$`)

// BssSecurity contains only public protocol fields. GET_CONFIG can also return credentials;
// those must never enter a payload, an error or a log. Exact matching deliberately excludes
// incomplete reports and networks whose security differs between access points.
type BssSecurity struct {
	Wpa      string `json:"wpa"`
	KeyMgmt  string `json:"key_mgmt"`
	Pairwise string `json:"pairwise"`
}

func canonicalWords(s string) string {
	words := strings.Fields(s)
	sort.Strings(words)
	return strings.Join(words, " ")
}

// GET_CONFIG uses hostapd's printf_encode (including byte escapes for UTF-8). Compare the
// decoded SSID with JSON telemetry and ubus reports, preserving literal escaped backslashes.
func controlSSID(encoded string) string {
	var decoded strings.Builder
	for encoded != "" {
		if strings.HasPrefix(encoded, `\e`) {
			decoded.WriteByte(27)
			encoded = encoded[2:]
			continue
		}
		ch, multi, rest, err := strconv.UnquoteChar(encoded, '"')
		if err != nil {
			return ""
		}
		if multi {
			decoded.WriteRune(ch)
		} else {
			decoded.WriteByte(byte(ch))
		}
		encoded = rest
	}
	ssid := decoded.String()
	if len(ssid) == 0 || len(ssid) > 32 || !utf8.ValidString(ssid) {
		return ""
	}
	return ssid
}

func publicBssConfig(raw string) (string, string, *BssSecurity) {
	fields := map[string]string{}
	for _, line := range strings.Split(raw, "\n") {
		key, value, ok := strings.Cut(line, "=")
		if ok {
			switch key {
			case "ssid", "bssid", "wpa", "key_mgmt", "rsn_pairwise_cipher":
				fields[key] = value
			}
		}
	}
	ssid := controlSSID(fields["ssid"])
	security := &BssSecurity{fields["wpa"], canonicalWords(fields["key_mgmt"]), canonicalWords(fields["rsn_pairwise_cipher"])}
	if security.Wpa != "2" || security.KeyMgmt == "" || security.Pairwise == "" || ssid == "" {
		security = nil
	}
	return ssid, strings.ToLower(fields["bssid"]), security
}

// nativeControl uses a fresh, un-attached socket per exchange: collection/event listeners cannot
// consume its reply, and concurrent exchanges never remove each other's local socket. There is
// no retry, including when a mutating command may have reached hostapd before a read timeout.
func nativeControl(ctx context.Context, dir, vap, command string) (string, error) {
	if !nativeVapName.MatchString(vap) || vap == "." || vap == ".." || len(command) > maxNativeCommand || strings.ContainsAny(command, "\x00\r\n") {
		return "", fmt.Errorf("invalid native control request")
	}
	if err := ctx.Err(); err != nil {
		return "", err
	}
	localDir, err := os.MkdirTemp("", "netopt-ctrl-")
	if err != nil {
		return "", fmt.Errorf("create control socket directory: %w", err)
	}
	defer os.RemoveAll(localDir)
	conn, err := net.DialUnix("unixgram", &net.UnixAddr{Name: filepath.Join(localDir, "s"), Net: "unixgram"},
		&net.UnixAddr{Name: filepath.Join(dir, vap), Net: "unixgram"})
	if err != nil {
		return "", fmt.Errorf("connect to hostapd on %s: %w", vap, err)
	}
	defer conn.Close()
	deadline := time.Now().Add(nativeControlTimeout)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}
	if err := conn.SetDeadline(deadline); err != nil {
		return "", err
	}
	stop := context.AfterFunc(ctx, func() { _ = conn.SetDeadline(time.Now()) })
	defer stop()
	if _, err := conn.Write([]byte(command)); err != nil {
		return "", fmt.Errorf("write to hostapd on %s: %w", vap, err)
	}
	buf := make([]byte, maxNativeReply+1)
	n, err := conn.Read(buf)
	if err != nil {
		return "", fmt.Errorf("read from hostapd on %s: %w", vap, err)
	}
	if n > maxNativeReply {
		return "", fmt.Errorf("oversized hostapd reply on %s", vap)
	}
	return strings.TrimSpace(string(buf[:n])), nil
}

// The command recognizer is probed with no station address. Upstream hostapd returns FAIL
// before sending anything when BSS_TM_REQ has no MAC; builds without CONFIG_WNM_AP answer
// UNKNOWN COMMAND. PING or hostapd_cli's help alone do not prove this daemon implements BTM.
func probeNativeVoluntary(ctx context.Context, dir string, vaps []string) []string {
	ctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	var supported []string
	for _, vap := range vaps {
		status, err := nativeControl(ctx, dir, vap, "STATUS")
		if err != nil || !nativeFiveGHz(status) {
			continue
		}
		config, err := nativeControl(ctx, dir, vap, "GET_CONFIG")
		if err != nil {
			continue
		}
		_, _, security := publicBssConfig(config)
		if security == nil {
			continue
		}
		reply, err := nativeControl(ctx, dir, vap, "BSS_TM_REQ ")
		if err == nil && reply == "FAIL" {
			supported = append(supported, vap)
		}
	}
	return supported
}

func nativeFiveGHz(status string) bool {
	for _, line := range strings.Split(status, "\n") {
		if value, ok := strings.CutPrefix(line, "freq="); ok {
			freq, err := strconv.Atoi(value)
			return err == nil && freq >= 5000 && freq < 5925
		}
	}
	return false
}

type NativeCandidate struct {
	Element  string       `json:"element"`
	Ssid     string       `json:"ssid"`
	Security *BssSecurity `json:"security"`
}

// A separate route and body keep voluntary semantics safe with mixed agent versions. Older
// agents return 404/405; they cannot ignore a new flag and apply the legacy eviction timer.
type NativeRoamRequest struct {
	Candidates []NativeCandidate `json:"candidates"`
}

func nativeNeighbor(element string) (string, string, error) {
	data, err := hex.DecodeString(element)
	if err != nil || len(data) < 13 || len(data) > 255 || data[0]&1 != 0 {
		return "", "", badRequest("invalid neighbor report")
	}
	// The first native backend is 5 GHz only. Validate each optional subelement before preserving
	// it: a malformed length must not reach hostapd's neighbor parser.
	if data[10] < 115 || data[10] > 130 || data[11] < 36 || data[11] > 177 || data[12] == 0 {
		return "", "", badRequest("native steering requires a 5 GHz candidate")
	}
	for offset := 13; offset < len(data); {
		if offset+2 > len(data) || offset+2+int(data[offset+1]) > len(data) {
			return "", "", badRequest("invalid neighbor subelement")
		}
		offset += 2 + int(data[offset+1])
	}
	mac := net.HardwareAddr(data[:6]).String()
	if !validNativeMac(mac) {
		return "", "", badRequest("invalid candidate BSSID")
	}
	neighbor := fmt.Sprintf("neighbor=%s,%d,%d,%d,%d", mac, binary.LittleEndian.Uint32(data[6:10]), data[10], data[11], data[12])
	if len(data) > 13 {
		neighbor += "," + hex.EncodeToString(data[13:])
	}
	return neighbor, mac, nil
}

func validNativeMac(mac string) bool {
	addr, err := net.ParseMAC(mac)
	return err == nil && len(mac) == 17 && len(addr) == 6 && addr[0]&1 == 0 && mac != "00:00:00:00:00:00"
}

// nativeStation distinguishes a known absence from a failed/unknown read. Only an exact FAIL
// is absence; a timeout, malformed response or UNKNOWN COMMAND must never imply departure.
func nativeStation(raw, mac string) (bool, error) {
	if raw == "FAIL" {
		return false, nil
	}
	lines := strings.Split(raw, "\n")
	if len(lines) < 2 || !strings.EqualFold(strings.TrimSpace(lines[0]), mac) {
		return false, fmt.Errorf("unknown hostapd station response")
	}
	fields := map[string]string{}
	for _, line := range lines[1:] {
		if key, value, ok := strings.Cut(line, "="); ok {
			fields[key] = value
		}
	}
	flags := fields["flags"]
	if strings.Contains(flags, "[MLO]") || fields["mld_addr"] != "" || fields["mld_mac"] != "" {
		return false, badRequest("native steering does not support MLO stations")
	}
	if !strings.Contains(flags, "[ASSOC]") || !strings.Contains(flags, "[AUTHORIZED]") {
		return false, badRequest("station is not associated and authorized")
	}
	ext, err := hex.DecodeString(fields["ext_capab"])
	if err != nil || len(ext) < 3 || ext[2]&8 == 0 {
		return false, badRequest("station has not advertised BTM support")
	}
	return true, nil
}

func (s *State) nativeRoamPayload(r *http.Request) (any, error) {
	data, err := io.ReadAll(io.LimitReader(r.Body, 64*1024+1))
	if err != nil || len(data) > 64*1024 {
		return nil, badRequest("invalid voluntary transition body")
	}
	var req NativeRoamRequest
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&req); err != nil {
		return nil, badRequest("invalid voluntary transition body")
	}
	if decoder.Decode(new(any)) != io.EOF {
		return nil, badRequest("unexpected trailing transition data")
	}
	mac := strings.ToLower(r.PathValue("mac"))
	if !validNativeMac(mac) || len(req.Candidates) == 0 || len(req.Candidates) > maxNativeCandidates {
		return nil, badRequest("invalid station or candidate count")
	}
	ctx, cancel := context.WithTimeout(r.Context(), 6*time.Second)
	defer cancel()
	// Serialize this AP's native requests without blocking cancellation while another is running.
	select {
	case s.nativeGate <- struct{}{}:
		defer func() { <-s.nativeGate }()
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	return s.sendNativeRoam(ctx, mac, req)
}

func (s *State) sendNativeRoam(ctx context.Context, mac string, req NativeRoamRequest) (*RoamResult, error) {
	s.mu.RLock()
	dir, native, probedAt := s.probes.HostapdDir, append([]string{}, s.probes.NativeVoluntaryVaps...), s.probes.ProbedAt
	s.mu.RUnlock()
	if dir == "" || len(native) == 0 || probedAt.IsZero() || time.Since(probedAt) < 0 || time.Since(probedAt) > 2*defaultProbeInterval*time.Second {
		return nil, httpError{http.StatusNotImplemented, "native voluntary steering is unavailable"}
	}
	table, _ := s.telemetry()
	client, found := FindClient(table.Clients(time.Now()), mac)
	if !found {
		return nil, notFound("client is no longer on this access point")
	}
	if client.IsMlo || client.MldMAC != "" || len(client.Links) != 1 || client.Band != "5" || !client.Authorized ||
		!strings.EqualFold(client.Links[0].MAC, mac) {
		return nil, badRequest("native steering requires one authorized non-MLO 5 GHz link")
	}
	eligible := false
	for _, vap := range native {
		eligible = eligible || vap == client.Vap
	}
	if !eligible {
		return nil, badRequest("the holding VAP cannot send a native voluntary request")
	}
	config, err := nativeControl(ctx, dir, client.Vap, "GET_CONFIG")
	if err != nil {
		return nil, err
	}
	ssid, bssid, security := publicBssConfig(config)
	if security == nil || ssid != client.Links[0].Ssid || !validNativeMac(bssid) {
		return nil, badRequest("source network information is unavailable or changed")
	}
	command := "BSS_TM_REQ " + mac + " disassoc_imminent=0 disassoc_timer=0 valid_int=255 pref=1 abridged=0"
	seen := map[string]bool{}
	neighborBytes := 0
	for _, candidate := range req.Candidates {
		neighbor, dest, err := nativeNeighbor(candidate.Element)
		if err != nil {
			return nil, err
		}
		if candidate.Ssid != ssid || candidate.Security == nil || *candidate.Security != *security || seen[dest] || dest == bssid {
			return nil, badRequest("candidate network is incompatible or duplicated")
		}
		for _, vap := range table.Vaps() {
			if strings.EqualFold(vap.Bssid, dest) {
				return nil, badRequest("native AP move cannot target this access point")
			}
		}
		seen[dest] = true
		neighborBytes += len(candidate.Element)/2 + 2 // Neighbor Report IE ID and length.
		command += " " + neighbor
	}
	if len(command) > maxNativeCommand || neighborBytes > 1000 {
		return nil, badRequest("native candidate list is too large")
	}
	// Query every non-backhaul VAP rather than trusting a table snapshot. Errors remain unknown;
	// a moved or multiply-associated station is refused, and no bounce guard is ever started.
	holding := ""
	for _, vap := range table.ControlVapNames() {
		raw, err := nativeControl(ctx, dir, vap, "STA "+mac)
		if err != nil {
			return nil, err
		}
		present, err := nativeStation(raw, mac)
		if err != nil {
			return nil, err
		}
		if present {
			if holding != "" || vap != client.Vap {
				return nil, notFound("client association changed before the request")
			}
			holding = vap
		}
	}
	if holding == "" {
		return nil, notFound("client left before the request")
	}
	status, err := nativeControl(ctx, dir, holding, "STATUS")
	if err != nil || !nativeFiveGHz(status) {
		return nil, badRequest("holding VAP band changed or is unknown")
	}
	config, err = nativeControl(ctx, dir, holding, "GET_CONFIG")
	if err != nil {
		return nil, err
	}
	finalSSID, finalBSSID, finalSecurity := publicBssConfig(config)
	if finalSSID != ssid || finalBSSID != bssid || finalSecurity == nil || *finalSecurity != *security {
		return nil, badRequest("source network changed before the request")
	}
	// The final native station read and hostapd's own request-time presence check bound the race.
	raw, err := nativeControl(ctx, dir, holding, "STA "+mac)
	if err != nil {
		return nil, err
	}
	present, err := nativeStation(raw, mac)
	if err != nil {
		return nil, err
	}
	if !present {
		return nil, notFound("client left before the request")
	}
	reply, err := nativeControl(ctx, dir, holding, command)
	if err != nil {
		return nil, err
	}
	if reply != "OK" {
		return nil, fmt.Errorf("hostapd did not accept the voluntary BTM request")
	}
	return &RoamResult{Mac: mac, Vap: holding, Candidates: len(req.Candidates), SentAt: time.Now().UTC()}, nil
}
