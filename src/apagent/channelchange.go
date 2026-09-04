package main

import "time"

// channelChanges compares two radio tables and reports each serving radio whose channel, width,
// or block moved. A radio that was not in the previous table, or had no channel on either side
// (no VAP up), is not a change: there is nothing it moved from. Scan radios hop by design and
// counter-only rows carry no channel, so neither is reported.
func channelChanges(prev, cur []RadioState, now time.Time) []Event {
	before := make(map[string]RadioState, len(prev))
	for _, r := range prev {
		before[r.Name] = r
	}

	var events []Event
	for _, r := range cur {
		if r.ScanRadio || r.CounterOnly || r.Channel == 0 {
			continue
		}
		p, ok := before[r.Name]
		if !ok || p.Channel == 0 {
			continue
		}
		if p.Channel == r.Channel && p.Bandwidth == r.Bandwidth && (p.CenterMhz == r.CenterMhz || p.CenterMhz == 0 || r.CenterMhz == 0) {
			continue
		}
		events = append(events, Event{
			Type:  EventChannelChange,
			Radio: r.Name,
			Channel: &ChannelChange{
				Band:          r.Band,
				FromChannel:   p.Channel,
				FromBw:        p.Bandwidth,
				FromCenterMhz: p.CenterMhz,
				ToChannel:     r.Channel,
				ToBw:          r.Bandwidth,
				ToCenterMhz:   r.CenterMhz,
			},
			CollectedAt: now,
		})
	}
	return events
}
