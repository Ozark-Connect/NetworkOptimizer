package main

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// TcpStats is the per-direction TCP quality block mca-dump carries on a station. A -1 latency
// means the AP has no sample, not a fast link.
type TcpStats struct {
	GoodBytes  int64 `json:"goodbytes"`
	LatAvg     int   `json:"lat_avg"`
	LatMax     int   `json:"lat_max"`
	LatMin     int   `json:"lat_min"`
	LatSamples int   `json:"lat_samples"`
	Stalls     int   `json:"stalls"`
	Retries    int   `json:"retries"`
}

// TxLatency is wifi_tx_latency_mov, the AP's own moving TX latency in microseconds.
type TxLatency struct {
	Avg        int   `json:"avg"`
	Max        int   `json:"max"`
	Min        int   `json:"min"`
	Total      int64 `json:"total"`
	TotalCount int64 `json:"total_count"`
}

// MloInfo is the per-link MLO block. num_links is not the link count: it reported 2 on a client
// with three live entries, so the link count comes from the entries this agent actually merged.
type MloInfo struct {
	IsEmlmr  bool `json:"is_emlmr"`
	IsEmlsr  bool `json:"is_emlsr"`
	IsStr    bool `json:"is_str"`
	NumLinks int  `json:"num_links"`
}

// StaSlow is one sta_table entry: identity plus the quality fields the fast tier cannot see.
// mld_mac is present but null on a non-MLO client, so it is a pointer rather than a string.
type StaSlow struct {
	MAC                   string     `json:"mac"`
	MldMAC                *string    `json:"mld_mac"`
	Hostname              string     `json:"hostname"`
	IP                    string     `json:"ip"`
	IPv6                  []string   `json:"ipv6_addresses"`
	Signal                *int       `json:"signal"`
	RSSI                  *int       `json:"rssi"`
	Noise                 *int       `json:"noise"`
	Nss                   int        `json:"nss"`
	Ccq                   int        `json:"ccq"`
	ChWidth               int        `json:"chwidth"`
	TxRate                int64      `json:"tx_rate"`
	RxRate                int64      `json:"rx_rate"`
	TxRateMov             int64      `json:"tx_rate_mov"`
	RxRateMov             int64      `json:"rx_rate_mov"`
	TxBytes               int64      `json:"tx_bytes"`
	RxBytes               int64      `json:"rx_bytes"`
	TxPackets             int64      `json:"tx_packets"`
	RxPackets             int64      `json:"rx_packets"`
	TxRetries             int64      `json:"tx_retries"`
	TxCombinedRetries     int64      `json:"tx_combined_retries"`
	TxRtsRetries          int64      `json:"tx_rts_retries"`
	WifiTxAttempts        int64      `json:"wifi_tx_attempts"`
	WifiTxDropped         int64      `json:"wifi_tx_dropped"`
	WifiTxSuccess         int64      `json:"wifi_tx_success"`
	WifiTxLatencyMov      *TxLatency `json:"wifi_tx_latency_mov"`
	TxTcpStats            *TcpStats  `json:"tx_tcp_stats"`
	RxTcpStats            *TcpStats  `json:"rx_tcp_stats"`
	Satisfaction          *int       `json:"satisfaction"`
	SatisfactionReal      *int       `json:"satisfaction_real"`
	SatisfactionSubscores []int      `json:"satisfaction_subscores"`
	Anomalies             int        `json:"anomalies"`
	Is11ax                bool       `json:"is_11ax"`
	Is11be                bool       `json:"is_11be"`
	Is11ac                bool       `json:"is_11ac"`
	Is11n                 bool       `json:"is_11n"`
	Is11r                 bool       `json:"is_11r"`
	IsMlo                 bool       `json:"is_mlo"`
	Mlo                   *MloInfo   `json:"mlo"`
	Authorized            bool       `json:"authorized"`
	BwMaxSupp             int        `json:"bw_max_supp"`
	Uptime                int64      `json:"uptime"`
	IdleTime              int64      `json:"idletime"`
	VlanID                int        `json:"vlan_id"`
	AuthTime              int64      `json:"auth_time"`
	DhcpStartTime         int64      `json:"dhcpstart_time"`
	DhcpEndTime           int64      `json:"dhcpend_time"`
	AnonClientID          string     `json:"anon_client_id"`
	TxPower               int        `json:"tx_power"`
	PowerSave             bool       `json:"state_pwrmgt"`

	// Vap is the parent entry's name and SnapshotAt is the pass that produced it, both filled in
	// during parsing. Band and channel live on the VAP, so a station record on its own cannot say
	// which radio it is on.
	Vap        string    `json:"-"`
	SnapshotAt time.Time `json:"-"`
}

