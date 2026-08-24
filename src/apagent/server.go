package main

import (
	"crypto/subtle"
	"encoding/json"
	"log/slog"
	"net"
	"net/http"
	"os"
	"strings"
	"time"
)

func processID() int { return os.Getpid() }

// newServer wires the two phase 0 endpoints behind bearer authentication.
func newServer(state *State, token string) *http.Server {
	mux := http.NewServeMux()
	mux.HandleFunc("/capabilities", jsonHandler(func() any { return state.Capabilities() }))
	mux.HandleFunc("/health", jsonHandler(func() any { return state.Health() }))

	return &http.Server{
		Handler:           authMiddleware(state, token, mux),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
}

func jsonHandler(payload func() any) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			w.Header().Set("Allow", "GET, HEAD")
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		enc := json.NewEncoder(w)
		enc.SetIndent("", "  ")
		if err := enc.Encode(payload()); err != nil {
			slog.Error("failed to encode response", "path", r.URL.Path, "error", err)
		}
	}
}

// authMiddleware refuses every unauthenticated request. The payload is hostnames, IPs, MACs, and
// per-client traffic, so there is no unauthenticated path to open by accident.
func authMiddleware(state *State, token string, next http.Handler) http.Handler {
	want := []byte("Bearer " + token)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		state.counters.Requests.Add(1)

		got := []byte(r.Header.Get("Authorization"))
		if subtle.ConstantTimeCompare(got, want) != 1 {
			state.counters.AuthFailures.Add(1)
			w.Header().Set("WWW-Authenticate", `Bearer realm="apagent"`)
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			slog.Warn("unauthorized request", "path", r.URL.Path, "remote", remoteHost(r.RemoteAddr))
			return
		}
		next.ServeHTTP(w, r)
	})
}

func remoteHost(addr string) string {
	host, _, err := net.SplitHostPort(addr)
	if err != nil {
		return addr
	}
	return host
}

// resolveBindAddress turns the configured interface (or explicit address) into a bind address.
func resolveBindAddress(cfg *Config, ifaces []InterfaceInfo) (string, error) {
	if cfg.ListenAddress != "" {
		return cfg.ListenAddress, nil
	}
	return addressForInterface(ifaces, cfg.ListenInterface)
}

func isWildcardAddress(addr string) bool {
	return addr == "0.0.0.0" || addr == "::" || strings.EqualFold(addr, "[::]")
}
