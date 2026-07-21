# Design: Luleey LL-XS2510 as an ONT provider

Date: 2026-07-21
Status: Implemented (config-only support + regression tests); live smoke-test pending
Author: Ben (with Claude), firmware reverse-engineering by a Claude subagent (offline, static)

## Goal

Monitor the Luleey LL-XS2510 GPON-SFP stick - now the live WAN ONT in the UCGF on eth6 - in the NetworkOptimizer "ONT Stats" tab, exactly like the Zyxel PMG3000 and Telekom Modem 2 providers, so its optical health (Rx/Tx power, temperature, ONU state) is polled continuously.

## Verdict up front

The Luleey needs **no new provider and no relay**.
It is stock Realtek RTL960x reference firmware with only branding changed, so the already-shipped `realtek-ont` provider (`RealtekOntProvider`) polls it as-is.
Luleey support is therefore **configuration-only**, plus regression tests that lock the parser against the real Luleey firmware markup.

This overturns the earlier working assumption (a custom-pon SSH/netcat relay, mirroring the Lantiq reference implementation).
See "Why not the custom-pon relay" below.

## The device

Luleey LL-XS2510, 2.5G XPON GPON-SFP stick, Realtek RTL960x, web server Boa/0.93.15, Dropbear SSH.
Factory mgmt IP `192.168.1.1`, factory firmware `V1.0--230303`; forum-recommended for UCGF is SFU 1.0.2 (`241026`).
Live in the UCGF on eth6 with a cloned GPON SN, VLAN 7 PPPoE online (see the [[luleey-ll-xs2510-evaluation]] and [[gpon-sfp-stick-swap]] notes).

## Firmware evidence (offline static analysis)

The three archived firmware images (`M110_sfp_LuLeey_240724` = v1.0.1, `_241026` = v1.0.2, `sfp_Luleey_HGU_250620` = v1.1.4) were unpacked (SquashFS 4.0 / LZMA) and their Boa web root and binaries read directly.
Findings that matter for the provider:

- **Login**: `POST /boaform/admin/formLogin`, form fields `username` / `password` / `save` / `submit-url`.
  The provider also sends `psd` / `challenge`, which this firmware ignores (they are absent from `bin/boa`); Boa reads only what it recognises, so the same POST works.
  Byte-identical `admin/login.asp` across all three firmware versions.
- **Status page**: `GET /status_pon.asp` (top level), present in all three versions.
  Rendered title/heading is literally "PON Status".
  Optical rows are `<tr bgcolor="#DDDDDD">` with English labels `Temperature`, `Voltage`, `Tx Power`, `Rx Power`, `Bias Current` (from `libmultilang_en.so`), values injected by server-side SSI (`<% ponGetStatus("rx-power"); %>` etc).
  The ONU state row is emitted by the compiled `showgpon_status()` as another `<tr bgcolor="#DDDDDD">` row labelled `ONU State` with the value rendered `O%d` (e.g. `O5`).
- **Cookies**: `bin/boa` supports `Set-cookie:`, consistent with the provider's cookie-container session handling.

Every one of these matches `RealtekOntProvider` exactly:
it POSTs the same login, checks the response contains "PON Status", and parses `//tr[@bgcolor='#DDDDDD']` rows by those exact labels.
The provider was written for RTL960x sticks (ODI DFP-34X, V-SOL V2801F, T&W TWCGPON657); the Luleey is the same family.

## What was built

1. `RealtekOntProvider.ParseStatusPon` changed from `private` instance to `internal static` (behaviour-preserving), so it is unit-testable off captured markup - matching the Zyxel provider's testable-parser convention.
2. `tests/NetworkOptimizer.Web.Tests/RealtekOntProviderTests.cs`: five fixture-driven tests over the real Luleey `status_pon.asp` markup (the provider previously had no tests at all).
   They assert optics + negative Rx sign + unit-stripping + ONU state + partial-page + no-throw-on-empty + ConfiguredHost preference.