// VapState is one vap_table entry. Channel, bandwidth, band, and SSID are only available here.
type VapState struct {
	Name            string    `json:"name"`
	Radio           string    `json:"radio"`
	RadioName       string    `json:"radio_name"`
	Band            string    `json:"band,omitempty"`
	Channel         int       `json:"channel"`
	Bandwidth       int       `json:"bw"`
	ExtChannel      int       `json:"extchannel,omitempty"`
	Essid           string    `json:"essid"`
	Bssid           string    `json:"bssid"`
	NumSta          int       `json:"num_sta"`
	TxPower         int       `json:"tx_power,omitempty"`
	State           string    `json:"state,omitempty"`
	Up              bool      `json:"up"`
	AvgClientSignal *int      `json:"avg_client_signal,omitempty"`
	Satisfaction    *int      `json:"satisfaction,omitempty"`
	MldName         string    `json:"mld_name,omitempty"`
	TxBytes         int64     `json:"tx_bytes"`
	RxBytes         int64     `json:"rx_bytes"`
	TxErrors        int64     `json:"tx_errors"`
	RxErrors        int64     `json:"rx_errors"`
	TxRetries       int64     `json:"tx_retries"`
	TxDropped       int64     `json:"tx_dropped"`
	CollectedAt     time.Time `json:"collected_at"`
}

// RadioState is one radio_table entry plus whatever counters the radio-stats tools contributed.
// IeeeModes is passed through as raw JSON: it is an opaque bitmask that reads as a number on
// measured firmware, and a type that drifts must not fail the whole parse.
type RadioState struct {
	Name           string           `json:"name"`
	Radio          string           `json:"radio"`
	Band           string           `json:"band,omitempty"`
	Channel        int              `json:"channel,omitempty"`
	Bandwidth      int              `json:"bw,omitempty"`
	Nss            int              `json:"nss,omitempty"`
	MaxTxPower     int              `json:"max_txpower,omitempty"`
	MinTxPower     int              `json:"min_txpower,omitempty"`
	AntGain        int              `json:"builtin_ant_gain,omitempty"`
	IeeeModes      json.RawMessage  `json:"ieee_modes,omitempty"`
	Is11ax         bool             `json:"is_11ax"`
	Is11be         bool             `json:"is_11be"`
	HasDfs         bool             `json:"has_dfs"`
	ScanRadio      bool             `json:"scan_radio,omitempty"`
	CounterOnly    bool             `json:"counter_only,omitempty"`
	NumSta         int              `json:"num_sta"`
	NoiseFloor     *int             `json:"noise_floor,omitempty"`
	Counters       map[string]int64 `json:"counters,omitempty"`
	Deltas         map[string]int64 `json:"counter_deltas,omitempty"`
	DeltaSeconds   float64          `json:"delta_seconds,omitempty"`
	CounterSources []string         `json:"counter_sources,omitempty"`
	CollectedAt    time.Time        `json:"collected_at"`
}

// McaSnapshot is one mca-dump pass, reduced to the fields this agent serves. The 142 KB document
// is never retained: only what is parsed out of it stays in memory.
type McaSnapshot struct {
	Version     string
	Model       string
	Hostname    string
	Vaps        []VapState
	Radios      []RadioState
	Stations    []StaSlow
	CollectedAt time.Time
}

type mcaRadioRaw struct {
	Name       string                     `json:"name"`
	Radio      string                     `json:"radio"`
	Nss        int                        `json:"nss"`
	MaxTxPower int                        `json:"max_txpower"`
	MinTxPower int                        `json:"min_txpower"`
	AntGain    int                        `json:"builtin_ant_gain"`
	IeeeModes  json.RawMessage            `json:"ieee_modes"`
	Is11ax     bool                       `json:"is_11ax"`
	Is11be     bool                       `json:"is_11be"`
	HasDfs     bool                       `json:"has_dfs"`
	Athstats   map[string]json.RawMessage `json:"athstats"`
}

type mcaVapRaw struct {
	VapState
	Sta []StaSlow `json:"sta_table"`
}

type mcaFull struct {
	Version    string         `json:"version"`
	Model      string         `json:"model"`
	Hostname   string         `json:"hostname"`
	RadioTable *[]mcaRadioRaw `json:"radio_table"`
	// ScanRadioTable is where the dedicated scan radio lives. It is NOT in radio_table, so a
	// discovery that reads only radio_table drops a real radio silently.
	ScanRadioTable *[]mcaRadioRaw `json:"scan_radio_table"`
	VapTable       *[]mcaVapRaw   `json:"vap_table"`
}

