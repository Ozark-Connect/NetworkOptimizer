# Adaptive SQM - Architecture

Adaptive SQM keeps a UniFi gateway's WAN shaper just below what the line can deliver right now. It
does not build queues. UniFi Smart Queues creates the HTB tree and the fq_codel leaves, and Adaptive
SQM rewrites their rates and tunes their parameters.

The runtime is shell. The app generates three scripts per WAN, deploys them over SSH, reads their
logs and a small HTTP status endpoint, and learns a per-WAN congestion curve. After a deploy the
gateway shapes on its own, with no dependency on the app being up.

```
 Network Optimizer (app)                              UniFi gateway
 ─────────────────────────                            ─────────────────────────────────────────
 Adaptive SQM page                                     /data/on_boot.d/20-sqm-<wan>.sh  (udm-boot)
   CreateSqmConfiguration ─┐                              │ writes, schedules, installs
                           ▼                              ▼
 SqmConfiguration ──► ScriptGenerator ──SFTP──►  /data/sqm/<wan>-speedtest.sh   2x daily (cron)
   ConnectionProfile      (boot script)            /data/sqm/<wan>-ping.sh        every minute (cron)
   LearnedCongestionProfile                        /data/sqm/<wan>-result.txt     last calibrated rate
                                                    /data/network-optimizer/bin/speedtest  (Ookla CLI)
                                                          │ tc class change / tc qdisc change
                                                          ▼
                                                    ifb<wan> (download)   <wan> (upload)
                                                    HTB 1:1 root + child classes + fq_codel leaves

 SqmService ◄──HTTP :8088── /data/on_boot.d/20-sqm-monitor.sh  (busybox httpd, JSON)
 SqmDeploymentService ◄──SSH── logs, status checks, manual calibration

 sqm_learning task (hourly) ──SSH──► SqmShaperLiftScript( gateway WAN speed test )
   SqmLearningExecutor ──► SqmLearningSample ──► CongestionProfileLearner ──► SqmCongestionProfile
```

## Where the code lives

| Component | Project | Responsibility |
|-----------|---------|----------------|
| `ConnectionProfile` | Sqm | Per connection type: speed envelope, latency tuning, blending weights, the built-in 7x24 pattern. Builds the 168-slot download and upload schedules. |
| `SqmConfiguration` | Sqm | One WAN's deployable configuration. `ApplyProfileSettings()` fills it from the type; `ApplyLearnedProfile()` re-derives it from a learned curve. |
| `ScriptGenerator` | Sqm | Emits the boot script, which carries the speedtest and ping scripts as heredocs. Owns the tc functions, the awk arithmetic helpers, the probe-lock logic, and the pinned Ookla CLI install. |
| `SqmShaperLiftScript` | Sqm | Wraps a gateway command so it runs with the shaper lifted, then restores the rates from an `EXIT` trap. |
| `CongestionProfileLearner`, `LearnedCongestionProfile` | Sqm | Turns hourly samples into a smoothed 7x24 multiplier curve, and converts a measured peak into a shaper rate. |
| `CongestionProfileInsights` | Sqm | Describes a learned curve in words for the page: the slow band, and how it compares with the type's default. |
| `InputSanitizer` | Sqm | Validates every value that reaches a shell command line: interface, ping host, Ookla server ID, cron expression, connection name. |
| `Sqm.razor` | Web | The Adaptive SQM page. `CreateSqmConfiguration()` is the only place a deployable `SqmConfiguration` is built. |
| `SqmDeploymentService` | Web | Deploy, remove, status check, manual calibration, log reads, and the SQM Monitor script. |
| `SqmService`, `TcMonitorClient` | Web | Polls the SQM Monitor endpoint; reads WAN interfaces and Smart Queues state from the UniFi Console. |
| `SqmLearningService`, `SqmLearningExecutor` | Web | Starts and stops learning, and takes one sample per scheduled run. |
| `WanIdleGate`, `GatewayInterfaceRateProbe` | Web | Decide whether the WAN is quiet enough to sample. |
| `GatewayWanSpeedTestService` | Web | Runs the gateway WAN speed test, wrapped in the shaper lift when a sample asks for it. |
| `GatewayShaperProbeService` | Web | Config Optimizer check that Smart Queues is actually provisioned on the gateway, not only enabled in UniFi Network. |
| `SqmWanConfiguration`, `SqmCongestionProfile`, `SqmLearningSample` | Storage | Saved WAN settings, the learned profile per WAN, and the raw samples. |

