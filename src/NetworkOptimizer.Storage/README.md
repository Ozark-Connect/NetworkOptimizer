# NetworkOptimizer.Storage

The storage layer. Two things live here: the SQLite database that holds everything persistent the app needs, and the InfluxDB client that handles time-series monitoring data.

SQLite is always in play. InfluxDB is optional - Monitoring and supported speed test exports use it when configured through the Monitoring page. Without an InfluxDB connection, speed tests continue to use SQLite alone.

## SQLite (EF Core)

`NetworkOptimizerDbContext` is the EF Core context, and it backs just about everything the app remembers: settings, audit history, speed test results, SQM baselines, agent configuration, and the monitoring metadata tables. Schema changes go through migrations in `Migrations/`, and those apply automatically on startup, so there's no manual `dotnet ef database update` step in normal operation.

Data access goes through the repositories in `Repositories/`, each behind an interface in `Interfaces/` - audit results, settings, UniFi data, modems, speed tests, SQM, agents, alerts, schedules, and so on. They're plain async EF Core repositories; nothing exotic.

The host application registers the context, its factory, and the repositories in its own composition root - see the Web project's `Program.cs` - and runs `db.Database.Migrate()` on startup, so there's no separate setup call to wire up.

## InfluxDB time-series (MonitoringInfluxClient)

`MonitoringInfluxClient` owns the InfluxDB connection for the monitoring subsystem. It builds itself lazily from the connection details the user saves on the Monitoring page (URL, org, bucket, token), which live in the `MonitoringSettings` row, and it reconfigures on the fly when those settings change. The token is stored encrypted and only decrypted through `ICredentialProtectionService`, so it never sits in plaintext.

Writes are buffered and flushed on a timer, or sooner when the buffer fills. That keeps high-frequency metric writes from hammering the database one point at a time. The client also exposes the read side the monitoring charts pull from, plus a health check the UI leans on to tell you whether the connection is actually good - it runs a small query against the buckets rather than just pinging the server, because a reachable server with a revoked or mis-scoped token will happily fail every write while looking "up."

This is the only thing in the project that touches `InfluxDB.Client`, which is why the package lives here.

### Speed test export

Saved WAN and LAN speed tests, including browser and client-initiated iperf3 results, automatically export to the `speed_test` measurement when InfluxDB is configured. Manual and scheduled runs use the same paths; unsaved ephemeral probes are excluded. There is no separate toggle, and disabling monitoring collection does not disable export. Each site's client writes to its existing long-term bucket (365 days by default), falling back to the primary bucket when unset. Export does not change retention.

Points use the saved UTC `TestTime`, with `test_type`, `direction`, and either `wan_network_group` or LAN `target_host` as tags. Fields hold the result ID, success, throughput in bps, per-direction duration, stream count, available latency/jitter in ms, and descriptive names. WAN download/upload have their usual internet meaning; LAN download is from the device to NO, and upload is from NO to the device. Missing latency is omitted, and success follows the saved result, including single-direction successes. Browser WAN tests identify the external server rather than exporting the client's address; an unresolved WAN is `unknown`. Raw payloads, topology, MACs, source IPs, user agents, geolocation, notes, and error text stay in SQLite. Blank LAN targets are skipped with a warning; missing WAN server hosts are omitted.

Client iperf3 results export immediately. A merged direction updates the same point using unchanged tags and timestamp. After client enrichment saves, a metadata-only update adds available names/types without overwriting measurements; blank metadata and later Wi-Fi retries produce no update.

SQLite remains authoritative. Delivery uses the existing buffer and flush timer: errors are logged without failing tests, failed writes are dropped, and shutdown discards unflushed points (normally up to five seconds of data). There is no backfill, persistent retry queue, or synchronization of SQLite edits/deletions.

A Grafana Flux query for WAN download throughput (use the site's actual long-term bucket):

```flux
from(bucket: "network_monitoring_longterm")
  |> range(start: v.timeRangeStart, stop: v.timeRangeStop)
  |> filter(fn: (r) => r._measurement == "speed_test"
    and r.test_type == "wan" and r._field == "download_bps")
```

## Provisioning InfluxDB

You don't hand-configure any of this. Point the app at an InfluxDB 2.x instance and the Monitoring page wizard provisions the buckets and mints a scoped token for you; your all-access token is never stored. If you need to stand an instance up, there's a ready-to-run compose file at `docker/influxdb/docker-compose.yml`, and the deployment guide covers the other paths (Proxmox, Homebrew, or a manual install).

## Packages

- InfluxDB.Client - official InfluxDB 2.x client, used by `MonitoringInfluxClient`
- Microsoft.EntityFrameworkCore.Sqlite - SQLite provider
- Microsoft.EntityFrameworkCore.Design - design-time tooling for migrations
- Microsoft.Extensions.Logging.Abstractions

Built for .NET 10 with nullable reference types and implicit usings enabled.
