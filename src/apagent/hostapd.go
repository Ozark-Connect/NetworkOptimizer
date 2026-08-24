package main

import (
	"fmt"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"time"
)

// hostapdGlobalSocket is the control socket hostapd keeps for itself; it is not a VAP.
const hostapdGlobalSocket = "global"

// filterVapNames turns the contents of the hostapd control directory into VAP names.
// VAP names differ per AP (wifi2ap10 on one, wifi2ap11 on another, plus vwireap* mesh VAPs),
// so they are always discovered and never hardcoded.
func filterVapNames(entries []string) []string {
	vaps := make([]string, 0, len(entries))
	for _, e := range entries {
		name := strings.TrimSpace(e)
		if name == "" || name == hostapdGlobalSocket || strings.HasPrefix(name, ".") {
			continue
		}
		vaps = append(vaps, name)
	}
	sort.Strings(vaps)
	return vaps
}

// discoverVaps enumerates the hostapd control socket directory.
func discoverVaps(dir string) ([]string, error) {
	items, err := os.ReadDir(dir)
	if err != nil {
		return nil, fmt.Errorf("read %s: %w", dir, err)
	}
	names := make([]string, 0, len(items))
	for _, it := range items {
		names = append(names, it.Name())
	}
	return filterVapNames(names), nil
}

var radioFromVap = regexp.MustCompile(`^(wifi\d+)ap\d+$`)

// radiosFromVaps derives radio interface names from VAP names. Mesh VAPs (vwireap*) carry no
// radio prefix and contribute nothing, so radio discovery also folds in mca-dump's radio_table.
func radiosFromVaps(vaps []string) []string {
	seen := map[string]bool{}
	radios := make([]string, 0, 4)
	for _, v := range vaps {
		m := radioFromVap.FindStringSubmatch(v)
		if m == nil || seen[m[1]] {
			continue
		}
		seen[m[1]] = true
		radios = append(radios, m[1])
	}
	sort.Strings(radios)
	return radios
}

func mergeRadios(a, b []string) []string {
	seen := map[string]bool{}
	out := make([]string, 0, len(a)+len(b))
	for _, s := range append(append([]string{}, a...), b...) {
		s = strings.TrimSpace(s)
		if s == "" || seen[s] {
			continue
		}
		seen[s] = true
		out = append(out, s)
	}
	sort.Strings(out)
	return out
}

// pingHostapd opens the VAP's control socket and round-trips PING/PONG. hostapd's control
// interface is a unix datagram socket, so the client must bind a local socket of its own.
func pingHostapd(dir, vap string, timeout time.Duration) (string, error) {
	remote := filepath.Join(dir, vap)
	local := filepath.Join(os.TempDir(), fmt.Sprintf("apagent-ctrl-%d-%s", os.Getpid(), vap))

	_ = os.Remove(local)
	conn, err := net.DialUnix("unixgram",
		&net.UnixAddr{Name: local, Net: "unixgram"},
		&net.UnixAddr{Name: remote, Net: "unixgram"})
	if err != nil {
		return "", fmt.Errorf("dial %s: %w", remote, err)
	}
	defer func() {
		conn.Close()
		os.Remove(local)
	}()

	if err := conn.SetDeadline(time.Now().Add(timeout)); err != nil {
		return "", err
	}
	if _, err := conn.Write([]byte("PING")); err != nil {
		return "", fmt.Errorf("write PING to %s: %w", remote, err)
	}
	buf := make([]byte, 256)
	n, err := conn.Read(buf)
	if err != nil {
		return "", fmt.Errorf("read from %s: %w", remote, err)
	}
	return strings.TrimSpace(string(buf[:n])), nil
}