`SqmManager`, `BaselineCalculator`, `LatencyMonitor` and `SpeedtestIntegration` are C# counterparts
of the script logic. The deploy path uses only `SqmManager.ValidateConfiguration()`. The generated
scripts are the runtime, and a change to the rate logic goes in `ScriptGenerator`.

## Gateway prerequisites

- **Smart Queues on for the WAN.** UniFi Network creates `ifb<interface>` and the HTB root class
  `1:1` for the download direction, and the HTB tree on the WAN interface for upload. A deploy
  refuses when the IFB device is missing. UniFi Network sometimes accepts the Smart Queues toggle
  without creating the classes; adding any QoS rule makes it provision them.
- **udm-boot.** Runs `/data/on_boot.d/*` at every boot. The page can install it.
- **SSH.** Direct, or through the On-Site Agent tunnel on an agent site.

## Building a configuration

`Sqm.razor` `CreateSqmConfiguration()` runs for each enabled WAN at deploy time:

1. Copy the page's fields: connection type, name, interface, nominal download and upload, burst
   mode, ping host, Ookla server ID, and the two calibration times as cron entries. `ShapeUpload`
   is always true.
2. `ApplyProfileSettings(effective link speed)` computes the envelope and tuning from the type. The
   effective link speed is the user's override, else the speed the UniFi Console reports.
3. Put back the user's overrides: baseline latency, latency threshold, and congestion severity.
4. Upload strength applies only to Cellular, Starlink and Fixed Wireless. Every other type gets 0.
5. When the WAN uses a learned profile, `ApplyLearnedProfile()` replaces the envelope (see below).

### Connection type parameters

Speeds are fractions of nominal download. `Max` is the calibration ceiling, `Min` the floor,
`AbsMax` the reference the ping loop scales against. Blending is baseline/measured.

| Type | Max | Min | AbsMax | Overhead | Base latency | Threshold | Decrease | Increase | Blend within | Blend below |
|------|-----|-----|--------|----------|--------------|-----------|----------|----------|--------------|-------------|
| GPON | 1.05 | 0.90 | 1.07 | 1.08 | 5 ms | 1.0 ms | 0.98 | 1.03 | 70/30 | 85/15 |
| XGS-PON | 1.05 | 0.92 | 1.07 | 1.08 | 4 ms | 1.0 ms | 0.99 | 1.02 | 75/25 | 90/10 |
| DOCSIS Cable | 0.95 | 0.65 | 0.98 | 1.05 | 18 ms | 2.5 ms | 0.97 | 1.04 | 60/40 | 80/20 |
| Starlink | 1.10 | 0.35 | 1.15 | 1.15 | 25 ms | 4.0 ms | 0.97 | 1.04 | 50/50 | 70/30 |
| DSL | 0.95 | 0.85 | 0.98 | 1.10 | 20 ms | 3.0 ms | 0.97 | 1.03 | 65/35 | 80/20 |
| Fixed Wireless | 1.10 | 0.50 | 1.15 | 1.10 | 15 ms | 4.0 ms | 0.96 | 1.05 | 50/50 | 65/35 |
| Cellular | 1.20 | 0.40 | 1.25 | 1.08 | 35 ms | 5.0 ms | 0.95 | 1.05 | 50/50 | 65/35 |

Every type uses a 0.95 safety cap. Starlink defaults to Ookla server 59762. The probe rate used
during calibration is `max(100, Max x 1.03)`.

### Schedules

The download schedule has 168 slots keyed `day_hour`, day 0 = Monday, gateway local time. Each slot
is the type's pattern multiplier times nominal download, with a 5 Mbps floor. Congestion severity
scales the dip: `1 - (1 - multiplier) x severity`, so hours at 1.0 never move.

The upload schedule exists only when upload is dynamic (`ShapeUpload` and strength > 0). Each slot
is `clamp(1 - (1 - multiplier) x strength, 0.5, 1.0) x nominal upload`. The upload curve is the
learned upload curve, else the learned download curve, else the type's pattern. At strength 0 the
scripts carry no upload schedule and are byte-identical to scripts from before dynamic upload.

