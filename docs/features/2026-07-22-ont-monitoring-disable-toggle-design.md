# Design: One-click Disable/Enable toggle for ONT Device Monitoring

Date: 2026-07-22
Status: Implemented
Author: Ben (with Claude)

## Goal

Let a user pause polling of a single ONT Device Monitoring config from the Settings row with one click, and resume it the same way, keeping the configuration.
Mirrors the Monitoring Interfaces Disable/Enable toggle (#1042), so the two monitoring lists behave the same.

## Motivation

Stale ONT configs (e.g. a stick that was physically swapped out) keep failing every poll cycle and spam the log with timeouts.
Deleting them loses the config; the only existing way to stop the polling is Edit -> uncheck Enabled -> Save (a form round-trip).
A row-level toggle makes pausing a one-click action.

## What already existed

The backend needed almost nothing:

- `OntConfiguration.Enabled` (bool, default true) already exists.
- The poll loop already reads `GetEnabledOntConfigurationsAsync()`, which filters `Where(o => o.Enabled)`, so disabled configs are already skipped.
- The Settings ONT table already shows an Enabled/Disabled status badge, and the edit form already has an Enabled checkbox.

So this is a UI convenience plus a thin, targeted persistence method - no migration, no gateway work, no change to poll or alert logic.

## Changes

### Persistence: `SetOntEnabledAsync(int id, bool enabled)`

Added to `IOntRepository` / `OntRepository` and surfaced on `OntMonitorService` (scoped like `DeleteOntAsync`).
Updates only the `Enabled` flag and `UpdatedAt`; it does not round-trip the whole entity through `SaveOntAsync` (which re-encrypts the password and rewrites every column), so the toggle cannot clobber other fields.
When disabling, it also clears `LastError` so a paused row does not keep displaying an old poll failure; `LastPolled` (history) is preserved.
Re-enabling does not touch `LastError` - the next poll overwrites it - so we never fabricate a healthy state.
Unknown id is a no-op, matching `DeleteOntConfigurationAsync`.

### UI: row toggle button

In the ONT table row action group (Settings.razor), between Test and Delete:

- Enabled config -> "Disable" button (`btn-secondary`).
- Disabled config -> "Enable" button (`btn-primary`).
- One click calls `ToggleOntEnabled`, which flips the flag via `SetOntEnabledAsync`, reloads the list, and shows a per-row spinner (`_ontTogglingId`) meanwhile - the same busy pattern as the existing Test button.

The existing Enabled/Disabled badge is kept as the state indicator (this is how the Monitoring Interfaces card also signals state), so no new row styling was introduced.

## Decisions

- Wording is "Disable/Enable", not "Pause/Resume", for parity with the Monitoring Interfaces button and the existing ONT Enabled/Disabled column (user's call).
- One-click, no confirmation dialog: pausing only stops polling and is trivially reversible (user's call).
- No greyed/muted disabled row: the Monitoring Interfaces card signals disabled state with the badge alone, so this matches it and avoids new CSS.

## Concurrency: an in-flight poll must not resurrect a paused ONT

Surfaced by cross-family review (Codex).
Both poll paths previously persisted the whole entity via `SaveOntConfigurationAsync(config)`, where `config` is the snapshot the poll loop loaded (with `Enabled = true`).
If a poll is in flight (an HTTP round-trip up to 10-15s) when the user clicks Disable, that write-back would copy the stale `Enabled = true` back and re-enable the ONT - and rewrite the `LastError` that Disable just cleared - silently defeating the toggle.

Fix: poll outcomes now go through `UpdateOntPollResultAsync(id, lastPolled, lastError)`, which updates only `LastPolled` (when provided), `LastError`, and `UpdatedAt` - never `Enabled` - and skips entirely (returning false) when the config is disabled by the time it runs.
On the success path a false return also stops the stats cache write, alert evaluation, and Influx write, so a paused ONT stops emitting immediately, even for a poll that was already running.
`LastPolled` is only advanced on success (the error path passes `null`), preserving the "last successful poll" semantics.

The button also disables all row toggles while any one is in flight (`_ontTogglingId != null`), so two overlapping toggles can't interleave their list reloads.

## Tests

`tests/NetworkOptimizer.Storage.Tests/OntRepositoryTests.cs` (new; the repo had no tests):

- Disable clears `LastError`, keeps `LastPolled`, sets the flag false.
- Enable leaves `LastError` for the next poll to overwrite.
- `UpdatedAt` is bumped.
- Unknown id does not throw.
- `GetEnabledOntConfigurationsAsync` returns only enabled configs (the poll-skip guarantee).
- `UpdateOntPollResultAsync` writes a result for an enabled config; the error path leaves `LastPolled` untouched.
- Regression: a config disabled mid-poll is neither re-enabled nor has its cleared `LastError` overwritten by a late poll result.

## Out of scope

- Alerts: a paused ONT is simply not polled, producing no new data or alerts - consistent with today's `Enabled` semantics.
- SFP-attached (`netopt-custom`) supplemental configs already honor `Enabled` the same way.
