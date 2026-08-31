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