### Learned profile

`ApplyLearnedProfile()` derives the whole envelope from the measured peak:

- `ceiling = round(peak download x ShaperFactor)`. ShaperFactor is 1.03 for GPON and XGS-PON, 1.01
  for DOCSIS and DSL, and 1.00 for shared media.
- Nominal, Max and AbsMax all become the ceiling. Min is the type's floor fraction of it.
- Overhead and safety cap become 1.0, because the ceiling is already a shaper rate.
- `MeasuredToShapedFactor` becomes the ShaperFactor, so the speedtest script converts an Ookla
  payload figure to a shaper rate before blending.
- The learned curves replace the type's pattern. With dynamic upload, nominal upload becomes the
  ceiling of the upload peak.

Severity, the ping loop, the floor, and the link clamp work as they do without a learned profile.

## Deploy

The page drives it; `SqmDeploymentService` does the gateway work.

1. `CleanAllSqmScriptsAsync()` removes every `20-sqm-*.sh` and `21-sqm-*.sh`, everything in
   `/data/sqm`, and every cron line containing `sqm`. This clears scripts left by a renamed WAN.
2. The page saves the WAN configurations.
3. `DeployAsync()` for each enabled WAN:
   - `SqmManager.ValidateConfiguration()` checks ranges and runs `InputSanitizer` on every shell value.
   - A ping to the ping host on the WAN interface must succeed.
   - `ifb<interface>` must exist.
   - The boot script is uploaded over SFTP (it outgrows an SSH exec packet), made executable, and run
     with a 5 minute timeout. A non-zero exit removes that WAN's scripts and cron lines and fails the
     deploy.
   - The first calibration runs `BootDelaySeconds` after the boot script: 5 s for WAN1, 50 s for
     WAN2 when both are enabled, so the two speed tests do not overlap.
4. `DeploySqmMonitorAsync()` deploys `20-sqm-monitor.sh`.

`RemoveAsync()` deletes the boot scripts, `/data/sqm`, the cron lines, and optionally the SQM Monitor.
It does not touch the managed Ookla CLI.

## Files on the gateway

| Path | Written by | Purpose |
|------|-----------|---------|
| `/data/on_boot.d/20-sqm-<wan>.sh` | Deploy | Self-contained boot script. Rebuilds everything below at every boot. |
| `/data/sqm/<wan>-speedtest.sh` | Boot script | Calibration. |
| `/data/sqm/<wan>-ping.sh` | Boot script | Latency loop. |
| `/data/sqm/<wan>-result.txt` | Speedtest script | `Measured download speed: <N> Mbps`, the last good calibrated rate. |
| `/data/sqm/probe-<interface>.lock` | Shaper lift | Present while a learning sample holds the shaper lifted. |
| `/data/network-optimizer/bin/speedtest` | Boot script | Managed Ookla CLI, pinned build. |
| `/var/log/sqm-<wan>.log` | All three | Per-WAN log, trimmed to 2000 lines each time the boot script runs. |
| `/data/on_boot.d/20-sqm-monitor.sh`, `/data/sqm-monitor/` | Deploy | SQM Monitor. |
| `/etc/systemd/system/sqm-monitor.service` | Monitor script | Runs the monitor HTTP server. |

`<wan>` is the connection name after `InputSanitizer.SanitizeConnectionName()`.

## Boot script

Runs on every boot and on every deploy.

1. **Dependencies.**
   - Ookla CLI: `speedtest_is_valid()` checks the file at the managed path is a regular executable,
     matches the pinned SHA-256 for the architecture (aarch64, armhf, x86_64), and prints the pinned
     `Speedtest by Ookla <version> (<build>)` line. When it fails, the script downloads the pinned
     tarball from `install.speedtest.net` over HTTPS only, capped at 120 s and 4 MiB, and checks the
     archive hash. On HTTP 404 or 410 it downloads Ookla's `.deb` for the same build from
     Packagecloud, checks that hash, and extracts the binary with `dpkg-deb`. It adds no apt
     repository, runs no maintainer scripts, and leaves the gateway's own `speedtest` package alone.
     The binary hash is checked again, then `mv` activates it atomically. A failed install logs a
     WARNING and the deploy continues; calibration then refuses to run until the binary validates.
   - `jq` from apt, after one `apt-get update` if it is missing.
   - A missing `awk` or `jq` is logged as an ERROR.
