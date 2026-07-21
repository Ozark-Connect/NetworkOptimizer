# Monitoring Interface: Disable / Enable

Status: Design (approved 2026-07-21)

## Summary

Add the ability to Disable (un-deploy) a configured monitoring interface without deleting its configuration, and to Enable it again later.
A disabled interface has all of its gateway artifacts removed (macvlan, host route, SNAT/DNAT, boot script, cron watchdog), but its row and settings are kept, so it can be restored with a single click.
This supports the common workflow of pausing an interface during testing and bringing it back later without re-entering it.

## Goals

- Per-interface Disable/Enable toggle (no global switch).
- A disabled interface stays down across reboots and against the cron watchdog - this must be robust, not merely a UI flag.
- Reuse the existing deploy/teardown primitives and match the repository's existing patterns (Performance Tweaks, the deployment service, the card UI).

## Non-goals

- No global "pause all" control.
- No change to CM/ONT/modem polling behaviour (no monitor currently consumes `MonitoringInterface`).
- No new external ONT provider (tracked separately).

## Background: current architecture

A monitoring interface deploys a macvlan on the WAN port with a stable MAC, a gateway-local IP, a `/32` host route (plus optional SNAT, plus optional alias DNAT for the duplicate-IP case), and a cron watchdog.
Everything is applied over SSH by `MonitoringInterfaceDeploymentService.DeployAsync(mi)`, which writes an idempotent boot script to `/data/on_boot.d/30-monitoring-iface-<Name>.sh` and runs it.
Reboot-safety comes from two mechanisms: udm-boot runs the boot script on boot, and the script self-installs a cron watchdog that re-applies after UniFi reprovisioning.
`RemoveAsync(mi)` performs the gateway teardown only (cron entry, route, macvlan, SNAT/DNAT, boot script) and does not touch the database; the database delete is the separate `Repo.DeleteMonitoringInterfaceAsync(id)`.
Status is computed on demand by `CheckStatusAsync`; there is no background poller and no persisted status.
Today there is no enabled/disabled state on the entity (`IsManuallyDeployed` means "the user manages the gateway artifacts by hand" and is unrelated).

## Why a naive flag is not enough

A design review surfaced two correctness problems that rule out "just add a boolean and call the existing RemoveAsync":

1. `RemoveAsync` success is unreliable.
   It ignores the results of the cron, SNAT, route, macvlan, and boot-script removal steps and only fails on alias-cleanup errors, so a non-aliased interface on an unreachable gateway returns `success = true`.
   Persisting `Disabled = true` off that would show a paused state while the interface is still deployed.

2. There is a real watchdog race.
   Removing the cron entry does not stop an already-running watchdog script, which recreates the cron entry and every artifact at its end.
   The script never reads the database, so a database flag alone cannot pause it.

Therefore Disable needs a gateway-side authority that the script honours, plus a hardened and verified teardown - not just a boolean.

## Design

### 1. Data model

Add `bool Disabled` (default `false`) to `MonitoringInterface`.
Add an EF Core migration that creates the column with `nullable: false, defaultValue: false`, so existing rows remain enabled, and update the model snapshot.
Add a migration test seeded from the immediately preceding migration to confirm the default applies to existing rows.

### 2. Gateway-side disabled marker (the core mechanism)

Introduce a per-interface marker file at `/data/monitoring-ifaces.disabled/<Name>`.
The marker lives in a dedicated directory under `/data`, deliberately not in `on_boot.d`, so udm-boot never executes it.
Its presence means "do not deploy this interface."

Change the boot-script template so that it checks for the marker in two places: at the very top (before applying any artifact) and again immediately before it (re)installs the cron watchdog.
If the marker is present the script exits 0 without applying anything and without (re)installing cron.
Wrap the script's apply section and the teardown in a shared per-interface `flock` (e.g. `/tmp/monitoring-iface-<Name>.lock`), so a teardown and a concurrently-running watchdog cannot interleave.

### 3. Hardened `RemoveAsync`

Check the result of every teardown SSH step (cron, SNAT, route, macvlan, boot script), not only the alias cleanup, and aggregate them into an honest success value with a specific error message on failure.
After teardown, verify absence (interface, route, cron, and boot script gone) with one probe before reporting success; this verification also catches a watchdog that resurrected artifacts mid-teardown.
This corrects an existing latent bug and is in scope because Disable builds directly on this primitive.

### 4. Service orchestration

Add `DisableAsync(mi)` and `EnableAsync(mi)` to `MonitoringInterfaceDeploymentService`.

`DisableAsync(mi)`:

1. Write the marker (this guards the teardown against a running watchdog).
2. Take the flock and run the hardened `RemoveAsync` teardown.
3. Verify absence.
4. On success, persist `Disabled = true` via the targeted update (see below).
   On failure, best-effort remove the marker, surface the error, and leave `Disabled = false`.
   If the gateway is unreachable the marker write fails first, so Disable reports "gateway unreachable" and changes nothing.

