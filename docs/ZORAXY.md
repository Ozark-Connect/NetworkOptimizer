# Network Optimizer behind Zoraxy

How to run Network Optimizer, its speed test, and On-Site Agents behind
[Zoraxy](https://github.com/tobychui/zoraxy). If you have no proxy yet,
[NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy) (Traefik) is the
ready-made option. This guide is for people who already run Zoraxy.

## Where each step happens

Everything on the Zoraxy side is done in its **web admin UI**: no config files to edit, nothing to
restart. Steps 1, 2 and 5 are all point-and-click there.

The one thing outside Zoraxy is **step 3**, which is Network Optimizer's own `.env` on the app host.
The agent needs no special configuration.

## Host or bridge networking for Zoraxy

Either works. We recommend **host mode** (`network_mode: host`) for performance. The examples below
assume host mode or a native install on the same host, so they use `127.0.0.1`.

In bridge mode, `127.0.0.1` inside the container is Zoraxy itself. Use the host's LAN IP as every
Proxy Target, leave `BIND_LOCALHOST_ONLY` unset, and set `TRUSTED_PROXIES` to the subnet of
Zoraxy's Docker network.

## One Zoraxy limitation worth knowing up front

HTTP/2 in Zoraxy is on or off for the whole server, not per hostname. Traefik and Nginx Proxy Manager
can serve HTTP/1.1 to the speed test and HTTP/2 to the app at the same time; Zoraxy cannot. That only
affects the speed test (step 4), not the app or agents. The per-rule **Force HTTP/1.1** option is a
different thing: it covers the hop from Zoraxy to the backend, not the browser's connection.

---

## 1. Get a certificate (Zoraxy admin UI)

**TLS/SSL Certificates > Open ACME Tool**. Enter your email, pick your DNS provider for a DNS-01
challenge (no port 80 needed, and it works for internal-only names), enter the hostname, and click
**Get Certificate**. Then on the **Status** tab, enable **Use TLS to serve proxy request** and set
the inbound port to **443**.

## 2. Add the app rule (Zoraxy admin UI)

**Create Proxy Rules**:

- Domain/Subdomain: `optimizer.example.com`
- Proxy Target: `127.0.0.1:8042`
- Leave **Proxy Target requires TLS Connection** unchecked (the app is HTTP)

WebSockets need no configuration: Zoraxy detects them itself. If your version shows a WebSocket
timeout in the rule's advanced settings, turn on the refresh-on-activity option so the app's
connection is not dropped at the 300-second default.

## 3. Tell the app it is proxied (on the Network Optimizer host, not Zoraxy)

This makes speed test result posting and canonical redirects work. Edit `.env`:

```env
REVERSE_PROXIED_HOST_NAME=optimizer.example.com
BIND_LOCALHOST_ONLY=true      # host mode or native, same host only
TRUSTED_PROXIES=127.0.0.1     # or Zoraxy's address - without this, X-Forwarded-* is ignored
```

Then `docker compose up -d` to apply (native installs: restart the service).

## 4. The speed test: pick one

Zoraxy cannot serve HTTP/1.1 to one hostname and HTTP/2 to another, and HTTP/2 inflates throughput
results:

- **Leave it unproxied** (recommended): reach it directly at `http://<host>:3005`. Results are
  accurate, but without HTTPS the browser does not attach GPS locations to speed test and Signal
  walk data.
- **Proxy it anyway** for HTTPS: rule `speedtest.example.com` -> `127.0.0.1:3005`. You get GPS
  locations and unreliable speed results.
- **Both**: run the [NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy)
  Traefik container bound to a second IP (`LISTEN_IP` in its `.env`) just for the speed test
  hostname, and keep Zoraxy for everything else.

## 5. On-Site Agents (Zoraxy admin UI, skip if not using multi-site)

Agents use the same hostname as the app; only one path goes to the tunnel port instead of the app.
In Zoraxy that is a **Virtual Directory** on the rule you made in step 2:

- Matching path: `/networkoptimizer.agent.v1.AgentTunnel`
- Proxy Target: `127.0.0.1:8043/networkoptimizer.agent.v1.AgentTunnel`
- Tick **Proxy Target requires TLS Connection**
- Tick **Ignore TLS/SSL Verification Error** (the tunnel serves its own throwaway certificate, new
  on every app start, so there is nothing to validate against)
- Leave **Force HTTP/1.1** off: it breaks the tunnel

**Why the path is typed twice:** an agent requests
`https://optimizer.example.com/networkoptimizer.agent.v1.AgentTunnel/Connect`. Zoraxy matches the
virtual directory and removes the matched part before passing the request on, so the app would only
see `/Connect` and reject it. Putting the path on the target as well makes Zoraxy add it back.
Traefik, Caddy and nginx remove nothing, which is why their examples do not repeat it.

The app opens the tunnel port at startup (v2.7.2 and later), so turning on multi-site needs no app
restart.

Nothing needs setting on the agent. `serverUrl` and `tunnelUrl` are both the app's hostname, which
is what the setup one-liner already writes:

```json
{
  "serverUrl": "https://optimizer.example.com",
  "tunnelUrl": "https://optimizer.example.com",
  "enrollmentToken": "noa_..."
}
```

**Long-lived connections:** the tunnel is one connection held open for as long as the agent runs,
so a write deadline on the proxy would cut it on a timer. Stock Zoraxy needs no tuning: its write
timeout ships disabled, and its idle timeout only closes idle connections. These are Zoraxy startup
flags, not admin UI settings, so they only matter if your install passes a non-zero
`-writetimeout`.

**If the tunnel will not stay up** (the agent log repeats a buffered backlog that never drains, or
the path returns 502): try the rule's **disable hop-by-hop header removal** option. Zoraxy strips a
header (`TE`) that gRPC relies on.

**Last resort**, if the tunnel will not pass at all: use Zoraxy's **Stream Proxy** instead, which
forwards raw TCP and does not need to understand gRPC. Listen on, for example, 8443 and forward to
`127.0.0.1:8043`. Then set `"tunnelUrl": "https://optimizer.example.com:8443"` and
`"ignoreSslErrors": true` in the agent's `agent.json`. The cost: `ignoreSslErrors` applies to the
whole agent, so certificate checking is off for its other calls home too.

## 6. Verify

Load the app. No "Reconnecting..." banner means the connection is working. For agents, the site
shows Online in **Settings - Multi-Site**. A tunnel that drops on the same interval every time is a
proxy timeout, not the network.
