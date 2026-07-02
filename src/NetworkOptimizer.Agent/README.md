# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Capabilities: enrollment, a persistent outbound gRPC tunnel with REST
heartbeat fallback, latency/loss probing (with per-WAN source IP binding),
SNMP monitoring relay, UniFi Console proxying over the tunnel, and LAN speed
test serving (OpenSpeedTest page + iperf3). Planned: SSH proxying over the
tunnel.

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
   port (default 8043), heartbeating every 30 seconds; the Multi-Site tab and
   Sites page show it as Online. If the tunnel is unreachable it falls back to
   `POST /api/public/agents/heartbeats` and keeps retrying the tunnel.

The tunnel port only starts listening when multi-site is enabled at server
startup - enable multi-site, then restart the server once. Override the port
with the `AgentTunnel__Port` environment variable on the server, or pin the
full address with `"tunnelUrl"` in `agent.json`.

## Securing the tunnel

The listener itself is cleartext HTTP/2, same as the web UI port: TLS belongs
to the reverse proxy already fronting the central server. Over a site-to-site
VPN, connect directly (`http://vpn-address:8043`). Without a VPN, publish the
tunnel through the same reverse proxy as the web UI (it must speak gRPC -
Caddy: `reverse_proxy h2c://127.0.0.1:8043`, nginx: `grpc_pass`) and set
`"tunnelUrl": "https://agents.example.com"` in `agent.json`. Everything rides
that one TLS session: heartbeats, probe and SNMP traffic (including SNMP
credentials pushed to the agent), and proxied UniFi Console connections -
which are additionally HTTPS end-to-end inside the tunnel.

## Probing

Once connected, the server pushes the site's monitoring targets over the
tunnel and the agent probes them (ICMP/TCP latency and loss, same engine and
cadence as the server's own prober), streaming results back for storage.

## LAN speed test serving

Set `"lanSpeedTest": true` (optionally `"lanSpeedTestPort"`, default 3000) and
the agent hosts the embedded OpenSpeedTest page for the site's clients.
Download/upload endpoints are served by the agent itself; results are relayed
to the central server tagged with the site slug and the client's real IP, so
they land in the site's own database with no CORS or exposure of the central
server to browsers required. If an `iperf3` binary is on the agent's PATH, an
iperf3 server (port 5201) runs alongside for wired/CLI throughput tests.

## Probe-only mode for multi-WAN monitoring

To monitor a secondary WAN, run an additional agent instance with its own IP
(LXC, Docker macvlan, or a small VM) and bind its probes to that IP:

```json
{
  "serverUrl": "https://your-network-optimizer:8042",
  "enrollmentToken": "noa_...",
  "probeSourceIp": "192.0.2.50"
}
```

Then policy-route `192.0.2.50` out the WAN you want measured on the gateway.
Every probe binds to that source (`ping -I` on Linux), so its latency and loss
reflect that WAN specifically. This works on the main site too - enroll the
extra agent against the default site. Source binding needs the native ping
binary, so probe-only agents should run on Linux or macOS.

Secrets at rest: the server stores only SHA-256 hashes of tokens and keys. If
`agent.json` is lost, disable the old agent row and enroll a new one.
