# Alerts & Schedule

**Branch:** `feature/alerts-schedule` (create from main)
**Worktree:** `C:\Users\tjvc4\OneDrive\StartupProjects\NetworkOptimizer\feature-alerts-schedule`
**Deploy:** Normal git push flow - `git push && ssh root@nas "cd /opt/network-optimizer && git pull && cd docker && docker compose build network-optimizer && docker compose up -d network-optimizer"`

## Setup

```bash
cd C:\Users\tjvc4\OneDrive\StartupProjects\NetworkOptimizer\main-work
git worktree add ../feature-alerts-schedule -b feature/alerts-schedule
pwsh ./scripts/local-dev/copy-untracked.ps1 ../feature-alerts-schedule -IncludeIgnored
```

## Spec

Rename the Alerts page (`/alerts`) to **Alerts & Schedule**. Add a **Schedule** tab as the first tab, moving existing tabs (Active Alerts, History, Rules, Incidents) after it.

## Current State

The alerts system is **fully functional** - not a facade:
- 26 source files, 4 DB tables, 2 background services
- 5 delivery channels (email, webhook, Slack, Discord, Teams)
- 7 default alert rules with pattern matching, cooldowns, escalation
- Real event publishing from AuditService, ThreatCollectionService, ClientSpeedTestService, WanSpeedTestServiceBase
- Incident correlation, digest summaries, full CRUD UI

What's missing: **nothing runs on a schedule today**. Audits are manual-only. Speed tests are manual-only. The SQM page has morning/evening time pickers for WAN speed tests but they're cosmetic (stored in `SqmWanConfiguration` but no background service reads them).

---

## Part 1: Schedule Tab

### Layout

The Schedule tab shows a single-page list of **schedulable tasks** grouped by category. Each task has:
- Enable/disable toggle
- Frequency selector
- Target selector (where applicable)
- Last run time + next run time display
- "Run Now" button

### Schedulable Tasks

#### Security Audit

- **Default:** Enabled, every 12 hours
- **Frequency options:** Every 6h, 12h, 24h, 48h
- **No target selector** (always audits the full network)
- Audits are lightweight (30s-2min depending on network size)
- Publishes existing `audit.completed` and `audit.critical_findings` alert events

#### WAN Speed Test

One row per detected WAN interface (WAN1, WAN2, etc.), auto-populated from gateway config.

- **Default:** Disabled
- **Frequency options:** Every 6h, 12h, 24h, custom (morning + evening time pickers like SQM page)
- **Target selector:** WAN interface dropdown matching `/wan-speedtest` UI:
  - Individual WANs: "WAN1", "WAN2"
  - Combos: "WAN1 + WAN2" (multi-WAN load test)
- **Test location:** Gateway (preferred, most accurate) or Server (fallback if no gateway SSH)
- **Max Load toggle:** Same as WAN speed test page (more servers/streams)
- Publishes existing `wan.speed_completed` alert event
- **Stagger multi-WAN tests** by 5 minutes to avoid saturating the link during measurement

#### LAN Speed Test

One row per saved device from the LAN speed test "Other Devices" list (manually configured targets like NAS, servers, desktops). Also one row for Gateway if gateway SSH is configured.

- **Default:** Disabled
- **Frequency options:** Every 6h, 12h, 24h, 48h, weekly
- **Target:** Pre-populated from saved devices (same list shown on `/speedtest` page)
- **No client speed tests** - mobile/wireless clients vary too much; only SSH-initiated LAN tests to known targets
- Publishes existing `speedtest.completed` alert event

### What NOT to Schedule

- **Client speed tests** - These are user-initiated from their device (browser or iperf3 client). Results vary by device location, Wi-Fi conditions, etc. Not meaningful as a scheduled task.
- **Threat collection** - Already runs continuously in background via `ThreatCollectionService`
- **Wi-Fi optimization** - Already has its own polling loop

---

## Part 2: Schedule Engine (Background Service)

### ScheduleService : BackgroundService

New hosted service that evaluates schedules and triggers tasks.

