# Threat Intelligence Dashboard (feature/vigilance)

**Branch:** `feature/vigilance`
**Worktree:** `C:\Users\tjvc4\OneDrive\StartupProjects\NetworkOptimizer\feature-vigilance`
**Deploy:** Normal git push flow - `git push && ssh root@nas "cd /opt/network-optimizer && git pull && cd docker && docker compose build network-optimizer && docker compose up -d network-optimizer"`

## What's Built

Full threat intelligence dashboard at `/threats` with:

- **Overview tab:** Summary cards, threat timeline (stacked area chart by severity), kill chain distribution, blocked vs detected breakdown, top source IPs with CrowdSec CTI enrichment, top targeted ports, attack patterns
- **Exposure tab:** Port forward rules cross-referenced with threat data, geo-block recommendations (excluding already-blocked events)
- **Geographic tab:** Country distribution with flags and full names, filterable by action
- **Attack Sequences tab:** Multi-stage kill chain sequences per source IP
- **IP Drill-Down:** Click any IP to see all activity (as source and destination), peer groups, port ranges, top signatures, hourly timeline
- **Settings tab:** Links to `/settings#threat-intelligence`
- **Noise Filters:** Add/remove filters (source IP, dest IP, dest port, with CIDR support), toggle on/off (persisted), affects all data including timeline chart
- **CrowdSec CTI:** Reputation badges on top sources, manual lookup, negative hit caching (24h), positive hit caching (30d)
- **MaxMind GeoIP:** Country, city, ASN data with background backfill for existing events
- **Auto-refresh:** 30s polling, triggers collection cycle for faster backfill

## Key Architecture

- `NetworkOptimizer.Threats` project: models, collection service, analysis (kill chain classifier, scan sweep detector, exploit campaign detector, exposure validator, pattern analyzer)
- `NetworkOptimizer.Storage/Repositories/ThreatRepository.cs`: LINQ path (BaseQuery + ApplyNoiseFilters) for most queries, raw SQL path (BuildNoiseFilterSql) for timeline aggregation
- `ThreatDashboardService`: Scoped service bridging repository to UI, handles filter application, CTI enrichment
- `ThreatCollectionService`: Singleton background service polling UniFi gateway IPS/IDS events, 6-hour backfill chunks

## Known Issues / TODO

- **Scan sweep detection thresholds:** Just relaxed to 5+ ports in 6h window (was 10 in 1h). Includes AttemptedExploitation stage (blocked probes to sensitive ports). Pattern analysis window widened to 6h to match. Monitor for false positives.
- **CTI cache improvements from plan:** 30-day positive TTL, 24h negative TTL - implemented
- **Faster backfill from plan:** 6-hour chunks, 20 pages per cycle - implemented

## Chart Update Patterns (Hard-Won)

- Single-series charts (kill chain, blocked vs detected): `UpdateSeriesAsync(true)` works
- Multi-series charts (timeline stacked area): must use `RenderAsync()`
- Data lists for charts must be REPLACED (new reference), not mutated - Blazor parameter diffing uses reference equality
- `_hasLoadedOnce` flag prevents chart destruction on subsequent data reloads (only show spinner on first visit)
- Raw SQL timeline had a parameter ordering bug (backwards loop added params in reverse) - fixed in `GetTimelineAsync`
