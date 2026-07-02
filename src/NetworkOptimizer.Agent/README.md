# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Current capabilities: enrollment, a persistent outbound gRPC tunnel with REST
heartbeat fallback. Planned: SNMP monitoring, latency/loss probes with per-WAN
source IP binding, LAN speed test serving (iperf3 + OpenSpeedTest), and SSH /
UniFi Console proxying over the tunnel.

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
   an agent key via `POST /api/public/agents/enrollments`, writes the key,
   site slug, and tunnel address back into `agent.json`, and discards the
   token. It then holds a persistent gRPC tunnel to the server's agent tunnel
   port (default 8043, cleartext HTTP/2 - run it over a VPN or trusted link),
   heartbeating every 30 seconds; the Multi-Site tab and Sites page show it as
   Online. If the tunnel is unreachable it falls back to
   `POST /api/public/agents/heartbeats` and keeps retrying the tunnel.

The tunnel port only starts listening when multi-site is enabled at server
startup - enable multi-site, then restart the server once. Override the port
with the `AgentTunnel__Port` environment variable on the server, or pin the
full address with `"tunnelUrl"` in `agent.json`.

Secrets at rest: the server stores only SHA-256 hashes of tokens and keys. If
`agent.json` is lost, disable the old agent row and enroll a new one.
