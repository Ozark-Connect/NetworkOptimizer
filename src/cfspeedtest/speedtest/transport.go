package speedtest

import (
	"crypto/tls"
	"fmt"
	"net"
	"net/http"
	"time"
)

// newTransport creates an HTTP transport that forces HTTP/1.1 (separate TCP
// connections per worker) and optionally binds to a specific network interface.
func newTransport(ifaceName string) (*http.Transport, error) {
	t := &http.Transport{
		ForceAttemptHTTP2:   false,
		MaxIdleConnsPerHost: 1,
		TLSNextProto:       make(map[string]func(string, *tls.Conn) http.RoundTripper),
	}

	if ifaceName != "" {
		localAddr, err := resolveInterfaceAddr(ifaceName)
		if err != nil {
			return nil, err
		}
		dialer := &net.Dialer{
			LocalAddr: localAddr,
			Timeout:   30 * time.Second,
		}
		t.DialContext = dialer.DialContext
	}

	return t, nil
}

// NewClient creates an HTTP client bound to the configured interface (if any).
// Used for metadata and latency phases which share a single client.
func NewClient(cfg Config, timeout time.Duration) (*http.Client, error) {
	t, err := newTransport(cfg.Interface)
	if err != nil {
		return nil, err
	}
	return &http.Client{
		Timeout:   timeout,
		Transport: t,
	}, nil
}

// resolveInterfaceAddr finds the first IPv4 address on the named interface
// and returns a TCP address suitable for net.Dialer.LocalAddr.
func resolveInterfaceAddr(name string) (*net.TCPAddr, error) {
	iface, err := net.InterfaceByName(name)
	if err != nil {
		return nil, fmt.Errorf("interface %q: %w", name, err)
	}

	addrs, err := iface.Addrs()
	if err != nil {
		return nil, fmt.Errorf("interface %q addrs: %w", name, err)
	}

	for _, addr := range addrs {
		var ip net.IP
		switch v := addr.(type) {
		case *net.IPNet:
			ip = v.IP
		case *net.IPAddr:
			ip = v.IP
		}
		if ip == nil || ip.To4() == nil {
			continue // skip IPv6 and nil
		}
		return &net.TCPAddr{IP: ip}, nil
	}

	return nil, fmt.Errorf("interface %q has no IPv4 address", name)
}
