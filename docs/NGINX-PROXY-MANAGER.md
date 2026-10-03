# Network Optimizer behind Nginx Proxy Manager

How to run Network Optimizer, its speed test, and On-Site Agents behind
[Nginx Proxy Manager](https://github.com/NginxProxyManager/nginx-proxy-manager) (NPM).
If you have no proxy yet, [NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy)
(Traefik) is the ready-made option. This guide is for people who already run NPM.

## What to know about NPM first

NPM generates one nginx `server` block per proxy host. Four things about it shape the setup:

- **HTTP/2 is per proxy host.** NPM can serve HTTP/1.1 to the speed test hostname and HTTP/2 to the
  app on the same port 443. The speed test needs HTTP/1.1: HTTP/2 multiplexing inflates throughput
  results.
- **The Advanced tab is server-level.** Its text goes into the proxy host's `server` block, so a
  complete `location` block written there is valid. That is how the agent tunnel gets `grpc_pass`.
- **Buffering is on by default.** nginx spools large upload bodies and downloads to temp files. On
  the speed test that disk I/O can hold down multi-gigabit results, so step 4 turns it off.
- **Proxy timeouts are 90 seconds.** That is fine for the app. It is not fine for the agent tunnel,
  which holds one request open for as long as the agent runs, so step 5 raises them.

## Where each step happens

Steps 1, 2, 4 and 5 are in the **NPM admin UI** (port 81 by default). Step 3 is Network Optimizer's
own `.env` on the app host. The agent needs no special configuration.

## Host or bridge networking for NPM

Either works. We recommend **host mode** (`network_mode: host` in NPM's compose file) for
performance: it removes Docker's NAT hop from every proxied byte, which matters most for the speed
test. The choice decides the **Forward Hostname / IP** and two `.env` values:

| NPM runs in | Forward Hostname / IP | `BIND_LOCALHOST_ONLY` | `TRUSTED_PROXIES` |
|-------------|-----------------------|-----------------------|-------------------|
| Host mode, same host (recommended) | `127.0.0.1` | `true` is safe | `127.0.0.1` |
| Bridge mode, same host | the host's LAN IP, e.g. `192.168.1.10` | leave unset | the NPM Docker network's subnet |
| Another box | the Network Optimizer host's LAN IP | leave unset | that box's LAN IP |

In bridge mode, `127.0.0.1` inside the NPM container is NPM itself, not the host. With
`BIND_LOCALHOST_ONLY=true` the app would then listen only where NPM cannot reach it.

Host mode needs ports 80, 443 and 81 free on the host, since NPM binds them directly.

The examples below use `127.0.0.1`. Substitute the host LAN IP for bridge mode or another box. The
agent tunnel port (8043) always listens on all interfaces, so either address reaches it.

---

## 1. Certificates (NPM admin UI)

**SSL Certificates > Add SSL Certificate > Let's Encrypt.** Turn on **Use a DNS Challenge**, pick
your DNS provider, and paste its credentials. A DNS challenge needs no open port 80 and works for
internal-only hostnames.

Request **one certificate per hostname** (`optimizer.example.com`, `speedtest.example.com`), not one
wildcard for both. A browser reuses an open HTTP/2 connection for any other hostname on the same IP
that the certificate also covers. With a shared certificate, speed test requests can ride the app's
HTTP/2 connection and skip the HTTP/1.1 setting entirely. Separate certificates prevent that.

If issuance intermittently fails with `Incorrect TXT record` or `No TXT record found`, raise
**Propagation Seconds**.

## 2. App proxy host (NPM admin UI)

**Hosts > Proxy Hosts > Add Proxy Host.**

Details tab:
- **Domain Names:** `optimizer.example.com`
- **Scheme:** `http`
- **Forward Hostname / IP:** `127.0.0.1` in host mode (see the table above)
- **Forward Port:** `8042`
- **Websockets Support:** on. The web UI runs over a WebSocket; without it the app shows
  "Reconnecting..." in a loop.
- **Cache Assets:** off

SSL tab:
- **SSL Certificate:** the `optimizer.example.com` certificate
- **Force SSL:** on
- **HTTP/2 Support:** on. The agent tunnel is gRPC, which needs HTTP/2 from the agent to NPM.

## 3. Tell the app it is proxied (on the Network Optimizer host, not NPM)

This makes speed test result posting and canonical redirects work. Edit `.env`:

```env
REVERSE_PROXIED_HOST_NAME=optimizer.example.com

# Only if the speed test is proxied (step 4, option A)
OPENSPEEDTEST_HOST=speedtest.example.com
OPENSPEEDTEST_HTTPS=true

# Host mode, same host. See the table above for the other two cases.
BIND_LOCALHOST_ONLY=true

# The address NPM connects from, as the app sees it. Without it, X-Forwarded-* is ignored, and
# every client shares one rate-limit bucket and one Audit Log address.
TRUSTED_PROXIES=127.0.0.1
```

For bridge mode, find the subnet of NPM's Docker network with:

```bash
docker network inspect <npm-network> --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
```

Then `docker compose up -d` to apply (native installs: restart the service).

## 4. The speed test (NPM admin UI, optional)

**Option A: proxy it for HTTPS.** HTTPS is what lets the browser attach GPS locations to speed test
and Signal walk data.

**Add Proxy Host**:

Details tab:
- **Domain Names:** `speedtest.example.com`
- **Scheme:** `http`
- **Forward Hostname / IP:** as in step 2
- **Forward Port:** `3005`
- **Websockets Support:** off. **Cache Assets:** off.

SSL tab:
- **SSL Certificate:** the `speedtest.example.com` certificate (its own, see step 1)
- **Force SSL:** on
- **HTTP/2 Support:** **off.** This is the important one.

Advanced tab, **Custom Nginx Configuration**:

```nginx
# Stream the test data instead of spooling it to disk.
proxy_buffering off;
proxy_request_buffering off;
```

**Option B: leave it unproxied** and reach it at `http://<host>:3005`. Results stay accurate, but
without HTTPS the browser does not provide GPS.

**External WAN speed test server:** the same proxy host settings work, with your VPS as the forward
host (`your-vps:3005`) and a hostname such as `speedtest-wan.example.com`. The DNS record points at
the NPM host, not the VPS.

## 5. On-Site Agents (NPM admin UI, skip if not using multi-site)

Agents use the same hostname as the app. Only the gRPC service path goes to the tunnel port. Open
the **app** proxy host from step 2, go to the Advanced tab, and add:

```nginx
location /networkoptimizer.agent.v1.AgentTunnel/ {
    grpc_pass grpcs://127.0.0.1:8043;   # host LAN IP for bridge mode or another box
    grpc_ssl_verify off;
    grpc_set_header Host $host;
    grpc_read_timeout 1d;
    grpc_send_timeout 1d;
    client_body_timeout 1d;
}
```

- `grpcs://` because the tunnel port serves TLS with a self-signed certificate that changes on every
  app start. There is nothing to verify against, so verification is off. The hop stays encrypted.
- The three `1d` timeouts replace 60-second nginx defaults. Any one of them left at the default
  drops the tunnel on a timer.
- Do not use the **Custom Locations** tab for this. It generates `proxy_pass`, which cannot carry
  gRPC.
- **HTTP/2 Support** must be on for this proxy host (step 2).

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

## 6. Verify

- Load the app. No "Reconnecting..." banner means the WebSocket is working.
- Check the speed test protocol: `curl -v https://speedtest.example.com 2>&1 | grep ALPN` must show
  `http/1.1`. The same command against the app hostname shows `h2`.
- For agents, the site shows Online in **Settings - Multi-Site**. A tunnel that drops on the same
  interval every time is a proxy timeout, not the network. The agent log then repeats a buffered
  backlog that never drains.

## A note on client addresses

NPM's own global config trusts an `X-Real-IP` header sent by clients in `10.0.0.0/8`,
`172.16.0.0/12` and `192.168.0.0/16`. A client on those ranges can therefore choose the address NPM
passes to Network Optimizer, which the app's rate limiter and Audit Log then record. This is NPM's
default, and it is changed on the NPM side (`/data/nginx/custom/http_top.conf`), not in Network
Optimizer.
