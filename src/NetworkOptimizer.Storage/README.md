# NetworkOptimizer.Storage

The storage layer. Two things live here: the SQLite database that holds everything persistent the app needs, and the InfluxDB client that handles time-series monitoring data.

SQLite is always in play. InfluxDB is optional - it's only used by the Monitoring feature, so if you never open the Monitoring page you never need an InfluxDB instance at all.

## SQLite (EF Core)

`NetworkOptimizerDbContext` is the EF Core context, and it backs just about everything the app remembers: settings, audit history, speed test results, SQM baselines, agent configuration, and the monitoring metadata tables. Schema changes go through migrations in `Migrations/`, and those apply automatically on startup, so there's no manual `dotnet ef database update` step in normal operation.

Data access goes through the repositories in `Repositories/`, each behind an interface in `Interfaces/` - audit results, settings, UniFi data, modems, speed tests, SQM, agents, alerts, schedules, and so on. They're plain async EF Core repositories; nothing exotic.

The host application registers the context, its factory, and the repositories in its own composition root - see the Web project's `Program.cs` - and runs `db.Database.Migrate()` on startup, so there's no separate setup call to wire up.

## InfluxDB time-series (MonitoringInfluxClient)

`MonitoringInfluxClient` owns the InfluxDB connection for the monitoring subsystem. It builds itself lazily from the connection details the user saves on the Monitoring page (URL, org, bucket, token), which live in the `MonitoringSettings` row, and it reconfigures on the fly when those settings change. The token is stored encrypted and only decrypted through `ICredentialProtectionService`, so it never sits in plaintext.

Writes are buffered and flushed on a timer, or sooner when the buffer fills. That keeps high-frequency metric writes from hammering the database one point at a time. The client also exposes the read side the monitoring charts pull from, plus a health check the UI leans on to tell you whether the connection is actually good - it runs a small query against the buckets rather than just pinging the server, because a reachable server with a revoked or mis-scoped token will happily fail every write while looking "up."

This is the only thing in the project that touches `InfluxDB.Client`, which is why the package lives here.

## Provisioning InfluxDB

You don't hand-configure any of this. Point the app at an InfluxDB 2.x instance and the Monitoring page wizard provisions the buckets and mints a scoped token for you; your all-access token is never stored. If you need to stand an instance up, there's a ready-to-run compose file at `docker/influxdb/docker-compose.yml`, and the deployment guide covers the other paths (Proxmox, Homebrew, or a manual install).

## Packages

- InfluxDB.Client - official InfluxDB 2.x client, used by `MonitoringInfluxClient`
- Microsoft.EntityFrameworkCore.Sqlite - SQLite provider
- Microsoft.EntityFrameworkCore.Design - design-time tooling for migrations
- Microsoft.Extensions.Logging.Abstractions

Built for .NET 10 with nullable reference types and implicit usings enabled.