// bandForRadio maps mca-dump's radio token to a band label. The measured tokens are ng, na, and 6e;
// anything else yields no band rather than a guess.
func bandForRadio(radio string) string {
	switch strings.ToLower(strings.TrimSpace(radio)) {
	case "ng":
		return "2.4"
	case "na":
		return "5"
	case "6e", "ax6e", "6g":
		return "6"
	default:
		return ""
	}
}

// parseMcaFull parses the slow tier out of an mca-dump document. A missing radio_table is the
// shape failure, which is the same assertion the capability probe makes.
func parseMcaFull(data []byte, now time.Time) (McaSnapshot, error) {
	var raw mcaFull
	if err := json.Unmarshal(data, &raw); err != nil {
		return McaSnapshot{}, fmt.Errorf("parse mca-dump JSON: %w", err)
	}
	if raw.RadioTable == nil {
		return McaSnapshot{}, fmt.Errorf("mca-dump JSON has no radio_table")
	}

	snap := McaSnapshot{Version: raw.Version, Model: raw.Model, Hostname: raw.Hostname, CollectedAt: now}

	radioRows := append([]mcaRadioRaw(nil), *raw.RadioTable...)
	scanFrom := len(radioRows)
	if raw.ScanRadioTable != nil {
		radioRows = append(radioRows, *raw.ScanRadioTable...)
	}

	for i, r := range radioRows {
		radio := RadioState{
			Name: r.Name, Radio: r.Radio, Band: bandForRadio(r.Radio), Nss: r.Nss,
			MaxTxPower: r.MaxTxPower, MinTxPower: r.MinTxPower, AntGain: r.AntGain,
			IeeeModes: r.IeeeModes, Is11ax: r.Is11ax, Is11be: r.Is11be, HasDfs: r.HasDfs,
			ScanRadio:   i >= scanFrom,
			CollectedAt: now,
		}
		if len(r.Athstats) > 0 {
			radio.Counters = numericFields(r.Athstats)
			radio.CounterSources = []string{"mca-dump"}
			if nf, ok := radio.Counters["noise_floor"]; ok {
				v := int(nf)
				radio.NoiseFloor = &v
			}
		}
		snap.Radios = append(snap.Radios, radio)
	}

	if raw.VapTable != nil {
		for _, v := range *raw.VapTable {
			vap := v.VapState
			vap.Band = bandForRadio(vap.Radio)
			vap.CollectedAt = now
			snap.Vaps = append(snap.Vaps, vap)
			for _, s := range v.Sta {
				if s.MAC == "" {
					continue
				}
				s.MAC = normalizeMAC(s.MAC)
				if s.MldMAC != nil {
					m := normalizeMAC(*s.MldMAC)
					s.MldMAC = &m
				}
				s.Vap = vap.Name
				s.SnapshotAt = now
				snap.Stations = append(snap.Stations, s)
			}
		}
	}

	// A radio's channel and width are not on radio_table; take them from a VAP that is up on it.
	for i := range snap.Radios {
		for _, v := range snap.Vaps {
			if v.RadioName != snap.Radios[i].Name || !v.Up {
				continue
			}
			snap.Radios[i].Channel = v.Channel
			snap.Radios[i].Bandwidth = v.Bandwidth
			snap.Radios[i].NumSta += v.NumSta
		}
	}
	return snap, nil
}

// numericFields keeps only the numeric members of a raw JSON object. That is what lets the radio
// counter sets be taken as a union: the tools disagree about which counters exist and athstats
// mixes a name string in with them.
func numericFields(fields map[string]json.RawMessage) map[string]int64 {
	out := make(map[string]int64, len(fields))
	for k, v := range fields {
		var n json.Number
		if err := json.Unmarshal(v, &n); err != nil {
			continue
		}
		i, err := n.Int64()
		if err != nil {
			f, ferr := n.Float64()
			if ferr != nil {
				continue
			}
			i = int64(f)
		}
		out[k] = i
	}
	return out
}

// collectSlow runs one mca-dump pass. The timeout is generous against a measured ~300 ms cost; it
// exists so a wedged utility cannot stall the tier forever.
func collectSlow(ctx context.Context, now time.Time) (McaSnapshot, error) {
	out, err := runCommand(ctx, 20*time.Second, "mca-dump")
	if err != nil {
		return McaSnapshot{}, err
	}
	return parseMcaFull([]byte(out), now)
}