No `Program.cs` or `Settings.razor` change is needed: `realtek-ont` is already registered and already in the Settings dropdown.

## Configuration (what Ben sets in the UI)

Settings -> ONT Device Monitoring, provider **"Realtek ONT Stick (HTTP)"**:

- Host: the Luleey mgmt IP reachable from the docker host (factory `192.168.1.1`; on the live UCGF it needs the same untagged-secondary-IP reachability trick as the Zyxel `10.10.1.1`, see the Zyxel design doc's Reachability section - the stick's mgmt IP is shadowed by the WAN default route otherwise).
- Port: 80.
- Username / Password: the stick's web credentials (factory `admin` / `admin`; use the real ones set on the live unit).

## Live smoke-test items (two residual risks, verify on the live unit with Ben)

Both are flagged by the firmware analysis as "verify live", not known incompatibilities - no preemptive code change is justified for either:

1. **Dormant Basic-Auth directive**: `boa.conf` declares `Auth /` -> `/var/boaSuper.passwd`, but that passwd file ships in none of the images and nothing creates it at boot; the error string matches a fail-open path.
   Evidence says the form flow is the only real gate, but one live `curl -v http://<host>/status_pon.asp` confirms no `401` challenge precedes the form login.
2. **v1.1.4 first-login gate**: the HGU firmware adds `password_first.asp` / `formPasswordSetup` that forces a password change on default-credential first login.
   Ben's units always run a custom (cloned-SN) config with non-default credentials, so this should not trigger; a single live login on v1.1.4 confirms `formLogin` still accepts a normal `password=...`.

## Minor shipped-behaviour observation (not fixed)

`RealtekOntProvider.ParseStatusPon` assigns `PonType = "GPON"` only after the row loop, so a `status_pon.asp` response with no `#DDDDDD` data rows returns without `PonType` set.
Left as-is deliberately: it is cosmetic (a no-rows response is an empty/failed poll anyway) and changing it would alter output for every Realtek stick, outside this task's scope.
The `ParseStatusPon_NoDataRows_...` test characterises the current behaviour so a future intentional change is a visible diff.

## Why not the custom-pon relay (the earlier idea)

The original plan was a Realtek analogue of the Lantiq reference relay: SSH into the stick, run its ONU CLI, and serve the vendor-neutral PON JSON (`docs/features/netopt-custom-pon-contract.md`) on `:10012` for the `netopt-custom` provider.
Three reasons it is not the right first step for this device:

- **Redundant for the stated goal.** The config-only HTTP scrape already delivers the optics + ONU state the ONT Stats tab needs. The relay's value is the *deep* PON-layer counters (GTC/FEC/BIP/GEM/allocations), not optics.
- **The Realtek CLI likely does not expose those deep counters.** The firmware's `bin/diag` (cparser shell) has symbols for `pon get transceiver` (DDM optics), `gpon get onu state`, and `gpon get serial number` - but no ploamsg/gtcsg/gtctcg-style GTC-counter commands were found. Realtek's stack differs from the Lantiq `onu` CLI the contract's reference implementation targets. A relay would mostly re-serve data the HTTP scrape already gives.
- **It cannot be built or verified safely right now.** The exact `diag` command syntax/output is inferred from symbol names, not execution-confirmed, and confirming it means SSHing into the stick and running diagnostics **on the live WAN ONT** - which risks dropping the connection and must be done with Ben present, not unsupervised.

Parked, not discarded: if Ben later wants the deep counters, the next step is a supervised live session to run `/bin/diag` (`pon get transceiver`, `gpon get onu state`, `gpon get serial number`, and a hunt for any GTC-counter command) and capture real output; only then is a relay worth writing, and only for whatever fields Realtek actually exposes.

## Out of scope

- Auto-detection of the stick (no ONT provider in the codebase does OUI/serial detection).
- Writing to the stick (SN clone / PLOAM / reset) from NetworkOptimizer - read-only telemetry only.
- Automating the eth6 management-IP reachability alias - a documented network prerequisite, identical to the Zyxel.
