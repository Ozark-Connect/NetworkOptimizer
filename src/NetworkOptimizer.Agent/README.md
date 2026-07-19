# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Capabilities: enrollment, a persistent outbound gRPC tunnel with REST
heartbeat fallback, latency/loss probing (with per-WAN source IP binding),
SNMP monitoring relay, TCP proxying into the site over the tunnel (SSH to the
gateway and devices, and the UniFi Console), and LAN speed test serving
(OpenSpeedTest page + iperf3).

The agent connects to a **single URL** - the central server's reverse-proxied
HTTPS address (the same host you open the app at, derived from the server's
`REVERSE_PROXIED_HOST_NAME`). Both the enrollment/heartbeat REST calls and the
gRPC tunnel travel through that one host; the reverse proxy fans them out to the
right port (see [Reverse proxy](#reverse-proxy)). The agent only ever speaks
HTTPS, and never accepts inbound connections - it dials out.

## When you need an agent

The on-site agent is the recommended way to bring a site online: one outbound
HTTPS tunnel, no VPN and no port-forwarding, works behind CGNAT, nothing new to
deploy between sites. It proxies that site's **UniFi Console, SNMP, and device
SSH**, hosts the site's own probes and speed tests, and unlocks the full feature
set.

If you already reach a site over a site-to-site VPN (or a public address), you
can onboard it with no agent and still get a solid floor - management, auditing,
and SNMP work directly. The agent adds the site's own monitoring and performance
layer on top. What works each way:

| Capability | Without an agent (site reachable over VPN) |
|---|---|
| Security Audit | Yes - UniFi Console API + gateway SSH |
| Wi-Fi Optimizer | Yes - Console API |
| Config Optimizer / Performance Tweaks | Yes - gateway SSH |
| Threat Intelligence | Yes - Console API + threat feeds |
| WAN Steering | Yes - gateway SSH |
| Adaptive SQM | Yes - gateway SSH |
| SNMP device health (CPU/mem/temp, interfaces, SFP) | Yes - the server polls SNMP directly |
| Latency / packet-loss probing | Needs agent - probes from *inside* the site; a server-side probe measures the wrong path |
| ISP Health + Upstream Path Discovery | Needs agent - path discovery and latency targets run from the site |
| LAN / WAN / Client speed tests | Needs agent - tests must originate inside the site |
| Cable Modem / ONT / Cellular / Starlink status | Only if the management IP is VPN-routable (often `192.168.100.x` and not); otherwise needs the agent |

Bottom line: the agent gets you everything with zero new inter-site
infrastructure, and it's the only path for a site you can't already reach. A
site-to-site VPN gets you the management/audit/SNMP floor without an agent; ISP
Health, path discovery, site-vantage latency/loss, and speed tests are the
agent's to add.

## Where to run it

The agent is light. It's a self-contained binary - a Docker image or a
bare-metal systemd service, no database and no .NET runtime to install - and
management, SNMP, and probing barely register. A low-power arm64 or amd64 box, a
Raspberry Pi-class SBC, or a spare VM/LXC on a hypervisor or server you already
run is plenty. Both arm64 and amd64 builds are published.

The only part that scales with hardware is **LAN speed testing**, and even that
is forgiving: to measure multi-gig you need a NIC and link that can carry it,
but nginx serves the transfer legs with `sendfile`, so pretty limited hardware
handles 2.5 GbE fine and most multi-core hardware from the last decade saturates
10 GbE without breaking a sweat. Match the box to the LAN speed you actually
want to test.

### On a UniFi gateway (on-box)

You can also run the agent directly on the UniFi gateway itself instead of a
separate site box - any current UniFi OS gateway (UCG, UXG, UDM, UDR, EFG lines).
There is no model gate: the installer checks free memory, not the model, so
any gateway with the headroom works. The published
`linux-arm64` build runs on UniFi OS (Debian, glibc 2.31, systemd); the
`install-agent-gateway.sh` installer (below, and generated for you in the setup
wizard) puts it under `/data` so it persists, with a systemd unit tuned for a
shared router - workstation GC and a memory fence - so it holds a ~50 MB
footprint that can't pressure routing or IPS. Even a 2 GB gateway has ample
headroom.

This path is **monitoring-only**: the LAN speed test stays off, because hosting
an nginx/iperf3 speed-test server on the router would compete with the data
plane. For LAN speed testing, run a Docker or bare-metal agent on a separate box
(and size that box to the LAN speed, per above). Two caveats: the systemd unit
lives on the overlay, so a UniFi OS firmware update drops it - re-run the
installer to reinstate it (it keeps the enrolled key; the binary and config
under `/data` persist) - and putting the agent on the gateway trades away the
"segment the agent box" isolation described under Security and hardening.

## Install

On the site's agent box, install with Docker or bare-metal (systemd) - pick one.
Both dial out to the central server over HTTPS only, with no inbound access to
the site. Generate the enrollment token in the web UI: **Settings > Multi-Site >
(site) > Agents > Set up agent**.

### Docker

```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-docker.sh | bash -s -- \
  --server "https://optimizer.example.com" \
  --token  "noa_..."
```

Pulls the agent image (`ghcr.io/ozark-connect/agent`) and the compose template
(`docker/agent/docker-compose.yml`), writes `agent.json`, and starts the
container with host networking. Config persists in `./data/agent.json` under the
install directory.

### Bare metal (systemd)

```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-native.sh | sudo bash -s -- \
  --server "https://optimizer.example.com" \
  --token  "noa_..."
```

Downloads the self-contained binary (no .NET runtime or Docker), writes
`agent.json`, and installs + starts a `netopt-agent` systemd service under
`/opt/netopt-agent`.

Both scripts accept:

- `--lan-speed-test` - host the LAN speed test page (port 3000) and iperf3 (5201)
- `--insecure` - accept a self-signed cert on the server's reverse proxy
- `--dir PATH` - override the install directory

### On a UniFi gateway (on-box)

To run the agent on the gateway itself (any current UniFi OS gateway)
rather than a separate box, use the gateway installer. It installs to `/data`
(persistent on UniFi OS) with a memory-fenced systemd unit, monitoring-only (no
speed test). UniFi gateways SSH in as root, so no `sudo`:

```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- \
  --server "https://optimizer.example.com" \
  --token  "noa_..."
```

It accepts `--insecure`, `--dir PATH`, and `--uninstall` (clean teardown). See
"Where to run it > On a UniFi gateway" for the footprint and firmware-update
caveats.

### Speed test listener: TLS, plain HTTP, and reverse proxies

The LAN speed test listener serves self-signed HTTPS by default (a secure
context, so browser geolocation works for GPS-tagged results). Two supported
deviations, and **both require updating the site's speed test URL override in
the central app** (Settings > Multi-Site > the site's Configuration), because
the app builds agent speed-test links as `https://<agent LAN IP>:3000` by
default:

- **Plain HTTP opt-out**: set `AGENT_SPEEDTEST_TLS=0` (an environment variable
  on the Docker container, or in the environment when running
  `install-native.sh`) to skip cert generation and serve HTTP on port 3000 -
  e.g. to avoid the self-signed trust prompt or shave TLS overhead on a
  high-throughput LAN. Then set the site's URL override to the matching
  `http://<agent>:3000` address.
- **Your own reverse proxy / TLS in front of the agent**: point the site's URL
  override at the proxy's address (e.g. `https://speedtest.site.example.com`).
  The auto-detected agent LAN IP would otherwise bypass your proxy and hit the
  self-signed listener directly.

If the two sides disagree (agent serving HTTP while the app links `https://`,
or vice versa), the speed test page simply won't load - fix the URL override
to match how the agent actually serves.

Note for the plain-HTTP opt-out: browsers block an https page from posting to
an `http://` LAN address (mixed content), so an HTTP-mode agent cannot receive
the WAN speed test post-back from an external test server - WAN results on
that site lose their client attribution. The opt-out is intended for LAN
speed tests (same-origin) only; keep TLS on agents whose sites run WAN tests
through `/wan/`.

Re-running either script updates the agent in place and preserves the enrolled
key. To build from source instead (development, or an architecture without a
published binary), see below.

## Build from source

```bash
dotnet publish src/NetworkOptimizer.Agent -c Release -r linux-x64
# also: linux-arm64, win-x64, osx-arm64
```

Produces a **self-contained single-file binary** at
`src/NetworkOptimizer.Agent/bin/Release/net10.0/<rid>/publish/NetworkOptimizer.Agent`
- no .NET runtime needed. The only extra dependency is **nginx**, and only when
the LAN speed test is enabled (it serves the OpenSpeedTest page + transfer legs);
`install-native.sh` handles that for you.

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

> **Only enable `ignoreSslErrors` for a self-signed server.** It disables TLS
> certificate validation on the tunnel and result post-back entirely, which
> opens the whole channel to a man-in-the-middle. If your central server has a
> valid (CA-signed) certificate - which it should in production - leave this
> `false` (the default).

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
# /etc/systemd/system/netopt-agent.service
[Unit]
Description=Network Optimizer Agent
After=network-online.target
Wants=network-online.target

[Service]
WorkingDirectory=/opt/netopt-agent
ExecStart=/opt/netopt-agent/NetworkOptimizer.Agent
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now netopt-agent
journalctl -u netopt-agent -f
```

## Reverse proxy

The tunnel listener speaks HTTP/2 over TLS with an ephemeral self-signed
certificate: the reverse proxy fronting the central server terminates the
agent's public TLS and re-encrypts to the tunnel port, skipping verification on
that self-signed cert. This keeps the proxy-to-app hop encrypted even when the
proxy runs on a separate box. Because the agent uses one URL, the proxy routes
by path on that single host - the gRPC service path goes to the tunnel port,
everything else to the app:

- gRPC tunnel path: `/networkoptimizer.agent.v1.AgentTunnel/` -> `https://127.0.0.1:8043` (self-signed, skip verification)
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
    loadBalancer:
      servers: [{ url: "https://127.0.0.1:8043" }]
      serversTransport: agent-tunnel-insecure   # self-signed cert on the tunnel
serversTransports:
  agent-tunnel-insecure:
    insecureSkipVerify: true
```

**Caddy**:

```caddyfile
optimizer.example.com {
    @grpc path /networkoptimizer.agent.v1.AgentTunnel/*
    reverse_proxy @grpc https://127.0.0.1:8043 {
        transport http { tls_insecure_skip_verify }
    }
    reverse_proxy 127.0.0.1:8042
}
```

**nginx**:

```nginx
location /networkoptimizer.agent.v1.AgentTunnel/ {
    grpc_pass grpcs://127.0.0.1:8043;
    grpc_ssl_verify off;
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

## Security and hardening

The agent dials out only, so the site never exposes an inbound port - a real
posture win. The flip side is that the central server it dials into can SSH into
this site's gateway, and a gateway is the LAN router, so that reach is
effectively LAN-wide. That makes the **central server the highest-value target**
in the whole setup, and hardening it the priority:

- **IP-allowlist both planes.** Restrict the admin/management surface *and* the
  agent tunnel endpoint to your sites' public IPs. Commercial sites are stable,
  and residential WAN IPs are sticky enough in practice (often unchanged for a
  year) that this stays maintainable - you touch it only when a site's IP
  actually changes. A stolen `agentKey` used from a random address then dies at
  the firewall before the bearer key is ever presented; the key and
  rate-limiting stay as defense-in-depth behind it.
- **Guard the `agentKey`.** It lives in `agent.json` (file permissions matter)
  and is revocable server-side. Treat it like a credential.
- **Keep TLS real.** Leave `ignoreSslErrors` at its default `false`; only enable
  it for a self-signed server, and know it opens the whole channel to a MITM.

A compromised central server is game-over for the gateways it manages, and that
is inherent to centralized gateway management, not a flaw of the tunnel - no
protocol trick changes it, which is exactly why protecting the server is the
whole ballgame.

**Segment the agent box (recommended).** Put the agent on a management VLAN and
firewall it to need-only reach. Outbound (WAN): HTTPS (443) to the central
server for the tunnel, ICMP for latency/loss probing and path discovery, and
HTTP 80/8080 plus 443 if you run WAN speed tests - any other outbound can be
locked down. On the LAN: only the targets the features you turned on actually
touch - UniFi Console, gateway/device SSH, any modem/ONT/cellular/Starlink
status pages, and your probe/speed-test targets. This is the network-side
complement to `proxyAllowedCidrs` below: that pins what the server can reach
*through* the agent; this caps what the agent box can reach *on your network* if
it is ever compromised.

Running the agent *on* the gateway (see "Where to run it > On a UniFi gateway")
trades this segmentation away by design - the agent then lives on the router
itself rather than a box you can fence off. That is a deliberate
convenience-vs-isolation choice, so weigh it against the hardening above.

Coming soon: the Security Audit will review the agent's own firewall placement,
and Network Optimizer will be able to deploy a recommended least-privilege
firewall configuration automatically, based on the agent's detected VLAN and
infrastructure.

### What the agent enforces on its own

Three controls are agent-owned - nothing the server sends over the tunnel can
change them:

- **Site-local proxy fence (built in, always on).** The tunnel's TCP proxy
  refuses to dial anything that is not a site-local address (RFC1918, IPv6
  unique-local, IPv6 link-local). Everything the proxy legitimately reaches -
  the UniFi Console, gateway/device SSH, modem/ONT/hotspot status pages - is
  site-local, so normal setups never notice. What it closes is the quiet abuse
  a compromised central server would otherwise get for free: using your site
  as an exit node to relay attacks at third parties. Hostnames are resolved
  once, every resolved address is checked, and the connection goes to the
  checked address, so DNS tricks can't split the check from the dial.
- **Operator pinning (`proxyAllowedCidrs` in `agent.json`, optional).** A list
  of IPs/CIDRs that fully replaces the built-in fence. Pin it to narrow the
  server's reach through the proxy to exactly the addresses you list (e.g.
  just the management VLAN) - or to admit an exotic public-IP target, which is
  the only escape hatch that exists. If you pin, include every subnet holding
  the UniFi Console, the gateway, any devices used for SSH/speed tests or as
  probe vantages, and modem/ONT/hotspot status pages - anything outside the
  pin fails with a logged denial. An invalid entry aborts agent startup rather
  than running half-pinned.
- **Dial audit trail.** Every proxy dial (allowed or denied) is one line in
  the agent's journal, with the target and connection id. The central server
  cannot suppress or rotate it, so the site always has its own record of what
  was reached through the tunnel: `journalctl -u netopt-agent | grep "Proxy dial"`.

Honest scope: with gateway SSH credentials configured, a compromised central
server still owns the LAN through the gateway - these controls close the
internet-relay vector, cap what the proxy path can reach, and leave evidence;
they do not (cannot) contain gateway-credential pivoting. That containment
story remains server-side hardening, above.

One control we deliberately did **not** build, so the reasoning is on record:

- **Pinning the gateway's SSH host key** is impractical here: UniFi regenerates
  host keys on firmware upgrades (and adoption/factory reset), so a strict pin
  would break SSH after routine updates and train operators to click through
  warnings. The residual risk it would guard - a rogue agent presenting a fake
  gateway - is better addressed at the tunnel (guard the key, IP-allowlist, and
  one-tunnel-per-key), with at most a soft "host key changed" alert that never
  blocks.

Also out of scope by design: filtering SSH *commands* at the agent. The proxied
SSH session is encrypted end-to-end between the central server and the
gateway's sshd; the agent pumps opaque bytes and cannot inspect them. Command
safety lives server-side (parameterized command construction), and the
gateway-side option (`authorized_keys` forced commands) is gateway
configuration, not agent code.

## Local dev / testing

Build for the site box's architecture and copy the single binary over - no build
tools needed on the box:

```bash
# from the repo root
dotnet publish src/NetworkOptimizer.Agent -c Release -r linux-x64   # or linux-arm64

scp src/NetworkOptimizer.Agent/bin/Release/net10.0/linux-x64/publish/NetworkOptimizer.Agent \
    agent.json user@sitebox:/opt/netopt-agent/

ssh user@sitebox 'cd /opt/netopt-agent && chmod +x NetworkOptimizer.Agent && ./NetworkOptimizer.Agent'
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

Set `"lanSpeedTest": true` and the site's clients get an OpenSpeedTest page on
port 3000. **nginx** serves the page and the throughput-critical download/upload
legs (sendfile, so it saturates 10 GbE on modest hardware where a .NET server
would go CPU-bound) - the Docker image bundles it, and `install-native.sh`
installs and configures it for the bare-metal install. The .NET agent keeps only
a loopback results relay (nginx proxies the result posts to it), which forwards
them to the central server tagged with the site slug and the client's real IP, so
they land in the site's own database with no CORS or exposure of the central
server to browsers. If an `iperf3` binary is on the agent's PATH, an iperf3 server
(port 5201) runs alongside for wired/CLI throughput tests.

The address the central server hands to site clients for these tests is the
agent's auto-detected LAN IPv4 (`DetectLocalIpFromInterfaces`). With the default
host networking that is correct; if the agent can't see the real LAN address
(Docker bridge mode, or a multi-NIC host picks the wrong interface), set
`NO_AGENT_LAN_IP=<ip>` in its environment to override it.

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
