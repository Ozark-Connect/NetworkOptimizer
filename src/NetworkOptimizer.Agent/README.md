# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Capabilities: enrollment, a persistent outbound gRPC tunnel with REST
heartbeat fallback, latency/loss probing (with per-WAN source IP binding),
SNMP monitoring relay, UniFi Console proxying over the tunnel, and LAN speed
test serving (OpenSpeedTest page + iperf3). Planned: SSH proxying over the
tunnel.

The agent connects to a **single URL** - the central server's reverse-proxied
HTTPS address (the same host you open the app at, derived from the server's
`REVERSE_PROXIED_HOST_NAME`). Both the enrollment/heartbeat REST calls and the
gRPC tunnel travel through that one host; the reverse proxy fans them out to the
right port (see [Reverse proxy](#reverse-proxy)). The agent only ever speaks
HTTPS, and never accepts inbound connections - it dials out.

## Build

```bash
dotnet publish src/NetworkOptimizer.Agent -c Release -r linux-x64
# also: linux-arm64, win-x64, osx-arm64
```

Produces a **self-contained single-file binary** at
`src/NetworkOptimizer.Agent/bin/Release/net10.0/<rid>/publish/NetworkOptimizer.Agent`.
It embeds the OpenSpeedTest page, so the site box needs nothing installed - no
.NET runtime, no Docker.

## Enroll and run

1. In the central server's web UI: **Settings > Multi-Site > (site) > Agents >
   Set up agent**. Copy the enrollment token - it is shown once.
2. On the agent box, create `agent.json` next to the binary:

```json
{
  "serverUrl": "https://optimizer.example.com",
  "tunnelUrl": "https://optimizer.example.com",
  "enrollmentToken": "noa_..."
}
```

`serverUrl` and `tunnelUrl` are the **same** URL: the central server's HTTPS
address as reachable from the site - over a site-to-site VPN or a public
address. The agent refuses anything but HTTPS. Self-signed certificates work
with `"ignoreSslErrors": true`; plain `http://` never does.

3. Run the binary (optionally pass a config path, default `agent.json`; or set
   `NO_AGENT_CONFIG`):

```bash
chmod +x NetworkOptimizer.Agent && ./NetworkOptimizer.Agent
```

On first run it exchanges the one-time token for an agent key via
`POST /api/public/agents/enrollments`, writes the key and site slug back into
`agent.json`, and discards the token. It then holds a persistent gRPC tunnel to
the server, heartbeating every 30 seconds; the Multi-Site tab and Sites page
show it as Online. If the tunnel is unreachable (server-side tunnel not enabled,
or the reverse-proxy gRPC route is missing), it falls back to
`POST /api/public/agents/heartbeats` and keeps retrying the tunnel.

The tunnel listener (default port 8043, `AgentTunnel__Port` on the server) only
starts when multi-site is enabled at server startup - enable multi-site, then
restart the server once.

### Run as a service (systemd)

```ini
# /etc/systemd/system/no-agent.service
[Unit]
Description=Network Optimizer Agent
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/no-agent
ExecStart=/opt/no-agent/NetworkOptimizer.Agent
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now no-agent
journalctl -u no-agent -f
```

## Reverse proxy

The tunnel listener is cleartext HTTP/2 (h2c), same idea as the web UI port:
TLS belongs to the reverse proxy already fronting the central server. Because
the agent uses one URL, the proxy routes by path on that single host - the gRPC
service path goes to the h2c tunnel port, everything else to the app:

- gRPC tunnel path: `/networkoptimizer.agent.v1.AgentTunnel/` -> `h2c://127.0.0.1:8043`
- everything else (app + `/api/public/agents/*`) -> `http://127.0.0.1:8042`

**Traefik** (file provider) - add a higher-priority path router alongside the
app router on the same host:

```yaml
routers:
  optimizer:
    rule: "Host(`optimizer.example.com`)"
    entryPoints: [websecure]
    service: optimizer
    tls: { certResolver: letsencrypt }
  optimizer-agents:
    rule: "Host(`optimizer.example.com`) && PathPrefix(`/networkoptimizer.agent.v1.AgentTunnel/`)"
    priority: 100
    entryPoints: [websecure]
    service: agents
    tls: { certResolver: letsencrypt }   # no compression middleware on this one
services:
  optimizer:
    loadBalancer: { servers: [{ url: "http://127.0.0.1:8042" }] }
  agents:
    loadBalancer: { servers: [{ url: "h2c://127.0.0.1:8043" }] }
```

**Caddy**:

```caddyfile
optimizer.example.com {
    @grpc path /networkoptimizer.agent.v1.AgentTunnel/*
    reverse_proxy @grpc h2c://127.0.0.1:8043
    reverse_proxy 127.0.0.1:8042
}
```

**nginx**:

```nginx
location /networkoptimizer.agent.v1.AgentTunnel/ {
    grpc_pass grpc://127.0.0.1:8043;
}
location / {
    proxy_pass http://127.0.0.1:8042;
}
```

Do not gzip the gRPC path - compression breaks streaming. Over a site-to-site
VPN the same config applies; the proxy is simply reached at its VPN address,
with `"ignoreSslErrors": true` if its certificate does not match that address.
Everything rides that one TLS session: heartbeats, probe and SNMP traffic
(including SNMP credentials pushed to the agent), and proxied UniFi Console
connections - which are additionally HTTPS end-to-end inside the tunnel.

## Local dev / testing

Build for the site box's architecture and copy the single binary over - no build
tools needed on the box:

```bash
# from the repo root
dotnet publish src/NetworkOptimizer.Agent -c Release -r linux-x64   # or linux-arm64

scp src/NetworkOptimizer.Agent/bin/Release/net10.0/linux-x64/publish/NetworkOptimizer.Agent \
    agent.json user@sitebox:/opt/no-agent/

ssh user@sitebox 'cd /opt/no-agent && chmod +x NetworkOptimizer.Agent && ./NetworkOptimizer.Agent'
```

Run it in the foreground first to watch enrollment and the tunnel connect, then
install the systemd unit above. To re-test enrollment from scratch, remove the
agent in the UI (Settings > Multi-Site > site > Agents > Remove), delete the
`agentKey`/`siteSlug` from `agent.json` (or just delete the file and recreate it
with a fresh token), and run again.

## Probing

Once connected, the server pushes the site's monitoring targets over the tunnel
and the agent probes them (ICMP/TCP latency and loss, same engine and cadence as
the server's own prober), streaming results back for storage.

## LAN speed test serving

Set `"lanSpeedTest": true` (optionally `"lanSpeedTestPort"`, default 3000) and
the agent hosts the embedded OpenSpeedTest page for the site's clients.
Download/upload endpoints are served by the agent itself; results are relayed to
the central server tagged with the site slug and the client's real IP, so they
land in the site's own database with no CORS or exposure of the central server to
browsers required. If an `iperf3` binary is on the agent's PATH, an iperf3 server
(port 5201) runs alongside for wired/CLI throughput tests.

## Probe-only mode for multi-WAN monitoring

To monitor a secondary WAN, run an additional agent instance with its own IP
(LXC, Docker macvlan, or a small VM) and bind its probes to that IP:

```json
{
  "serverUrl": "https://optimizer.example.com",
  "tunnelUrl": "https://optimizer.example.com",
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
`agent.json` is lost, remove the old agent in the UI and enroll a new one.
