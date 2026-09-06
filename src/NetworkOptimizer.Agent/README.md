# Network Optimizer On-Site Agent

Runs at a remote site and reports back to a central Network Optimizer server.
Capabilities: enrollment, a persistent outbound gRPC tunnel with REST
heartbeat fallback, latency/loss probing (with per-WAN source IP binding),
SNMP monitoring relay, TCP proxying into the site over the tunnel (SSH to the
gateway and devices, and the UniFi Console), and LAN speed test serving
(OpenSpeedTest page + iperf3).

The agent connects to a **single URL** - the central server's reverse-proxied
HTTPS address (the same host you open the app at, derived from the server's
`REVERSE_PROXIED_HOST_NAME`, plus `REVERSE_PROXIED_PORT` when the proxy's
front-end port is not 443). Both the enrollment/heartbeat REST calls and the
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
| Multi-WAN monitoring (per-WAN latency, loss, path analysis) | Needs agent - each WAN needs a Vantage bound to an agent that can probe from it (easiest with an agent on the gateway itself); see [Monitoring additional WANs](#monitoring-additional-wans) |

Bottom line: the agent gets you everything with zero new inter-site
infrastructure, and it's the only path for a site you can't already reach. A
site-to-site VPN gets you the management/audit/SNMP floor without an agent; ISP
Health, path discovery, site-vantage latency/loss, multi-WAN monitoring, and
speed tests are the agent's to add.

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
shared router - workstation GC plus a memory limit (256 MB soft, 512 MB hard) as
a safety backstop. In practice it uses ~50 MB; the limit only bounds a worst-case
fault, keeping it well clear of routing and IPS. Even a 2 GB gateway has ample
headroom.

This path is **monitoring-only**: the LAN speed test stays off, because hosting
an nginx/iperf3 speed-test server on the router would compete with the data
plane. For LAN speed testing, run a Docker or bare-metal agent on a separate box
(and size that box to the LAN speed, per above).

It survives UniFi OS firmware upgrades with nothing to re-run: on UniFi OS the
root filesystem is an overlay whose writable upper layer is the persistent
`/data` partition, so the binary, config, and the systemd unit under
`/etc/systemd/system` all live on persistent storage and carry across a firmware
upgrade untouched (the same reason udm-boot survives). The one trade-off to weigh
is isolation: putting the agent on the gateway gives up the "segment the agent
box" hardening described under Security and hardening.

## Install

On the site's agent box, install with Docker, bare-metal (systemd), or a Proxmox
LXC - pick one. All dial out to the central server over HTTPS only, with no
inbound access to the site. Generate the enrollment token in the web UI:
**Settings > Multi-Site > (site) > Agents > Set up agent**.

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

### Proxmox LXC

Run on the **Proxmox VE host**. It creates a small Debian container and runs the
bare-metal installer inside it, so the result is the systemd install above with
its own MAC address (what a per-WAN agent behind a Policy-Based Route needs):

```bash
bash -c "$(curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install-agent.sh)"
```

Every prompt is also a flag (`--server`, `--token`, `--lan-speed-test`, `--ip`, `--vlan`,
`--unattended`, ...); see the script header. To upgrade the agent later, re-run the
bare-metal installer inside the container from the Proxmox host. The enrolled key and
the speed test setting are kept, so only `--server` is needed:

```bash
pct exec <CT_ID> -- bash -c "curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-native.sh | bash -s -- --server 'https://optimizer.example.com'"
```

The Docker and bare-metal scripts accept:

- `--lan-speed-test` - host the LAN speed test page (port 24443) and iperf3 (5201). The only feature that uses nginx.
- `--speed-test-port N` - serve that page on `N` instead of 24443, for a host where 24443 is taken
  or where you would rather use something else (443, if nothing else on the box wants it). Moves
  nginx's listener and records the port in `agent.json`; the agent announces it to the server, so
  in-app speed test links follow with no override needed. Docker equivalent: `AGENT_SPEEDTEST_PORT=N`
  in the container environment.

Both nginx and the agent resolve the port the same way, so the listener and the announcement can
never disagree: the environment variable if set, otherwise `"lanSpeedTestPort"` from `agent.json`,
otherwise 24443. Two consequences worth knowing:

- **An existing install keeps the port it is already serving.** The agent records its whole config
  on enrollment, so agents set up before the page port was configurable have `3000` written there
  and go on using it - an upgrade will not move a page that clients and firewall rules already know
  about. Re-running the installer without `--speed-test-port` preserves it too.
- **To adopt the current default, delete the `"lanSpeedTestPort"` line** from `agent.json` and
  restart the agent. That is also the way out if an agent ever ends up on a port that does not work.
- `--insecure` - accept a self-signed cert on the server's reverse proxy
- `--dir PATH` - override the install directory

The bare-metal installer additionally accepts `--configure-apparmor` - with
`--lan-speed-test`, add a persistent AppArmor exception if the host's nginx profile
blocks the speed test (off by default).

#### Uninstall

All three installers accept `--uninstall` for a clean, verified teardown. It stops
and reaps the agent process (and its `iperf3 -s` child) even when a prior partial
removal left the systemd unit in a `not-found` state, then removes the
service/container, the install dir, and - on bare metal - any AppArmor override the
installer added (the host's own nginx is left untouched). It refuses to report
success while the agent is still running, so a teardown can never silently leave a
stale agent holding a tunnel and relaying data.

```bash
# Docker
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-docker.sh | bash -s -- --uninstall

# Bare metal (systemd)
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-native.sh | sudo bash -s -- --uninstall

# UniFi gateway (on-box)
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/agent/install-agent-gateway.sh | bash -s -- --uninstall

# Docker and bare metal: if you installed with --dir, pass the SAME --dir here -
# uninstall targets the process and files under it. Defaults: Docker
# /opt/network-optimizer-agent, bare metal /opt/netopt-agent. The gateway
# installer has no --dir; it is always /data/netopt-agent.
```

### Re-enrolling an existing agent

If the agent's enrollment is invalidated server-side - removed in the UI
(Settings > Multi-Site > site > Agents > Remove) - the agent stops connecting
and logs `Invalid agent key`. The agent only enrolls when its config has no
`agentKey`, so a stale key must go before a new token is used. Re-running the
installer with a fresh token does this for you. The installer first checks the
existing key against the server: if the server still accepts it, the key is kept
and the token is ignored, so re-running a saved install command (which carries
its original, already-used token) is a safe in-place upgrade. Only a key the
server rejects is replaced by the token:

1. Generate a new enrollment token in the web UI: **Settings > Multi-Site >
   (site) > Agents > Add Agent** (or **Set up agent** for a site with none).
   The install panel's **New token** button issues another if the first one
   lapses; tokens expire one hour after they are issued.
2. Re-run the install command shown there, on the agent box. Passing `--token`
   over an enrolled config discards the old `agentKey` and `siteSlug`, writes
   the new token, and restarts the agent (Docker recreates the container) so it
   enrolls again. Without `--token`, a re-run keeps the existing enrollment and
   only updates the binary or image.

To do it by hand instead, edit `agent.json`: remove the `agentKey` and
`siteSlug` lines, set `enrollmentToken` to the new token, and restart the
agent. Leaving `agentKey` in place means the token is never read.

The config file location depends on the install type:

| Install type | `agent.json` location |
|---|---|
| Docker | `/opt/network-optimizer-agent/data/agent.json` (or the custom `--dir` you specified during install, under `data/`) |
| Bare metal | `/opt/netopt-agent/agent.json` (or the custom `--dir` you specified during install) |
| Gateway | `/data/netopt-agent/agent.json` |

```bash
# Edit the config: delete the agentKey and siteSlug lines, set enrollmentToken
nano <path-to-agent.json>

# Restart - Docker
docker restart network-optimizer-agent

# Restart - bare metal
sudo systemctl restart netopt-agent

# Restart - gateway
systemctl restart netopt-agent
```

On next start the agent exchanges the new token for a fresh key and resumes
normal operation.

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

It accepts `--insecure` and `--uninstall` (clean teardown). The install directory
is fixed at `/data/netopt-agent` (there is no `--dir`): only `/data` survives a
UniFi OS firmware upgrade, so the gateway agent is deliberately not relocatable. See
"Where to run it > On a UniFi gateway" for the footprint, firmware-upgrade
persistence, and the isolation trade-off.

### Speed test listener: TLS, plain HTTP, and reverse proxies

The LAN speed test listener serves self-signed HTTPS by default (a secure
context, so browser geolocation works for GPS-tagged results). Two supported
deviations, and **both require updating the site's speed test URL override in
the central app** (Settings > Multi-Site > the site's Configuration), because
the app builds agent speed-test links from the port the agent announces, defaulting to
`https://<agent LAN IP>:24443`:

- **Plain HTTP opt-out**: set `AGENT_SPEEDTEST_TLS=0` (an environment variable
  on the Docker container, or in the environment when running
  `install-native.sh`) to skip cert generation and serve HTTP on port 24443 -
  e.g. to avoid the self-signed trust prompt or shave TLS overhead on a
  high-throughput LAN. Then set the site's URL override to the matching
  `http://<agent>:24443` address - the announcement carries the port but not the
  scheme, and the app defaults to https.
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
show it as Online. If the tunnel is unreachable (the reverse-proxy gRPC route is
missing, or the server could not bind its listener), it falls back to
`POST /api/public/agents/heartbeats` and keeps retrying the tunnel.

The tunnel listener (default port 8043, `AgentTunnel__Port` on the server) binds
at startup on every install, so enabling multi-site needs no restart. The one
case it does not bind is a server serving its own HTTPS, where its ports cannot
be re-bound alongside the tunnel; it says so at startup, and those installs stay
on REST heartbeats. Put the server behind the reverse proxy instead.

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

The central server never serves TLS itself - it binds plain HTTP on 8042 by
design, and a reverse proxy in front terminates TLS and manages certificates.
That proxy is a prerequisite for agents, not a finishing touch: the agent speaks
HTTPS only and refuses to start against an `http://` server URL.

If you don't already run one,
**[NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy)**
is a ready-to-use Traefik setup (Let's Encrypt certificates via Cloudflare
DNS-01) that ships the agent tunnel route **enabled by default** - point it at
your hostname and there is nothing else to configure for agents.

**Installed it before the agent gRPC route was added?** `setup.sh` only copies the example config
on first run, so yours predates it. Add the route in place - no git checkout
needed:

```bash
curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer-Proxy/main/add-agent-tunnel.sh | bash
```

The rest of this section is for folding the tunnel into a proxy you already run.

The tunnel listener speaks HTTP/2 over TLS with an ephemeral self-signed
certificate: the reverse proxy fronting the central server terminates the
agent's public TLS and re-encrypts to the tunnel port, skipping verification on
that self-signed cert. This keeps the proxy-to-app hop encrypted even when the
proxy runs on a separate box. Because the agent uses one URL, the proxy routes
by path on that single host - the gRPC service path goes to the tunnel port,
everything else to the app:

- gRPC tunnel path: `/networkoptimizer.agent.v1.AgentTunnel/` -> `https://127.0.0.1:8043` (self-signed, skip verification)
- everything else (app + `/api/public/agents/*`) -> `http://127.0.0.1:8042`

The tunnel is one long-lived HTTP/2 request that stays open for as long as the
agent is connected. Most proxies ship a whole-request or request-body read
deadline measured in seconds, which severs a perfectly healthy tunnel on a
timer, so raise or disable that deadline on the route serving the tunnel and
keep HTTP/2 end to end.

**Traefik** - in the static config, clear the read deadline on the entrypoint
that serves the tunnel. Traefik v3 defaults `readTimeout` to 60 seconds and
applies it to the entire request, which resets the tunnel every 60 seconds:

```yaml
entryPoints:
  websecure:
    address: ":443"
    transport:
      respondingTimeouts:
        readTimeout: 0
```

Then in the dynamic config (file provider), add a higher-priority path router
alongside the app router on the same host:

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
    reverse_proxy @grpc https://localhost:8043 {
        transport http {
            tls_insecure_skip_verify
        }
        header_up Host {http.request.host}
    }
    reverse_proxy 127.0.0.1:8042
}
```

Caddy needs no timeout changes for the tunnel - its server read timeouts are
unset by default.

**nginx** - `grpc_pass` keeps the stream on HTTP/2, and the three 60-second
defaults below all have to be lifted or the tunnel dies on whichever fires
first:

```nginx
location /networkoptimizer.agent.v1.AgentTunnel/ {
    grpc_pass grpcs://127.0.0.1:8043;
    grpc_ssl_verify off;
    grpc_set_header Host $host;
    grpc_read_timeout 1d;
    grpc_send_timeout 1d;
    client_body_timeout 1d;
}
location / {
    proxy_pass http://127.0.0.1:8042;
    proxy_set_header Host $host;
}
```
**NPM** - supported, work from the nginx config above, or email tj@ozarkconnect.net and we'll send you instructions. 

**Zoraxy** - supported, but the setup is GUI-driven and there is no written
walkthrough yet. Email tj@ozarkconnect.net and we'll send you instructions.

### Whichever proxy you use

Do not gzip the gRPC path - compression breaks streaming.

A proxy timeout looks like this from the outside: enrollment and the first
configuration exchange both succeed, then the tunnel drops on a fixed interval
and the agent reconnects, while its log repeats a backlog that never drains.

```text
Buffered backlog: 235 result message(s) (~132 KB) will flush once the tunnel connects
```

Probe and SNMP results are buffered on the agent and flushed over the tunnel, so
a stream that keeps resetting leaves the site reporting nothing even though the
agent is up and reconnecting. Pin down the interval - a reset landing on the same
round number every time is a proxy deadline, not a network fault.

Over a site-to-site VPN the same config applies; the proxy is simply reached at
its VPN address, with `"ignoreSslErrors": true` if its certificate does not
match that address.
Everything rides that one TLS session: heartbeats, probe and SNMP traffic
(including SNMP credentials pushed to the agent), and proxied UniFi Console
connections - which are additionally HTTPS end-to-end inside the tunnel.

### After the proxy is up: tell the app its address

Set **`REVERSE_PROXIED_HOST_NAME`** on the central server to the proxy's
hostname (plus `REVERSE_PROXIED_PORT` if the proxy's front end is not on 443),
then restart it. This is what the agent's server URL is derived from, so until
it is set, **Settings > Multi-Site > (site) > Agents** has no address to put in
the install command and shows a placeholder instead. Substituting the app's own
LAN address there does not work: the agent would dial that host on 443, where
the app does not listen and the proxy is not running.

Verify before enrolling an agent - from the site, or anywhere outside the
server's own box:

```bash
curl -sSf https://optimizer.example.com/api/health
```

That has to succeed over HTTPS on the hostname you configured. If it does not,
fix the proxy first; the agent has no fallback to plain HTTP by design.

## Security and hardening

### Protocol security

The agent authenticates with a bearer key, exchanged once during enrollment.
The enrollment token (`noa_...`) is single-use: the agent posts it on first
run, receives a permanent key and site slug, writes both into `agent.json`,
and discards the token. The server stores only SHA-256 hashes of both the
token and the key, so a database leak does not expose usable credentials.

The same key authenticates both the gRPC tunnel (sent in the hello handshake)
and the REST heartbeat fallback (sent in the POST body). A revoked or disabled
key gets a 401 on either path, and the agent logs the rejection. There is no
rotation mechanism short of removing the old agent in the UI and re-enrolling;
`agent.json` is the only copy of the live key.

TLS is mandatory and enforced twice: at startup (the agent refuses a
`serverUrl` or `tunnelUrl` whose scheme is not `https`) and again before
dialing the tunnel. SNMP credentials and proxied console traffic ride the
tunnel, so cleartext is never acceptable. `ignoreSslErrors` disables
certificate validation for self-signed servers - not the TLS layer itself;
even with it set, the channel is encrypted.

### Network posture

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

## Agent resilience

Probes and SNMP polling run for the life of the process, not per-connection.
When the tunnel drops they keep running on the last configuration the server
pushed, and results accumulate in a local buffer. When the tunnel reconnects,
the backlog flushes and the site's monitoring history picks up where it left
off. While the tunnel is down the agent falls back to REST heartbeats (every
30 seconds), so it stays visible as Online on the server.

### Buffered results and acknowledgement

Results are never dropped on write. A write into a black-holed TCP connection
reports success while the bytes sit in the kernel send buffer and get discarded
on teardown - so the agent holds every result until the server explicitly
acknowledges it. On a reconnect, anything unacknowledged replays automatically.

The buffer caps at **12 hours** of data or **64 MB** (whichever comes first),
evicting the oldest entries when either limit is hit - newest data is the most
valuable when the link returns. A typical site's probe and SNMP output fits
comfortably inside both caps; a site with a large target set trims to fewer
hours rather than growing without bound. Drop counts are logged at reconnect
time so you can see whether caps were hit during the outage.

When replaying a large backlog, results are coalesced into bigger batches
(cutting the server's per-batch overhead) while leaving headroom on the
connection for heartbeats and proxied console traffic to get through mid-flush.

### Disk spool

On shutdown (and before a watchdog-forced restart), unacknowledged results are
written to `result-spool.bin` next to the agent binary. On the next start the
spool is restored with original timestamps intact, so the 12-hour age cap
still applies. A truncated or corrupt spool (crash mid-write) keeps whatever
decoded cleanly; nothing beyond the corrupt tail is lost.

systemd delivers SIGTERM to the agent and its child processes (ping probes) at
the same instant, so a probe in flight completes with fewer replies and reads
as real packet loss. To prevent this, the agent drops results from the last
five seconds before spooling - by time, not count, so a long backlog from a
server outage is preserved while only the seconds that straddle the stop are
discarded.

### Dead-connection detection

The tunnel reconnects on a 30-second interval. Each attempt has a 15-second
TCP connect timeout (without it, a powered-off server rides OS SYN retries for
~2 minutes) and a 20-second hello timeout for the post-connect handshake.

The server pushes configuration refreshes every 60 seconds on a healthy
tunnel. If nothing arrives for 150 seconds (2.5 missed cycles), the agent
treats the connection as black-holed and tears it down. Without this, a dead
TCP session would hang for the full OS timeout (~15 minutes) before the
reconnect loop could run. Because unacknowledged results stay in the buffer,
the forced teardown is lossless.

### Async I/O watchdog

On certain vendor kernels (seen on UniFi gateways), the kernel's event-polling
layer can wedge: async socket completions stop being delivered while timers and
synchronous I/O keep working. The agent detects this with a loopback canary -
an async connect to its own listener on 127.0.0.1 every 60 seconds. Since a
loopback connect cannot time out for network reasons, three consecutive
in-process timeouts prove the async engine is dead. The watchdog saves the
result spool and exits for systemd to relaunch.

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
port 24443, or whatever `"lanSpeedTestPort"` records (see above). **nginx** serves the page and the throughput-critical download/upload
legs (sendfile, so it saturates 10 GbE on modest hardware where a .NET server
would go CPU-bound) - the Docker image bundles it, and `install-native.sh`
installs and configures it for the bare-metal install. The .NET agent keeps only
a loopback results relay (nginx proxies the result posts to it), which forwards
them to the central server tagged with the site slug and the client's real IP, so
they land in the site's own database with no CORS or exposure of the central
server to browsers. If an `iperf3` binary is on the agent's PATH, an iperf3 server
(port 5201) runs alongside for wired/CLI throughput tests.

If the host's nginx is confined by AppArmor, it may be denied access to the
install dir and the page won't serve (the agent and monitoring are unaffected).
Re-run `install-native.sh` with `--configure-apparmor` to add a persistent,
scoped exception where the profile supports it; on a host whose profile has no
source file or `local/` hook, an admin must grant the exception, or run the
speed-test agent on a host whose nginx isn't AppArmor-confined.

The address the central server hands to site clients for these tests is the
agent's auto-detected LAN IPv4 (`DetectLocalIpFromInterfaces`). With the default
host networking that is correct; if the agent can't see the real LAN address
(Docker bridge mode, or a multi-NIC host picks the wrong interface), set
`NO_AGENT_LAN_IP=<ip>` in its environment to override it.

## Monitoring additional WANs

Probes leave by the default WAN unless something sends them elsewhere, so a
second or third WAN goes unmeasured until you give it a Vantage. Vantages live on
Monitoring - Network Performance, in the Multi-WAN Monitoring - Vantages card. A
Vantage pairs a WAN with the box that probes it, and the targets you assign to it
are measured over that WAN.

**The easiest box to pair is an Agent on the UniFi Gateway itself.** It reaches each
WAN by that WAN's own interface, so one Agent covers all of them with no routing
to build. Set one up in Settings - Multi-Site.

Any other Agent has to be forced out the WAN you are measuring. Give the Vantage a
Probe source IP, then add a Policy-Based Route in UniFi Network sending that
address out that WAN. UniFi matches a route's source by Client Device, which is a
MAC, so the address needs an interface of its own (an LXC, a VM, or a Docker
container on macvlan). WAN Steering matches on IP instead, so steering the Agent
there sidesteps that entirely.

Source binding rides the native ping binary (`ping -I` on Linux), so an Agent
probing a WAN this way belongs on Linux or macOS. It can also carry a source of
its own, used for any target that arrives without one:

```json
{
  "serverUrl": "https://optimizer.example.com",
  "tunnelUrl": "https://optimizer.example.com",
  "enrollmentToken": "noa_...",
  "probeSourceIp": "192.0.2.50"
}
```

Secrets at rest: the server stores only SHA-256 hashes of tokens and keys. If
`agent.json` is lost, remove the old agent in the UI and enroll a new one.
