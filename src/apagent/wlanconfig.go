package main

import (
	"context"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// StaFast is one station as the fast tier sees it: RF metrics only, no identity. Rates are kbps
// everywhere in this agent so nothing downstream has to guess which unit a field carries.
type StaFast struct {
	MAC          string    `json:"mac"`
	Vap          string    `json:"vap"`
	Channel      int       `json:"channel,omitempty"`
	TxRateKbps   int64     `json:"tx_rate_kbps,omitempty"`
	RxRateKbps   int64     `json:"rx_rate_kbps,omitempty"`
	Signal       *int      `json:"signal,omitempty"`
	MinSignal    *int      `json:"min_signal,omitempty"`
	MaxSignal    *int      `json:"max_signal,omitempty"`
	SNR          *int      `json:"snr,omitempty"`
	IdleSeconds  int       `json:"idle_seconds"`
	AssocSeconds int       `json:"assoc_seconds,omitempty"`
	Mode         string    `json:"mode,omitempty"`
	TxNss        int       `json:"tx_nss,omitempty"`
	RxNss        int       `json:"rx_nss,omitempty"`
	PsMode       string    `json:"ps_mode,omitempty"`
	CollectedAt  time.Time `json:"collected_at"`
}

var macPattern = regexp.MustCompile(`^[0-9a-fA-F]{2}(:[0-9a-fA-F]{2}){5}$`)

func isMAC(s string) bool { return macPattern.MatchString(s) }

// normalizeMAC lowercases a MAC so an address from wlanconfig and the same address from mca-dump
// land on one table key.
func normalizeMAC(s string) string { return strings.ToLower(strings.TrimSpace(s)) }

// parseRateKbps turns a wlanconfig rate token ("1200M", "6.5M", "0") into kbps. An unparsable
// token yields 0 rather than an error: one bad column must not discard the whole row.
func parseRateKbps(tok string) int64 {
	tok = strings.TrimSpace(tok)
	if tok == "" || tok == "-" {
		return 0
	}
	mult := 1000.0
	switch {
	case strings.HasSuffix(tok, "G"), strings.HasSuffix(tok, "g"):
		mult, tok = 1000000.0, tok[:len(tok)-1]
	case strings.HasSuffix(tok, "M"), strings.HasSuffix(tok, "m"):
		mult, tok = 1000.0, tok[:len(tok)-1]
	case strings.HasSuffix(tok, "K"), strings.HasSuffix(tok, "k"):
		mult, tok = 1.0, tok[:len(tok)-1]
	}
	v, err := strconv.ParseFloat(tok, 64)
	if err != nil {
		return 0
	}
	return int64(v*mult + 0.5)
}

// parseAssocTime turns wlanconfig's ASSOCTIME ("00:12:34" or "12:34") into seconds.
func parseAssocTime(tok string) int {
	parts := strings.Split(strings.TrimSpace(tok), ":")
	if len(parts) < 2 || len(parts) > 4 {
		return 0
	}
	total := 0
	for _, p := range parts {
		v, err := strconv.Atoi(p)
		if err != nil {
			return 0
		}
		total = total*60 + v
	}
	return total
}

func atoiOrZero(s string) int {
	v, err := strconv.Atoi(strings.TrimSpace(s))
	if err != nil {
		return 0
	}
	return v
}

// signalOrSNR splits wlanconfig's RSSI columns by sign. The column reads dBm on measured firmware
// (-54) but the same name carries SNR above the noise floor on other builds, and both are plausible
// integers, so the sign decides which field it lands in rather than an assumption.
func signalOrSNR(tok string) (signal *int, snr *int) {
	v, err := strconv.Atoi(strings.TrimSpace(tok))
	if err != nil || v == 0 {
		return nil, nil
	}
	if v < 0 {
		return &v, nil
	}
	return nil, &v
}

// raggedColumn is the one column whose value holds space-separated tokens ("RSN WME"), so the
// measured header has 25 names while its data row splits into 26 fields. Everything after it
// shifts left under a naive split, which silently misaligns MODE, RXNSS, TXNSS, and PSMODE.
const raggedColumn = "IEs"

// mapRow assigns fields to header columns around the ragged column: the columns before it are
// fixed-arity and map left-to-right, the columns after it are fixed-arity and map right-to-left,
// and whatever is left in the middle is the ragged column's value. Character offsets cannot be
// used instead: the measured row drifts 16 characters right of its header by the last column.
func mapRow(header, fields []string) map[string]string {
	out := make(map[string]string, len(header))
	ragged := -1
	for i, name := range header {
		if strings.EqualFold(name, raggedColumn) {
			ragged = i
			break
		}
	}

	// With no ragged column, or a row too short to straddle it, index mapping is all there is.
	if ragged < 0 || len(fields) <= ragged || len(fields) < len(header) {
		for i, name := range header {
			if i < len(fields) {
				out[name] = fields[i]
			}
		}
		return out
	}

	for i := 0; i < ragged; i++ {
		out[header[i]] = fields[i]
	}
	suffix := len(header) - ragged - 1
	tail := len(fields) - suffix
	for i := 0; i < suffix; i++ {
		out[header[ragged+1+i]] = fields[tail+i]
	}
	out[header[ragged]] = strings.Join(fields[ragged:tail], " ")
	return out
}

// parseWlanconfigStations parses `wlanconfig <vap> list sta`. Columns are mapped by header name,
// never by fixed index: the measured header is 25 columns and the set drifts between firmwares.
func parseWlanconfigStations(vap, out string, now time.Time) []StaFast {
	header, ok := parseWlanconfigHeader(out)
	if !ok {
		return nil
	}

	stations := make([]StaFast, 0, 8)
	for _, line := range strings.Split(out, "\n") {
		fields := strings.Fields(line)
		// Only a row that begins with a MAC is a station. wlanconfig also emits a wrapped
		// continuation of the capability flags and a per-station detail block, both indented.
		if len(fields) < 2 || !isMAC(fields[0]) {
			continue
		}
		row := mapRow(header, fields)

		s := StaFast{
			MAC:          normalizeMAC(fields[0]),
			Vap:          vap,
			Channel:      atoiOrZero(row["CHAN"]),
			TxRateKbps:   parseRateKbps(row["TXRATE"]),
			RxRateKbps:   parseRateKbps(row["RXRATE"]),
			IdleSeconds:  atoiOrZero(row["IDLE"]),
			AssocSeconds: parseAssocTime(row["ASSOCTIME"]),
			Mode:         row["MODE"],
			TxNss:        atoiOrZero(row["TXNSS"]),
			RxNss:        atoiOrZero(row["RXNSS"]),
			PsMode:       row["PSMODE"],
			CollectedAt:  now,
		}
		s.Signal, s.SNR = signalOrSNR(row["RSSI"])
		s.MinSignal, _ = signalOrSNR(row["MINRSSI"])
		s.MaxSignal, _ = signalOrSNR(row["MAXRSSI"])
		stations = append(stations, s)
	}
	return stations
}

// collectFast sweeps every VAP. One VAP failing must not lose the others, so the sweep returns
// which VAPs actually answered: a member on a VAP nothing answered for must not be expired as if
// it had gone away.
func collectFast(ctx context.Context, vaps []string, now time.Time) (map[string]StaFast, map[string]bool) {
	out := make(map[string]StaFast, 16)
	covered := make(map[string]bool, len(vaps))
	for _, vap := range vaps {
		text, err := runCommand(ctx, 5*time.Second, "wlanconfig", vap, "list", "sta")
		if err != nil {
			continue
		}
		covered[vap] = true
		for _, s := range parseWlanconfigStations(vap, text, now) {
			out[stationKey(s.Vap, s.MAC)] = s
		}
	}
	return out, covered
}

// stationKey identifies one link. An MLO client holds several, one per VAP.
func stationKey(vap, mac string) string { return vap + "/" + normalizeMAC(mac) }