2. **Directories.** `mkdir -p /data/sqm`.
3. **Speedtest script** and 4. **ping script**, written from quoted heredocs.
5. **Crontab.** Removes this WAN's lines, then adds one line per calibration time and one
   `*/1 * * * *` ping line. The ping line skips the minutes that match a calibration time. Each line
   exports `PATH` and `HOME=/root`.
6. **Initial calibration.** Stops any earlier `systemd-run` timer for this speedtest script, then
   schedules the speedtest script `initialDelaySeconds` from now.

## Speedtest script (calibration)

Runs at the two configured times, after every boot, and on demand from the page (Operator and up).

1. Ensure `awk` and `jq`. A missing one is installed from apt (`mawk` for awk), retried after
   `apt-get update`. Still missing: ERROR, exit, tc untouched.
2. Validate the managed Ookla binary. Invalid: ERROR, exit, tc untouched.
3. Wait up to 90 s for the probe lock to clear. A lock older than 120 s is stale and removed.
4. Require `ifb<interface>`.
5. Lift download to the probe rate with the configured burst mode, and set upload to nominal, so the
   measurement runs unshaped.
6. Run `speedtest --accept-license --accept-gdpr --format=json --interface=<wan> [--server-id=<id>]`.
7. `download.bandwidth x 8 / 1e6` is the measured Mbps. With a learned profile, multiply by
   `MeasuredToShapedFactor`.
8. Floor at Min.
9. Blend with the schedule. The slot value is interpolated at quarter-hour steps toward the next
   hour's slot (`:00` 0%, `:15` 25%, `:30` 50%, `:45` 75%).
   ```
   if measured >= 0.9 x slot:  blended = slot x within + measured x (1 - within)
   else:                       blended = slot x below  + measured x (1 - below)
   rate = blended x overhead            (no slot: measured x overhead)
   ```
10. Dynamic upload: the upload slot, interpolated the same way, clamped to [min upload, nominal].
11. Clamp to Max, then to `Max x safety cap`, then to `link speed x 0.98` when the link speed is known.
12. An unusable rate (empty, non-numeric, or zero) never reaches tc. The script restores the rate in
    `result.txt` if that one is usable, else leaves the probe rate and logs it, then exits 1.
13. Write `result.txt`, apply download and upload, log `Adjusted to <down> Mbps (down), <up> Mbps (up)`.

## Ping script (latency loop)

Runs every minute except during a calibration minute. It never calls apt.

1. Require `awk`. Stand down (exit 0) while a fresh probe lock exists.
2. Read `result.txt` and require a number above 0 and below 100000.
3. Require `ifb<interface>`.
4. Starting rate, from the interpolated slot and the last calibration:
   ```
   MAX_DOWNLOAD_SPEED = min(slot x overhead, Max) x within + result x (1 - within)
   ```
   With no slot it is the calibrated result.
5. Dynamic upload: compute the upload slot rate.
6. Schedule cap: `AbsMax x safety cap`. GPON and XGS-PON scale it by `slot / nominal`, so the cap
   follows the curve. Clamp the starting rate to the cap, and both to `link speed x 0.98`.
7. `ping -I <wan> -c 10 -i 0.5 <host>`, average RTT.
8. `n = int((latency - base latency) / threshold)`, then:
   ```
   latency >= base + threshold:   rate = start x decrease ^ ((n + 1)^0.7 - 1),  floored at Min
   latency <  base - 0.4:         start < 0.92 AbsMax -> start x increase^2
                                  start < 0.94 AbsMax -> 0.94 AbsMax
                                  else                -> start
   otherwise (and latency - base <= 0.3):
                                  start < 0.90 AbsMax -> start x increase
                                  start < 0.92 AbsMax -> 0.92 AbsMax
                                  else                -> start
   ```
   The `(n + 1)^0.7 - 1` exponent is gentle on a single transient spike and steep under real
   congestion.
