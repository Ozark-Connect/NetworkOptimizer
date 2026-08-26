package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"
)

// The agent serves plain HTTP on a management LAN, so a bearer token rides the wire in clear on
// every poll - once every three seconds per access point - and anything that reads it can steer or
// ban a client. Signing proves the caller holds the secret without ever sending it.
//
// Canonical string, newline separated: METHOD, path, timestamp, nonce, hex(sha256(body)).
const (
	// hmacSkew is how far a request's timestamp may sit from ours. Wide enough for an access point
	// whose clock drifts between NTP syncs, narrow enough that a captured request expires quickly.
	hmacSkew = 5 * time.Minute

	// nonceRetention outlives the skew window on both sides, so a replay cannot arrive after its
	// nonce has been forgotten but while its timestamp still passes.
	nonceRetention = 2 * hmacSkew

	// maxNonces bounds the store. Reaching it means something is flooding us; dropping the oldest
	// is better than growing without limit on a device with this little memory.
	maxNonces = 4096
)

// nonceStore remembers which nonces have been spent, so a captured request cannot be replayed.
type nonceStore struct {
	mu   sync.Mutex
	seen map[string]time.Time
}

func newNonceStore() *nonceStore { return &nonceStore{seen: make(map[string]time.Time, 64)} }

// use records a nonce and reports whether it was fresh. A repeat is a replay.
func (n *nonceStore) use(nonce string, now time.Time) bool {
	n.mu.Lock()
	defer n.mu.Unlock()

	for k, at := range n.seen {
		if now.Sub(at) > nonceRetention {
			delete(n.seen, k)
		}
	}
	if _, exists := n.seen[nonce]; exists {
		return false
	}
	if len(n.seen) >= maxNonces {
		return false
	}
	n.seen[nonce] = now
	return true
}

// signature is the canonical MAC for one request.
func signature(secret, method, path, ts, nonce string, body []byte) string {
	sum := sha256.Sum256(body)
	canonical := strings.Join([]string{method, path, ts, nonce, hex.EncodeToString(sum[:])}, "\n")
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(canonical))
	return base64.StdEncoding.EncodeToString(mac.Sum(nil))
}

// parseHmacHeader pulls ts, nonce and sig out of `HMAC ts=...,nonce=...,sig=...`.
func parseHmacHeader(header string) (ts, nonce, sig string, ok bool) {
	rest, found := strings.CutPrefix(header, "HMAC ")
	if !found {
		return "", "", "", false
	}
	for _, part := range strings.Split(rest, ",") {
		k, v, found := strings.Cut(strings.TrimSpace(part), "=")
		if !found {
			continue
		}
		switch k {
		case "ts":
			ts = v
		case "nonce":
			nonce = v
		case "sig":
			sig = v
		}
	}
	return ts, nonce, sig, ts != "" && nonce != "" && sig != ""
}

// verifyHmac checks a signed request. The error says which check failed, for the log only - the
// caller is told nothing beyond unauthorized.
func verifyHmac(secret, header, method, path string, body []byte, nonces *nonceStore, now time.Time) error {
	ts, nonce, sig, ok := parseHmacHeader(header)
	if !ok {
		return fmt.Errorf("malformed authorization header")
	}

	seconds, err := strconv.ParseInt(ts, 10, 64)
	if err != nil {
		return fmt.Errorf("unparsable timestamp")
	}
	if drift := now.Sub(time.Unix(seconds, 0)); drift > hmacSkew || drift < -hmacSkew {
		return fmt.Errorf("timestamp outside the accepted window")
	}

	want := signature(secret, method, path, ts, nonce, body)
	if subtle.ConstantTimeCompare([]byte(sig), []byte(want)) != 1 {
		return fmt.Errorf("signature mismatch")
	}

	// Last, so a replayed nonce is only spent once the signature has already proven the caller
	// holds the secret. Otherwise anyone could burn nonces for a client they cannot even sign for.
	if !nonces.use(nonce, now) {
		return fmt.Errorf("nonce already used")
	}
	return nil
}
