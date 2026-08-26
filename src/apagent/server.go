package main

import (
	"bytes"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"os"
	"strings"
	"time"
)

func processID() int { return os.Getpid() }

// newMux registers every endpoint. Each one reads the in-memory table and never triggers a
// collection: N pollers would otherwise cost N times the collection.
func newMux(state *State) *http.ServeMux {
	mux := http.NewServeMux()
	mux.HandleFunc("/capabilities", jsonHandler(func() any { return state.Capabilities() }))
	mux.HandleFunc("/health", jsonHandler(func() any { return state.Health() }))
	mux.HandleFunc("/clients", jsonRequestHandler(state.clientsPayload))
	mux.HandleFunc("GET /clients/{mac}", jsonRequestHandler(state.clientPayload))
	mux.HandleFunc("/vaps", jsonRequestHandler(state.vapsPayload))
	mux.HandleFunc("/radios", jsonRequestHandler(state.radiosPayload))
	mux.HandleFunc("/events", jsonRequestHandler(state.eventsPayload))
	mux.HandleFunc("/neighbors", jsonRequestHandler(state.neighborsPayload))

	// The only route that changes anything. The resource is the transition request, not an action:
	// POST creates one for the client named in the path. A more specific pattern than "/clients/{mac}",
	// so it wins on precedence.
	mux.HandleFunc("POST /clients/{mac}/bss-transitions", jsonMutatingHandler(state.bssTransitionPayload))
	return mux
}

// newServer puts every endpoint behind bearer authentication.
func newServer(state *State, token string) *http.Server {
	return &http.Server{
		Handler:           authMiddleware(state, token, newMux(state)),
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       15 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
}

func jsonHandler(payload func() any) http.HandlerFunc {
	return jsonRequestHandler(func(*http.Request) (any, error) { return payload(), nil })
}

// jsonMutatingHandler is the read handler's contract for the one endpoint that changes something.
// POST only, so a stray GET or a link preview can never move a client.
func jsonMutatingHandler(payload func(*http.Request) (any, error)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			w.Header().Set("Allow", "POST")
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}
		body, err := payload(r)
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")

		if err != nil {
			status := http.StatusInternalServerError
			var he httpError
			if errors.As(err, &he) {
				status = he.status
			}
			w.WriteHeader(status)
			body = map[string]string{"error": err.Error()}
		}

		enc := json.NewEncoder(w)
		enc.SetIndent("", "  ")
		if err := enc.Encode(body); err != nil {
			slog.Error("failed to encode response", "path", r.URL.Path, "error", err)
		}
	}
}

// jsonRequestHandler is the same contract for endpoints that read the query or the path. A refusal
// carries a JSON body too, so a collector never has to parse an HTML error page.
func jsonRequestHandler(payload func(*http.Request) (any, error)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			w.Header().Set("Allow", "GET, HEAD")
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		body, err := payload(r)
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")

		if err != nil {
			status := http.StatusInternalServerError
			var he httpError
			if errors.As(err, &he) {
				status = he.status
			}
			w.WriteHeader(status)
			body = map[string]string{"error": err.Error()}
		}

		enc := json.NewEncoder(w)
		enc.SetIndent("", "  ")
		if err := enc.Encode(body); err != nil {
			slog.Error("failed to encode response", "path", r.URL.Path, "error", err)
		}
	}
}

// authMiddleware refuses every unauthenticated request. The payload is hostnames, IPs, MACs, and
// per-client traffic, so there is no unauthenticated path to open by accident.
func authMiddleware(state *State, token string, next http.Handler) http.Handler {
	want := []byte("Bearer " + token)
	nonces := newNonceStore()
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		state.counters.Requests.Add(1)

		header := r.Header.Get("Authorization")
		var reason error

		if strings.HasPrefix(header, "HMAC ") {
			// Read the body to sign over, then put it back for the handler.
			body, err := io.ReadAll(io.LimitReader(r.Body, maxSignedBody))
			r.Body.Close()
			r.Body = io.NopCloser(bytes.NewReader(body))
			if err != nil {
				reason = fmt.Errorf("unreadable body")
			} else {
				reason = verifyHmac(token, header, r.Method, r.URL.Path, body, nonces, time.Now())
			}
		} else if subtle.ConstantTimeCompare([]byte(header), want) != 1 {
			// Bearer still works so an agent keeps serving a server that has not been upgraded
			// yet. Remove it once no deployed server sends it - until then the token is only as
			// safe as the oldest caller.
			reason = fmt.Errorf("bearer mismatch")
		}

		if reason != nil {
			state.counters.AuthFailures.Add(1)
			w.Header().Set("WWW-Authenticate", `Bearer realm="apagent"`)
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			slog.Warn("unauthorized request", "path", r.URL.Path, "remote", remoteHost(r.RemoteAddr), "reason", reason)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// maxSignedBody caps what we will buffer to verify a signature. Every signed request this agent
// serves is a small JSON control message; anything larger is not ours.
const maxSignedBody = 1 << 20

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