`EnableAsync(mi)`:

1. Persist `Disabled = false`.
2. Remove the marker.
3. Run `DeployAsync(mi)` (writes a fresh boot script and cron).
4. Report success only if `DeployAsync` succeeds; on deploy failure the row stays enabled with the existing "not deployed / needs re-apply" status rather than a fake success.

Order rationale: for Disable, teardown-before-flag means a crash leaves an enabled row that looks "not deployed" (safe) instead of active artifacts hidden behind "Disabled".
For Enable, flag-before-deploy means a crash cannot leave artifacts up while the UI still says "Disabled".
A persisted pending/transition state is deliberately avoided; without a reconciliation worker it could get stuck, and strong teardown results plus live verification fit the current on-demand model better.

### 5. `DeployAsync` guard

`DeployAsync` rejects a call on a `Disabled` interface (no-op plus a clear message), so no path can silently deploy a paused row.

### 6. Persistence

Add a targeted `SetDisabledAsync(int id, bool value)` on the repository that updates only the `Disabled` column (and `UpdatedAt`) with an optimistic-concurrency check.
This avoids `SaveMonitoringInterfaceAsync`, which copies every field from a detached object and could clobber a concurrent edit made in another browser tab after a slow SSH operation.

### 7. UI (`MonitoringInterfacesCard.razor`)

Add a Disable/Enable toggle button in the row action group, following the `RemoveInterface` handler, the `_busyId`/`_busyAction` busy-state, and the `_deploySteps`/`_message` conventions.
An enabled row shows "Disable" (calls `DisableAsync`); a disabled row shows "Enable" (calls `EnableAsync`).
`StatusBadge` gains a neutral "Disabled" branch, shown when `mi.Disabled`, distinct from Active (green) and error (red).
Keep "Edit", "Check status", and "Remove" available for disabled rows; Check status is useful to diagnose leftover artifacts.
`Snapshot()` must copy `Disabled`, otherwise editing a disabled row would silently save `false`.
For a disabled row the edit form is save-only (`SaveOnly` must skip old-teardown for an already-disabled row), and "Save & Deploy" is presented as "Save & Enable" (save then `EnableAsync`); the edit form never calls `DeployAsync` on a disabled row.
Clear the cached `_statuses` entry on every Disable/Enable transition so a stale "Active" badge cannot reappear.
Hide Disable/Enable for manually-deployed rows (`IsManuallyDeployed == true`); we do not manage those artifacts.
`SummaryLabel` may show a paused count (for example "4 configured, 1 paused") - optional.

### 8. Queries and uniqueness

Disabled rows keep participating in all uniqueness checks (`Name`, `AliasIp`, `GatewayLocalIp`, and cross-column), so two paused configurations cannot reserve the same gateway artifacts and collide when re-enabled.
`GetByEffectiveIpAsync` keeps returning disabled rows; callers inspect `Disabled` rather than treating a disabled row as nonexistent.

## Error handling summary

- Disable, gateway unreachable: marker write fails, nothing changes, "gateway unreachable" surfaced, row stays enabled.
- Disable, teardown step fails: no `Disabled` persisted, marker removed best-effort, specific error surfaced, row stays enabled.
- Disable, watchdog resurrects mid-teardown: caught by post-teardown verification, treated as a teardown failure.
- Enable, deploy fails: `Disabled` already cleared, marker removed, row shows "not deployed / needs re-apply", no fake success.
- Concurrent Enable/Disable from two tabs: targeted `SetDisabledAsync` with optimistic concurrency; the loser gets a concurrency error rather than a clobber.

## Testing

- `DisableAsync`/`EnableAsync` orchestration: flag flips, call order, marker write/remove (gateway SSH and deploy mocked).
- Teardown-failure paths: non-alias SSH failure and cron/script removal failure both prevent persisting `Disabled` and surface the error.
- Watchdog exclusion: the script honours the marker at both check points (apply and cron install).
- Post-teardown verification catches resurrection.
- Edit: a disabled snapshot round-trips `Disabled`; "Save & Deploy" on a disabled row behaves as "Save & Enable"; `DeployAsync` rejects a disabled row.
- Manual rows: Disable/Enable are hidden.
- Enable with a failing deploy: the row stays enabled with no fake success.
- Concurrency: interleaved Enable/Disable via `SetDisabledAsync` optimistic concurrency.
- Migration: existing rows default to `Disabled = false` (seeded from the prior migration).

## Open questions

- While an interface is paused, any reachability check or alert against the modem/ONT IP elsewhere in the UI could still fire; no monitor consumes `MonitoringInterface` today, but if a surface does, it should reflect "paused" rather than "unreachable". Decide whether to annotate now or defer.
