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

Points keep the saved UTC `TestTime`. Tags are `test_type` (`wan` or `lan`), `runner` (`server`, `gateway`, or `client`), `provider` (`cloudflare`, `uwn`, `iperf3`, or `openspeedtest`), and either `wan_network_group` or LAN `target_host`. On-Site Agent runs use `server`. Fields include success, throughput in bps, available latency/jitter in ms, and descriptive names. WAN rates use the usual internet perspective; LAN download is device to NO, and upload is NO to device. Browser WAN results identify the external server and use the sole WAN group when known; multi-WAN sites omit the group. Successful WAN descriptions use `server_host`/`server_name`; LAN uses `target_name`/`target_type`. Failed results omit descriptions. Raw payloads and detailed client metadata stay in SQLite.

Client iperf3 results export immediately. Merges update the same point; enrichment updates LAN names/types without overwriting rates or changing browser WAN server identity. Browser WAN results record the sole WAN group at test time regardless of InfluxDB configuration; ambiguous or unavailable groups remain unset.

SQLite remains authoritative. Delivery uses the existing buffer and flush timer: errors are logged without failing tests, failed writes are dropped, and shutdown discards unflushed points (normally up to five seconds of data). There is no backfill, persistent retry queue, or synchronization of SQLite edits/deletions.

A Grafana Flux query for WAN download throughput (use the site's actual long-term bucket):

```flux
from(bucket: "network_monitoring_longterm")
  |> range(start: v.timeRangeStart, stop: v.timeRangeStop)
  |> filter(fn: (r) => r._measurement == "speed_test"
    and r.test_type == "wan" and r.runner != "client" and r._field == "download_bps")
```

## Provisioning InfluxDB

You don't hand-configure any of this. Point the app at an InfluxDB 2.x instance and the Monitoring page wizard provisions the buckets and mints a scoped token for you; your all-access token is never stored. If you need to stand an instance up, there's a ready-to-run compose file at `docker/influxdb/docker-compose.yml`, and the deployment guide covers the other paths (Proxmox, Homebrew, or a manual install).

## Packages

- InfluxDB.Client - official InfluxDB 2.x client, used by `MonitoringInfluxClient`
- Microsoft.EntityFrameworkCore.Sqlite - SQLite provider
- Microsoft.EntityFrameworkCore.Design - design-time tooling for migrations
- Microsoft.Extensions.Logging.Abstractions

Built for .NET 10 with nullable reference types and implicit usings enabled.