```
Loop every 60 seconds:
  1. Load all enabled schedules from DB
  2. For each schedule where NextRunAt <= UtcNow:
     a. Mark as running (prevent duplicate execution)
     b. Execute the task (audit, WAN test, LAN test)
     c. Update LastRunAt, calculate NextRunAt
     d. Mark as idle
  3. Sleep 60 seconds
```

**Concurrency rules:**
- Only one audit at a time (skip if manual audit is running)
- Only one WAN test per WAN interface at a time (skip if user is running one)
- Only one LAN test per device at a time
- WAN tests on different interfaces CAN run concurrently (staggered by 5 min)

**Task execution** reuses existing services:
- Audit: `AuditService.RunAuditAsync()` (needs to be callable from singleton context via `IServiceScopeFactory`)
- WAN test: `GatewayWanSpeedTestService.RunTestAsync()` or `UwnSpeedTestService.RunTestAsync()`
- LAN test: `Iperf3SpeedTestService.RunTestAsync()`

All of these already publish alert events, so scheduled runs automatically flow through the existing alert pipeline (rules, delivery channels, digests).

---

## Part 3: Database Schema

### New Table: `ScheduledTasks`

| Column | Type | Description |
|--------|------|-------------|
| Id | int (PK) | Auto-increment |
| TaskType | string | `audit`, `wan_speedtest`, `lan_speedtest` |
| Name | string | Display name, e.g. "Security Audit", "WAN1 Speed Test", "NAS Speed Test" |
| Enabled | bool | Default false (except audit = true) |
| FrequencyMinutes | int | Interval in minutes (360=6h, 720=12h, 1440=24h) |
| CustomMorningHour | int? | For custom schedule (null = use frequency interval) |
| CustomMorningMinute | int? | |
| CustomEveningHour | int? | |
| CustomEveningMinute | int? | |
| TargetId | string? | Device host/IP for LAN, WAN interface name for WAN, null for audit |
| TargetConfig | string? | JSON blob for task-specific config (max load, test location, parallel streams) |
| LastRunAt | DateTime? | Last execution time (UTC) |
| NextRunAt | DateTime? | Calculated next execution time (UTC) |
| LastStatus | string? | `success`, `failed`, `skipped` |
| LastErrorMessage | string? | Error details if failed |
| CreatedAt | DateTime | |

### Migration

- Seed default audit schedule (enabled, 720 minutes = 12h)
- Pre-populate WAN schedules (disabled) from existing `SqmWanConfiguration` morning/evening times if set
- Pre-populate LAN device schedules (disabled) from existing `DeviceSshConfiguration` entries

---

## Part 4: Schedule Tab UI

### Layout

```
[ Schedule ] [ Active Alerts ] [ History ] [ Rules ] [ Incidents ]

Scheduled Tasks
  Automatic tasks run in the background on configured intervals.
  Results flow through your alert rules and delivery channels.

+-------------------------------------------------------------------+
| Security Audit                                                     |
|                                                                    |
|  [ON]  Every: [12 hours v]                                        |
|                                                                    |
|  Last run: 2h ago (Score: 87)    Next: in 10h    [ Run Now ]     |
+-------------------------------------------------------------------+

WAN Speed Tests
  Test your internet connection speed on a schedule.
  Gateway SSH must be configured for gateway-based tests.

+-------------------------------------------------------------------+
| WAN1 - Starlink                                                    |
|                                                                    |
|  [OFF]  Every: [24 hours v]  Location: [Gateway v]  [ ] Max Load |
|                                                                    |
|  Last run: never                 Next: --          [ Run Now ]    |
+-------------------------------------------------------------------+
| WAN2 - AT&T                                                       |
|                                                                    |
|  [OFF]  Every: [24 hours v]  Location: [Gateway v]  [ ] Max Load |
|                                                                    |
|  Last run: never                 Next: --          [ Run Now ]    |
+-------------------------------------------------------------------+

LAN Speed Tests
  Test throughput to devices on your local network.
  Add devices on the LAN Speed Test page.

+-------------------------------------------------------------------+
| Gateway (192.168.1.1)                                              |
|                                                                    |
|  [OFF]  Every: [24 hours v]                                       |
|                                                                    |
|  Last run: never                 Next: --          [ Run Now ]    |
+-------------------------------------------------------------------+
| NAS (192.168.1.50)                                          Server |
|                                                                    |
|  [OFF]  Every: [24 hours v]                                       |
|                                                                    |
|  Last run: 18h ago (942/940 Mbps)  Next: in 6h   [ Run Now ]    |
+-------------------------------------------------------------------+

  No saved LAN devices yet. Add devices on the [LAN Speed Test] page.
```

