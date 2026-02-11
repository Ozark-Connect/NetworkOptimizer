package speedtest

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"strings"
)

const (
	baseURL      = "https://speed.cloudflare.com"
	downloadPath = "__down?bytes="
	uploadPath   = "__up"
)

// FetchMetadata retrieves edge metadata from the Cloudflare /cdn-cgi/trace endpoint.
func FetchMetadata(ctx context.Context, client *http.Client) (*Metadata, error) {
	url := baseURL + "/cdn-cgi/trace"
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, fmt.Errorf("create trace request: %w", err)
	}
	req.Header.Set("User-Agent", "cfspeedtest/1.0")

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("fetch trace: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("trace returned HTTP %d", resp.StatusCode)
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf("read trace body: %w", err)
	}

	data := make(map[string]string)
	for _, line := range strings.Split(string(body), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		idx := strings.IndexByte(line, '=')
		if idx <= 0 {
			continue
		}
		key := strings.TrimSpace(line[:idx])
		val := strings.TrimSpace(line[idx+1:])
		data[key] = val
	}

	return &Metadata{
		IP:      data["ip"],
		Colo:    data["colo"],
		Country: data["loc"],
	}, nil
}
