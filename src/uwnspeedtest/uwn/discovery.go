package uwn

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"net/http"
	"sort"
	"time"
)

const (
	tokenURL     = "https://sp-dir.uwn.com/api/v1/tokens"
	serversURL   = "https://sp-dir.uwn.com/api/v2/servers"
	ipInfoURL    = "https://sp-dir.uwn.com/api/v1/ip"
	userAgent    = "ui-speed-linux-arm64/1.3.4"
	pingAttempts = 3
	pingTimeout  = 3 * time.Second // per-ping timeout
)

// IpInfo holds the external IP and ISP information from the UWN API.
type IpInfo struct {
	IP  string  `json:"ip"`
	ISP string  `json:"isp"`
	Lat float64 `json:"lat"`
	Lon float64 `json:"lon"`
}

// FetchIpInfo retrieves external IP and ISP information from the UWN API.
func FetchIpInfo(ctx context.Context, client *http.Client) (*IpInfo, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, ipInfoURL, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", userAgent)

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("ip info returned HTTP %d", resp.StatusCode)
	}

	var info IpInfo
	if err := json.NewDecoder(resp.Body).Decode(&info); err != nil {
		return nil, err
	}
	return &info, nil
}

// FetchToken acquires a test token from the UWN directory service.
func FetchToken(ctx context.Context, client *http.Client) (string, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, tokenURL, nil)
	if err != nil {
		return "", fmt.Errorf("create token request: %w", err)
	}
	req.Header.Set("User-Agent", userAgent)

	resp, err := client.Do(req)
	if err != nil {
		return "", fmt.Errorf("fetch token: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("token endpoint returned HTTP %d", resp.StatusCode)
	}

	var tok tokenResponse
	if err := json.NewDecoder(resp.Body).Decode(&tok); err != nil {
		return "", fmt.Errorf("decode token: %w", err)
	}
	if tok.Token == "" {
		return "", fmt.Errorf("empty token returned")
	}

	return tok.Token, nil
}

// DiscoverServers fetches the list of available speed test servers.
func DiscoverServers(ctx context.Context, client *http.Client) ([]Server, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, serversURL, nil)
	if err != nil {
		return nil, fmt.Errorf("create servers request: %w", err)
	}
	req.Header.Set("User-Agent", userAgent)

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("fetch servers: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("servers endpoint returned HTTP %d", resp.StatusCode)
	}

	var servers []Server
	if err := json.NewDecoder(resp.Body).Decode(&servers); err != nil {
		return nil, fmt.Errorf("decode servers: %w", err)
	}

	return servers, nil
}

// SelectServers sorts candidates by geo distance to estimate proximity,
// pings all candidates with a short timeout, and returns the best N by RTT.
func SelectServers(ctx context.Context, client *http.Client, token string, candidates []Server, count int, clientLat, clientLon float64) ([]Server, error) {
	if len(candidates) == 0 {
		return nil, fmt.Errorf("no candidate servers")
	}

	// Sort by geographic distance if we have client coordinates
	if clientLat != 0 || clientLon != 0 {
		sort.Slice(candidates, func(i, j int) bool {
			di := haversine(clientLat, clientLon, candidates[i].Lat, candidates[i].Lon)
			dj := haversine(clientLat, clientLon, candidates[j].Lat, candidates[j].Lon)
			return di < dj
		})
	}

	// Ping nearest candidates by geo distance (at least count+2 to have spares)
	pingCount := count + 2
	if pingCount < 10 {
		pingCount = 10
	}
	if pingCount > len(candidates) {
		pingCount = len(candidates)
	}
	var pinged []Server
	for i := 0; i < pingCount; i++ {
		s := candidates[i]
		latency, err := pingServer(ctx, client, s.URL, token)
		if err != nil {
			continue // skip unreachable servers
		}
		s.LatencyMs = latency
		pinged = append(pinged, s)
	}

	if len(pinged) == 0 {
		return nil, fmt.Errorf("no servers responded to ping")
	}

	// Sort by RTT and return best N
	sort.Slice(pinged, func(i, j int) bool {
		return pinged[i].LatencyMs < pinged[j].LatencyMs
	})

	if count > len(pinged) {
		count = len(pinged)
	}
	return pinged[:count], nil
}

// pingServer sends a few pings to a server with a short per-request timeout
// and returns the minimum RTT.
func pingServer(ctx context.Context, client *http.Client, serverURL, token string) (float64, error) {
	pingURL := serverURL + "/ping"

	var minRTT float64 = math.MaxFloat64
	for i := 0; i < pingAttempts; i++ {
		pingCtx, cancel := context.WithTimeout(ctx, pingTimeout)
		req, err := http.NewRequestWithContext(pingCtx, http.MethodGet, pingURL, nil)
		if err != nil {
			cancel()
			return 0, err
		}
		req.Header.Set("User-Agent", userAgent)
		req.Header.Set("x-test-token", token)

		start := time.Now()
		resp, err := client.Do(req)
		rtt := time.Since(start).Seconds() * 1000
		cancel()
		if err != nil {
			continue
		}
		io.Copy(io.Discard, resp.Body)
		resp.Body.Close()

		if resp.StatusCode != http.StatusOK {
			continue
		}

		if rtt < minRTT {
			minRTT = rtt
		}
	}

	if minRTT == math.MaxFloat64 {
		return 0, fmt.Errorf("all pings failed")
	}
	return minRTT, nil
}

// haversine computes the great-circle distance in km between two points.
func haversine(lat1, lon1, lat2, lon2 float64) float64 {
	const earthRadiusKm = 6371.0
	dLat := (lat2 - lat1) * math.Pi / 180
	dLon := (lon2 - lon1) * math.Pi / 180
	lat1Rad := lat1 * math.Pi / 180
	lat2Rad := lat2 * math.Pi / 180

	a := math.Sin(dLat/2)*math.Sin(dLat/2) +
		math.Cos(lat1Rad)*math.Cos(lat2Rad)*math.Sin(dLon/2)*math.Sin(dLon/2)
	c := 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
	return earthRadiusKm * c
}