### Frequency Dropdown Options

| Label | Minutes | Use case |
|-------|---------|----------|
| Every 6 hours | 360 | Audit, critical WAN monitoring |
| Every 12 hours | 720 | Audit default |
| Every 24 hours | 1440 | Speed test default |
| Every 48 hours | 2880 | Low-priority LAN targets |
| Weekly | 10080 | Infrequent LAN targets |
| Custom times | -- | Shows morning + evening time pickers (HH:MM inputs) |

"Custom times" reveals the same morning/evening time picker UI currently on the SQM page, allowing users to pick exact times rather than intervals.

### Run Now

Triggers the task immediately regardless of schedule. Uses the same service call as the scheduled execution. Button shows spinner while running, disables to prevent double-clicks.

### Empty States

- **No gateway SSH configured:** WAN section shows info banner linking to Settings
- **No saved LAN devices:** LAN section shows link to `/speedtest` page to add devices
- **No WAN interfaces detected:** WAN section shows info banner about gateway connection

---

## Part 5: Nav Menu Update

Change nav item text from "Alerts" to "Alerts & Schedule". Keep the same icon (`alerts.png`) and route (`/alerts`).

---

## Part 6: Alert Rule Additions

### New Default Alert Rules

Add these to `DefaultAlertRules.cs`:

| Rule Name | Event Pattern | Severity | Cooldown | Notes |
|-----------|--------------|----------|----------|-------|
| Audit Score Regression | `audit.completed` | Warning | 12h | Triggers when score drops > 5 points vs previous |
| WAN Speed Below Threshold | `wan.speed_completed` | Warning | 6h | User-configurable threshold (default: 50% of baseline) |
| LAN Speed Below Threshold | `speedtest.completed` | Warning | 6h | User-configurable threshold (default: 50% of baseline) |
| Scheduled Task Failed | `schedule.task_failed` | Error | 1h | SSH connection failed, timeout, etc. |

### New Alert Events

| EventType | Source | Published By | When |
|-----------|--------|-------------|------|
| `schedule.task_failed` | schedule | ScheduleService | When a scheduled task fails (SSH error, timeout, etc.) |
| `schedule.task_completed` | schedule | ScheduleService | When a scheduled task completes (info-level, for history) |

Existing events (`audit.completed`, `wan.speed_completed`, `speedtest.completed`) are already published by the underlying services and will fire for scheduled runs too - no changes needed.

---

## Part 7: SQM Integration (Migrate Existing Schedule)

The SQM page currently has morning/evening WAN speed test time pickers stored in `SqmWanConfiguration`. These should be migrated:

1. On first load of the Schedule tab, check `SqmWanConfiguration` for non-default speedtest times
2. If found, pre-populate `ScheduledTasks` entries with custom morning/evening times
3. The SQM page time pickers should link to the Schedule tab: "Configure speed test schedule on the [Alerts & Schedule](/alerts) page"
4. Eventually remove the time pickers from SQM page (but not in this PR - just add the link)

---

## Implementation Order

1. DB migration: `ScheduledTasks` table + seed audit schedule
2. `ScheduleService` background service (loop, concurrency, task dispatch)
3. Schedule repository (CRUD, next-run calculation)
4. Schedule tab UI on Alerts page
5. Nav menu rename
6. New default alert rules + `schedule.*` events
7. SQM page link to schedule tab
8. Tests for ScheduleService (mock services, verify scheduling logic)

## Out of Scope

- Scheduling client speed tests (mobile devices vary too much)
- Scheduling Wi-Fi optimization (already has its own loop)
- Scheduling threat collection (already continuous)
- Cron expressions (overkill - simple intervals + custom times covers all use cases)
