package main

import (
	"fmt"
	"strings"
	"testing"
	"time"
)

const testSecret = "a-per-agent-token"

func signedHeader(secret, method, path, nonce string, body []byte, at time.Time) string {
	ts := fmt.Sprintf("%d", at.Unix())
	return fmt.Sprintf("HMAC ts=%s,nonce=%s,sig=%s", ts, nonce,
		signature(secret, method, path, ts, nonce, body))
}

func TestSignedRequestIsAccepted(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	body := []byte(`{"candidates":[]}`)
	h := signedHeader(testSecret, "POST", "/clients/aa/bss-transitions", "n1", body, now)

	if err := verifyHmac(testSecret, h, "POST", "/clients/aa/bss-transitions", body, newNonceStore(), now); err != nil {
		t.Fatalf("a correctly signed request must be accepted, got %v", err)
	}
}

func TestSecretIsNeverOnTheWire(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	h := signedHeader(testSecret, "GET", "/clients", "n1", nil, now)

	// The whole point: capturing the header must not reveal the key.
	if strings.Contains(h, testSecret) {
		t.Fatal("the authorization header carried the secret itself")
	}
}

func TestTamperedRequestIsRejected(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	body := []byte(`{"duration":100}`)
	h := signedHeader(testSecret, "POST", "/clients/aa/bss-transitions", "n1", body, now)

	cases := []struct {
		name           string
		method, path   string
		body           []byte
		secret, header string
	}{
		{"path swapped", "POST", "/clients/bb/bss-transitions", body, testSecret, h},
		{"method swapped", "GET", "/clients/aa/bss-transitions", body, testSecret, h},
		{"body edited", "POST", "/clients/aa/bss-transitions", []byte(`{"duration":9999}`), testSecret, h},
		{"another agent's token", "POST", "/clients/aa/bss-transitions", body, "different-token", h},
		{"header not signed at all", "POST", "/clients/aa/bss-transitions", body, testSecret, "HMAC nonsense"},
	}
	for _, c := range cases {
		if err := verifyHmac(c.secret, c.header, c.method, c.path, c.body, newNonceStore(), now); err == nil {
			t.Errorf("%s: must be rejected, was accepted", c.name)
		}
	}
}

func TestReplayIsRejected(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	body := []byte(`{}`)
	h := signedHeader(testSecret, "POST", "/clients/aa/bss-transitions", "n1", body, now)
	store := newNonceStore()

	if err := verifyHmac(testSecret, h, "POST", "/clients/aa/bss-transitions", body, store, now); err != nil {
		t.Fatalf("first use must pass, got %v", err)
	}
	// Byte-identical capture, replayed a second later. This is the attack the nonce exists for.
	if err := verifyHmac(testSecret, h, "POST", "/clients/aa/bss-transitions", body, store, now.Add(time.Second)); err == nil {
		t.Fatal("a replayed request must be rejected")
	}
}

func TestStaleAndFutureTimestampsAreRejected(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	h := signedHeader(testSecret, "GET", "/clients", "n1", nil, now)

	for _, drift := range []time.Duration{hmacSkew + time.Minute, -(hmacSkew + time.Minute)} {
		if err := verifyHmac(testSecret, h, "GET", "/clients", nil, newNonceStore(), now.Add(drift)); err == nil {
			t.Errorf("drift %s must be rejected", drift)
		}
	}
}

func TestClockDriftInsideTheWindowIsAccepted(t *testing.T) {
	// An access point between NTP syncs is not an attacker.
	now := time.Unix(1_700_000_000, 0)
	h := signedHeader(testSecret, "GET", "/clients", "n1", nil, now)

	if err := verifyHmac(testSecret, h, "GET", "/clients", nil, newNonceStore(), now.Add(hmacSkew-time.Second)); err != nil {
		t.Fatalf("drift inside the window must be accepted, got %v", err)
	}
}

func TestNonceIsNotSpentByAnUnsignedAttempt(t *testing.T) {
	// Otherwise anyone could burn a nonce for a request they cannot sign, and the real one bounces.
	now := time.Unix(1_700_000_000, 0)
	body := []byte(`{}`)
	h := signedHeader(testSecret, "GET", "/clients", "n1", body, now)
	store := newNonceStore()

	_ = verifyHmac("wrong-token", h, "GET", "/clients", body, store, now)
	if err := verifyHmac(testSecret, h, "GET", "/clients", body, store, now); err != nil {
		t.Fatalf("the genuine request must still pass, got %v", err)
	}
}

func TestForgottenNoncesCannotOutliveTheSkewWindow(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	store := newNonceStore()
	store.use("n1", now)

	// Once a nonce is forgotten its timestamp is far outside the window, so verifyHmac rejects it
	// on age before it can be reused. Retention must therefore exceed the skew on both sides.
	if nonceRetention <= hmacSkew {
		t.Fatal("nonce retention must outlive the skew window, or a replay slips through the gap")
	}
}


// The vector below is duplicated verbatim in the server's ApAgentRequestSignerTests. Two languages
// build this string independently, so if either drifts they stop agreeing and every agent 401s.
func TestCanonicalFormMatchesTheServer(t *testing.T) {
	const want = "jIrgeEUstgz5okESBy5t4t/LVTSW2/Mcf1kecvFgfoo="
	got := signature("a-per-agent-token", "GET", "/clients", "1700000000", "n1", nil)
	if got != want {
		t.Fatalf("canonical form drifted from the server: got %s, want %s", got, want)
	}
}
