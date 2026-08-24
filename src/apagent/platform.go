package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"strings"
)

// PlatformInfo describes the host, for diagnostics only. Nothing here gates a feature: probes
// decide what works, because a model or firmware allowlist breaks on every new SKU.
type PlatformInfo struct {
	Machine       string `json:"machine"`
	KernelRelease string `json:"kernel_release,omitempty"`
	Model         string `json:"model,omitempty"`
	ModelShort    string `json:"model_short,omitempty"`
	Firmware      string `json:"firmware,omitempty"`
	GOARCH        string `json:"goarch"`
}

// InterfaceInfo is one network interface the agent can see, so a collector can reconcile where
// it is probing against where the agent actually listens.
type InterfaceInfo struct {
	Name      string   `json:"name"`
	MAC       string   `json:"mac,omitempty"`
	Up        bool     `json:"up"`
	Addresses []string `json:"addresses,omitempty"`
}

// parseKeyValueFile parses the `key=value` shape of /etc/board.info.
func parseKeyValueFile(data string) map[string]string {
	out := map[string]string{}
	for _, line := range strings.Split(data, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		k, v, found := strings.Cut(line, "=")
		if !found {
			continue
		}
		out[strings.TrimSpace(k)] = strings.Trim(strings.TrimSpace(v), `"`)
	}
	return out
}

func readBoardInfo(path string) map[string]string {
	data, err := os.ReadFile(path)
	if err != nil {
		return map[string]string{}
	}
	return parseKeyValueFile(string(data))
}

func readFirmwareVersion(path string) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	return strings.TrimSpace(firstLine(string(data)))
}

// collectInterfaces enumerates interfaces and their addresses.
func collectInterfaces() []InterfaceInfo {
	ifaces, err := net.Interfaces()
	if err != nil {
		return nil
	}
	out := make([]InterfaceInfo, 0, len(ifaces))
	for _, ifi := range ifaces {
		info := InterfaceInfo{
			Name: ifi.Name,
			MAC:  ifi.HardwareAddr.String(),
			Up:   ifi.Flags&net.FlagUp != 0,
		}
		addrs, err := ifi.Addrs()
		if err == nil {
			for _, a := range addrs {
				info.Addresses = append(info.Addresses, a.String())
			}
		}
		out = append(out, info)
	}
	return out
}

// managementMAC is the AP identity a payload carries, taken from the interface the listener is
// bound to so a collector fanning out over a fleet can tell the answers apart.
func managementMAC(ifaces []InterfaceInfo, name string) string {
	for _, ifi := range ifaces {
		if ifi.Name == name && ifi.MAC != "" {
			return strings.ToLower(ifi.MAC)
		}
	}
	return ""
}

// mcaSummary is the shape assertion for mca-dump: enough to prove the payload is what we expect
// without depending on any single field's name surviving a firmware bump.
type mcaSummary struct {
	Version    string   `json:"-"`
	Model      string   `json:"-"`
	RadioCount int      `json:"-"`
	VapCount   int      `json:"-"`
	RadioNames []string `json:"-"`
}

type mcaNamedRadio struct {
	Name string `json:"name"`
}

type mcaRaw struct {
	Version    string           `json:"version"`
	Model      string           `json:"model"`
	RadioTable *[]mcaNamedRadio `json:"radio_table"`
	// The dedicated scan radio is in scan_radio_table, not radio_table. Reading only radio_table
	// drops a real radio, which then never gets health counters collected for it.
	ScanRadioTable *[]mcaNamedRadio `json:"scan_radio_table"`
	VapTable       *[]struct {
		RadioName string `json:"radio_name"`
	} `json:"vap_table"`
}

// parseMcaDump asserts radio_table is present, which is the documented shape check.
func parseMcaDump(data []byte) (mcaSummary, error) {
	var raw mcaRaw
	if err := json.Unmarshal(data, &raw); err != nil {
		return mcaSummary{}, fmt.Errorf("parse mca-dump JSON: %w", err)
	}
	if raw.RadioTable == nil {
		return mcaSummary{}, fmt.Errorf("mca-dump JSON has no radio_table")
	}
	s := mcaSummary{Version: raw.Version, Model: raw.Model, RadioCount: len(*raw.RadioTable)}
	rows := append([]mcaNamedRadio(nil), *raw.RadioTable...)
	if raw.ScanRadioTable != nil {
		rows = append(rows, *raw.ScanRadioTable...)
	}
	for _, r := range rows {
		if r.Name != "" {
			s.RadioNames = append(s.RadioNames, r.Name)
		}
	}
	if raw.VapTable != nil {
		s.VapCount = len(*raw.VapTable)
		// A VAP names its parent radio, which catches a radio missing from both tables.
		for _, v := range *raw.VapTable {
			if v.RadioName != "" {
				s.RadioNames = append(s.RadioNames, v.RadioName)
			}
		}
	}
	s.RadioNames = mergeRadios(s.RadioNames, nil)
	return s, nil
}

// parseWlanconfigHeader validates the header row of `wlanconfig <vap> list sta` and returns its
// columns. Shape is validated rather than firmware version, because column sets drift.
func parseWlanconfigHeader(out string) ([]string, bool) {
	for _, line := range strings.Split(out, "\n") {
		fields := strings.Fields(line)
		if len(fields) < 3 || fields[0] != "ADDR" {
			continue
		}
		have := map[string]bool{}
		for _, f := range fields {
			have[f] = true
		}
		if have["TXRATE"] || have["RSSI"] {
			return fields, true
		}
	}
	return nil, false
}

// stahtdMarker is the log line stahtd emits for every association, roam, and leave.
const stahtdMarker = "STA_ASSOC_TRACKER"

func containsStahtd(data []byte) bool {
	return strings.Contains(string(data), stahtdMarker)
}

// tailFile reads the last maxBytes of a file, which keeps the syslog probe bounded on a box whose
// log can be large.
func tailFile(path string, maxBytes int64) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	fi, err := f.Stat()
	if err != nil {
		return nil, err
	}
	offset := int64(0)
	if fi.Size() > maxBytes {
		offset = fi.Size() - maxBytes
	}
	if _, err := f.Seek(offset, io.SeekStart); err != nil {
		return nil, err
	}
	return io.ReadAll(io.LimitReader(f, maxBytes))
}

// radioCounterNames are counters the radio-health probe expects; any one proves the tool answered
// with the counter set we can use.
var radioCounterNames = []string{"pdev_resets", "cu_total", "cu_interf", "cu_self_tx", "lithium_cycle_cnt"}

// matchedRadioCounters returns which known counter names appear in a radio stats dump.
func matchedRadioCounters(out string) []string {
	found := make([]string, 0, len(radioCounterNames))
	for _, name := range radioCounterNames {
		if strings.Contains(out, name) {
			found = append(found, name)
		}
	}
	return found
}