9. Clamp to the schedule cap and to Max, round, and skip a rate of 0 or below.
10. Dynamic upload: a latency cut scales upload by the same fraction, floored at min upload. The
    ping cannot tell which direction is congested.
11. Skip the tc write when neither root rate would change. Otherwise apply both and log
    `Ping adjusted to <down> Mbps (down), <up> Mbps (up) (latency: <ms>ms)`.

## tc functions

`ScriptGenerator.TcFunctionsText` is the only copy. The speedtest script, the ping script and the
shaper lift all embed it.

- **`update_all_tc_classes <dev> <rate> [burst mode]`.** Refuses an unusable rate: `rate 0Mbit` on
  the root class takes the whole direction down. Sets root `1:1` to `rate = ceil = <rate>`. For each
  child of `1:1` whose rate is `64bit` (stock best-effort) or `100Kbit` (already ours), sets
  `rate 100kbit ceil <rate>`, keeps its `prio`, and retunes its fq_codel leaf. A child with a real
  guaranteed rate is a UniFi QoS class and stays untouched.
- **fq_codel leaf.** `limit 2000`, `target 5ms`, `interval 100ms`, `ecn`. `memory_limit` is 4 MB up
  to 300 Mbps, rises linearly to 8 MB at 750 Mbps, and stays at 8 MB above that.
- **Burst.** Mode 0: 5 bytes per Mbps, clamped to 1500..5000. Mode 1 (rate-proportional, about
  1 ms of line time): 125 bytes per Mbps, clamped to 1500..131072. Mode 1 applies to the download
  (IFB) side only. The page turns it on for a new WAN; a saved WAN keeps its stored value.
- **`tune_tc_performance <dev>`.** Re-applies burst and fq_codel at the current root rate without
  changing it. Used on upload when `ShapeUpload` is off.

All arithmetic goes through the awk helpers `num_i`, `num_f`, `num_s` and `num_bool`, under
`LC_ALL=C`. Never reintroduce `bc`: it is not in the UniFi OS firmware base, so every firmware
upgrade removes it.

## Congestion profile learning

Learning measures the WAN once an hour for a week and builds a curve from what the line delivered,
in place of the connection type's assumed pattern.

**Start.** `SqmLearningService.StartAsync()` (Operator) deletes the previous run's samples and
creates an `sqm_learning` scheduled task: hourly, first run one minute out, 1 to 28 days (default 7),
2 to 20 s per direction (default 4). The task shows on Alerts & Schedule - Schedule. It is the only
record of whether learning is running; the `SqmCongestionProfile` row holds the data.

**Each run** (`SqmLearningExecutor.RunAsync`):

1. Past the end time: recompute, mark complete, disable the task, notify.
2. Read the gateway (`GatewayInterfaceRateProbe`, one SSH call): 3 s of rx/tx counters on the
   data-path interface, the gateway's local day, hour and minute, and whether a `*-speedtest.sh` is
   running. The local day and hour are the slot the sample lands in.
3. Decide idle. `WanIdleGate` reads the SNMP `interface_counters` series for the WAN's counter
   interface (the one Live View and ISP Health show, which is the physical port for a VLAN or PPPoE
   WAN): the peak over the last 60 s, no older than 90 s. Without monitoring data it uses the
   gateway counters from step 2. Idle means at most `max(1 Mbps, 1.5% of nominal)` in each direction.
4. Defer as "waiting" and retry in 10 minutes when: the SQM speedtest is running, a scheduled SQM
   calibration is within 3 minutes, another enabled WAN speed test schedule is within 3 minutes
   either side, the WAN is not idle, or (when SNMP decided) a fresh gateway read says it is busy.
5. Size the lift. Download: the calibration probe rate (`Max x 1.03`). Upload:
   `max(nominal + 1, nominal x 1.10)`. Both rise to any lift remembered from earlier samples. With
   a link speed override saved on the WAN, both stay under `override x 0.98`.
6. Run the gateway WAN speed test with that duration, 10 streams, not stored as a speed test
   result, wrapped by `SqmShaperLiftScript`.
7. Wait 2 s and read the gateway again. Traffic still on the line means the sample was contended:
   record nothing, defer.
8. Store an `SqmLearningSample`. A direction at 95% or more of its lift is probe-limited; its next
   lift rises to 1.5x (download) or 1.25x (upload) of what it measured, up to the ceiling.
