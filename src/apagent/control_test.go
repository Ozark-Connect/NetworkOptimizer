package main

import "testing"

func TestFtInKeyMgmt(t *testing.T) {
	// Shape verified against a live U7 get_config answer.
	ftConfig := "bssid=aa:bb:cc:dd:ee:f2\nssid=TestNet\nwps_state=disabled\nwpa=2\nkey_mgmt=FT-SAE SAE\ngroup_cipher=CCMP\nrsn_pairwise_cipher=CCMP\n"

	cases := []struct {
		name   string
		config string
		want   bool
	}{
		{"ft-sae alongside sae", ftConfig, true},
		{"ft-psk alone", "key_mgmt=FT-PSK\n", true},
		{"sae without ft", "key_mgmt=WPA-PSK SAE\n", false},
		{"no key_mgmt line", "bssid=aa:bb:cc:dd:ee:f2\nssid=TestNet\n", false},
		{"empty answer", "", false},
		// Only the key_mgmt line decides; FT- anywhere else must not count.
		{"ft in ssid only", "ssid=FT-Lounge\nkey_mgmt=SAE\n", false},
		{"ft not at akm start", "key_mgmt=WPA-EAP-FT-ISH\n", false},
	}

	for _, tc := range cases {
		if got := ftInKeyMgmt(tc.config); got != tc.want {
			t.Errorf("%s: ftInKeyMgmt = %v, want %v", tc.name, got, tc.want)
		}
	}
}

func TestStripOwnCandidates(t *testing.T) {
	own := ownMarks{
		elements: map[string]bool{"aabbccddeef2ffffffff645964": true},
		bssidHex: map[string]bool{"aabbccddeef2": true, "aabbccddeef4": true},
	}
	candidates := []string{
		"112233445566ffffffff785164", // another AP: kept
		"AABBCCDDEEF2FFFFFFFF645964", // own element, case-insensitive: dropped
		"aabbccddeef4ffffffff016564", // opens with an own BSSID: dropped
		"short",                      // too short for a prefix match: kept
	}

	kept := stripOwnCandidates(candidates, own)
	if len(kept) != 2 || kept[0] != candidates[0] || kept[1] != "short" {
		t.Errorf("stripOwnCandidates kept %v", kept)
	}
}

// A client that hops VAPs leaves a dead station entry holding the session's byte count; the live
// link must win on recency, not totals.
func TestBetterActivePrefersLowerIdle(t *testing.T) {
	stale := ClientLink{Vap: "wifi2ap10", IdleSeconds: 25, TxBytes: 9_000_000}
	live := ClientLink{Vap: "wifi1ap5", IdleSeconds: 1, TxBytes: 4_000}

	if !betterActive(live, stale) {
		t.Error("the low-idle link must beat the high-byte stale one")
	}
	if betterActive(stale, live) {
		t.Error("the stale link must not beat the live one")
	}

	// Equal idle falls through to byte totals as before.
	a := ClientLink{IdleSeconds: 2, TxBytes: 100}
	b := ClientLink{IdleSeconds: 2, TxBytes: 50}
	if !betterActive(a, b) {
		t.Error("equal idle must still prefer the higher byte total")
	}
}
