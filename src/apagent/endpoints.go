package main

import (
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// maxEventReplay bounds one /events reply. A collector that has been away longer than the window
// gets the window plus the truncated flag, never an unbounded response.
const maxEventReplay = 2048

// ClientsPayload is GET /clients. Filters echoes what was applied, so an empty result can be told
// apart from a filter that matched nothing the caller expected.
type ClientsPayload struct {
	Ap          ApInfo            `json:"ap"`
	Filters     map[string]string `json:"filters,omitempty"`
	Count       int               `json:"count"`
	Clients     []Client          `json:"clients"`
	Sources     TierStatus        `json:"sources"`
	CollectedAt time.Time         `json:"collected_at"`
}

// ClientPayload is GET /client/<mac>.
type ClientPayload struct {
	Ap          ApInfo     `json:"ap"`
	Client      Client     `json:"client"`
	Sources     TierStatus `json:"sources"`
	CollectedAt time.Time  `json:"collected_at"`
}

// VapsPayload is GET /vaps. Band and channel live here rather than on a client record.
type VapsPayload struct {
	Ap          ApInfo     `json:"ap"`
	Count       int        `json:"count"`
	Vaps        []VapState `json:"vaps"`
	Sources     TierStatus `json:"sources"`
	CollectedAt time.Time  `json:"collected_at"`
}

// RadiosPayload is GET /radios. Counters are a union across the radio-stats tools, which do not
// agree on which counters exist.
type RadiosPayload struct {
	Ap          ApInfo       `json:"ap"`
	Count       int          `json:"count"`
	Radios      []RadioState `json:"radios"`
	Sources     TierStatus   `json:"sources"`
	CollectedAt time.Time    `json:"collected_at"`
}

// EventsPayload is GET /events?since=. AgentStartedAt is what tells a collector the sequence
// numbering restarted: the agent holds no state across a reboot, so seq begins at 1 again.
type EventsPayload struct {
	Ap             ApInfo    `json:"ap"`
	AgentStartedAt time.Time `json:"agent_started_at"`
	Since          string    `json:"since,omitempty"`
	Truncated      bool      `json:"truncated"`
	Count          int       `json:"count"`
	Events         []Event   `json:"events"`
	Ring           RingStats `json:"ring"`
	CollectedAt    time.Time `json:"collected_at"`
}

// httpError carries a status alongside the message so a handler can refuse a bad request without
// each one repeating the header work.
type httpError struct {
	status  int
	message string
}

func (e httpError) Error() string { return e.message }

func badRequest(format string, args ...any) error {
	return httpError{status: http.StatusBadRequest, message: fmt.Sprintf(format, args...)}
}

func notFound(format string, args ...any) error {
	return httpError{status: http.StatusNotFound, message: fmt.Sprintf(format, args...)}
}

// parseClientFilter reads the /clients query. An unrecognized parameter is refused rather than
// ignored: silently serving every client to a caller that asked for one band is the worse failure.
func parseClientFilter(r *http.Request) (ClientFilter, error) {
	f := ClientFilter{}
	for key, values := range r.URL.Query() {
		if len(values) == 0 {
			continue
		}
		v := strings.TrimSpace(values[0])
		switch strings.ToLower(key) {
		case "band":
			f.Band = v
		case "ap":
			f.Ap = v
		case "vap":
			f.Vap = v
		case "ssid":
			f.Ssid = v
		case "authorized":
			parsed, err := strconv.ParseBool(v)
			if err != nil {
				return ClientFilter{}, badRequest("authorized must be true or false, got %q", v)
			}
			f.Authorized = &parsed
		default:
			return ClientFilter{}, badRequest("unknown filter %q (band, ap, vap, ssid, authorized)", key)
		}
	}
	return f, nil
}

func (s *State) clientsPayload(r *http.Request) (any, error) {
	filter, err := parseClientFilter(r)
	if err != nil {
		return nil, err
	}
	table, _ := s.telemetry()
	now := time.Now().UTC()
	ap := table.Ap()

	matched := make([]Client, 0, 16)
	for _, c := range table.Clients(now) {
		if matchClient(c, filter, ap) {
			matched = append(matched, c)
		}
	}
	return ClientsPayload{
		Ap:          ap,
		Filters:     filter.Applied(),
		Count:       len(matched),
		Clients:     matched,
		Sources:     table.Tiers(),
		CollectedAt: now,
	}, nil
}

func (s *State) clientPayload(r *http.Request) (any, error) {
	mac := strings.Trim(strings.TrimPrefix(r.URL.Path, "/client/"), "/")
	if mac == "" {
		return nil, badRequest("no MAC in the path (/client/<mac>)")
	}
	table, _ := s.telemetry()
	now := time.Now().UTC()
	client, ok := FindClient(table.Clients(now), mac)
	if !ok {
		return nil, notFound("no client %s on this AP", mac)
	}
	return ClientPayload{
		Ap:          table.Ap(),
		Client:      client,
		Sources:     table.Tiers(),
		CollectedAt: now,
	}, nil
}

func (s *State) vapsPayload(*http.Request) (any, error) {
	table, _ := s.telemetry()
	vaps := table.Vaps()
	return VapsPayload{
		Ap:          table.Ap(),
		Count:       len(vaps),
		Vaps:        vaps,
		Sources:     table.Tiers(),
		CollectedAt: time.Now().UTC(),
	}, nil
}

func (s *State) radiosPayload(*http.Request) (any, error) {
	table, _ := s.telemetry()
	radios := table.Radios()
	return RadiosPayload{
		Ap:          table.Ap(),
		Count:       len(radios),
		Radios:      radios,
		Sources:     table.Tiers(),
		CollectedAt: time.Now().UTC(),
	}, nil
}

// eventsPayload serves the replay window. since accepts a sequence number or an RFC3339 timestamp:
// the sequence is what a collector should hold, and the timestamp is what a person types by hand.
func (s *State) eventsPayload(r *http.Request) (any, error) {
	table, ring := s.telemetry()
	since := strings.TrimSpace(r.URL.Query().Get("since"))

	var (
		events    []Event
		truncated bool
	)
	switch {
	case since == "":
		events, truncated = ring.Since(0)
	default:
		if seq, err := strconv.ParseUint(since, 10, 64); err == nil {
			events, truncated = ring.Since(seq)
			break
		}
		at, err := time.Parse(time.RFC3339, since)
		if err != nil {
			return nil, badRequest("since must be a sequence number or an RFC3339 timestamp, got %q", since)
		}
		events, truncated = ring.SinceTime(at.UTC())
	}

	if len(events) > maxEventReplay {
		events = events[len(events)-maxEventReplay:]
		truncated = true
	}
	if events == nil {
		events = []Event{}
	}
	return EventsPayload{
		Ap:             table.Ap(),
		AgentStartedAt: s.startedAt,
		Since:          since,
		Truncated:      truncated,
		Count:          len(events),
		Events:         events,
		Ring:           ring.Stats(),
		CollectedAt:    time.Now().UTC(),
	}, nil
}
