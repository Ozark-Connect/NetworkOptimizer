# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Current capabilities: enrollment and heartbeat. Planned (see
`research/multi-site/multi-site-spec.md`): SNMP monitoring, latency/loss probes
with per-WAN source IP binding, LAN speed test serving (iperf3 + OpenSpeedTest),
and an outbound gRPC tunnel that can proxy SSH and UniFi Console access.

## Build

```bash
dotnet publish src/NetworkOptimizer.Agent -c Release -r linux-x64
# also: linux-arm64, win-x64, osx-arm64
```

Produces a self-contained single-file binary.

## Enroll and run

1. In the central server's web UI: Settings > Multi-Site > (site row) > Agents >
   New Agent Token. The token is shown once.
2. On the agent box, create `agent.json` next to the binary:

```json
{
  "serverUrl": "https://your-network-optimizer:8042",
  "enrollmentToken": "noa_...",
  "ignoreSslErrors": false
}
```

`serverUrl` must be reachable from the site (site-to-site VPN address or a
port-forwarded public address).

3. Run the binary (optionally passing the config path, default `agent.json`;
   or set `NO_AGENT_CONFIG`). On first run it exchanges the one-time token for
   an agent key via `POST /api/public/agents/enrollments`, writes the key and
   site slug back into `agent.json`, and discards the token. It then sends
   `POST /api/public/agents/heartbeats` every 30 seconds; the Multi-Site tab
   and Sites page show it as Online.

Secrets at rest: the server stores only SHA-256 hashes of tokens and keys. If
`agent.json` is lost, disable the old agent row and enroll a new one.