9. A failure, including a run with zero throughput in either direction, counts toward
   `ConsecutiveFailures`. The third in a row notifies, then every 24th.
10. Recompute the profile from every successful sample of the run.

**Shaper lift.** `SqmShaperLiftScript` waits up to 20 s for a running `ping.sh`, creates the probe
lock, reads both root rates, and installs an `EXIT` trap that restores them and removes the lock. It
lifts only the devices that have an HTB root, runs the test, and drops stderr so progress lines stay
out of the JSON the app parses. The ping script stands down on the lock and the speedtest script
waits for it, so neither writes over the lift.

**Learner** (`CongestionProfileLearner.Learn`, deterministic):

1. Drop samples with an invalid slot or zero throughput.
2. Per hour of day with at least 4 samples, drop a sample below 0.45x or above 1.8x that hour's
   median, in either direction.
3. Average each of the 168 slots, then smooth over the week ring. Weights: own slot 1.0, adjacent
   hour 0.5, same hour on other days 0.25, adjacent hour on other days 0.1. An empty neighbourhood
   falls back to the hour-of-day mean, then the overall median.
4. The peak comes only from samples that were not probe-limited. When every sample was, the peak is
   the smoothed maximum and `PeakIsLowerBound` is set.
5. Multiplier per slot = smoothed / peak, clamped to [0.2, 1.0].
6. Reliable = at least 100 valid samples, 70% of slots covered, and 6 distinct days.

**Use.** The page offers the learned curve once a profile exists; the user selects it per WAN, and
the next deploy applies it through `ApplyLearnedProfile()`. The profile exports as JSON with slot
layout `index = day * 24 + hour, day 0 = Monday`.

**ISP Health.** Load analysis on the primary WAN excludes each scheduled SQM calibration (30 s from
its scheduled time) and each learning sample (from `20 s + 2 x duration` before its stamp to 5 s
after). Learning samples are not stored as speed test results, so nothing else marks them.

## Status and monitoring

- **SQM Monitor.** `20-sqm-monitor.sh` installs `sqm-monitor.service`, which runs `busybox httpd` on
  the gateway's TC monitor port (default 8088). Every request returns JSON per WAN: current root rate,
  last calibrated rate, last speedtest (measured and adjusted), last ping adjustment, whether a
  speedtest is running, and the last ERROR not followed by a successful adjustment.
  `SqmService` polls it through `TcMonitorClient`. The script also removes the netcat-era watchdog
  timer and cron line.
- **Deployment status.** `CheckDeploymentStatusAsync()` runs one SSH command that reports udm-boot,
  boot script and cron counts, the SQM Monitor, the managed Ookla CLI (the same validation as the
  scripts), `jq`, scripts still calling `bc`, and ping scripts without the probe-lock guard. The last
  two make the page ask for a redeploy.
- **Log contract.** The SQM Monitor and `SqmDeploymentService` parse these strings from
  `/var/log/sqm-<wan>.log`: `Starting speedtest adjustment`, `Measured: <N> Mbps`,
  `Adjusted to <N> Mbps`, `Ping adjusted to <N> Mbps ... (latency: <ms>ms)`, and `ERROR:`. Changing
  their wording breaks status parsing.
- **Manual calibration.** `TriggerSqmAdjustmentAsync()` runs the speedtest script with a 90 s timeout.
  Any stdout is an error; with none, the last `ERROR:` line in the log is the reason.

## Authorization

Both services are `[MutatingService(SiteScoped = true)]`; the role is checked on the site in context.

| Role | Adaptive SQM | Learning |
|------|--------------|----------|
| Viewer | Status, logs, WAN status, connection test | Status, samples, export |
| Operator | Deploy, deploy SQM Monitor, clean scripts, run calibration | Start, stop, clear, remove schedule, sample now |
| Admin | Remove Adaptive SQM, install udm-boot | |

## Firmware upgrades

A UniFi OS upgrade resets the root filesystem and keeps `/data`. The boot script runs at the next
boot and rebuilds the scripts, the cron lines and the first calibration. `jq` comes back from apt.
The Ookla CLI lives under `/data` and survives as is. Rates never depend on `bc`.
