# What Network Optimizer Can Do

Everything Network Optimizer does, organized the way the app is navigated. Requirements are in
parentheses where a feature needs more than a UniFi Console connection: Gateway SSH, Device SSH,
an On-Site Agent, AP Telemetry, InfluxDB, or HTTPS.

**Jump to:** [Dashboard](#dashboard) · [Multi-Site](#multi-site) · [Monitoring](#monitoring) ·
[Network Tools](#network-tools) · [Client Performance](#client-performance) ·
[Config Optimizer](#config-optimizer) · [Performance Tweaks](#performance-tweaks) ·
[Change Control](#change-control) · [Firmware Rollout](#firmware-rollout) · [Wi-Fi Optimizer](#wi-fi-optimizer) ·
[Security Audit](#security-audit) · [UPnP Inspector](#upnp-inspector) ·
[Threat Intelligence](#threat-intelligence) · [Web Application Firewall](#web-application-firewall) · [Adaptive SQM](#adaptive-sqm) ·
[WAN Steering](#wan-steering) · [WAN Speed Test](#wan-speed-test) ·
[Client WAN Test](#client-wan-test) · [LAN Speed Test](#lan-speed-test) ·
[Client Speed Test](#client-speed-test) · [Alerts & Schedule](#alerts--schedule) ·
[Settings](#settings) ·
[Supported Hardware](#supported-hardware) · [Accounts and Access](#accounts-and-access) ·
[Integrations and API](#integrations-and-api) · [Installation and Platforms](#installation-and-platforms)

## Dashboard

- **Edit Layout** - move, show or hide, resize (full or half width), and stack cards
- **Quick Stats** - Total Devices, Security Score, Adaptive SQM Status, Security Findings, Threat Events (24h), Wi-Fi Health
- **Active Alerts** - top three open alerts, with Acknowledge and Resolve in place
- **Security Posture**, **Adaptive SQM**, **Threat Trends**, and **Recent Security Findings** cards
- **WAN Speed Test**, **LAN Speed Test**, and **Wi-Fi Optimizer** summary cards
- **Device Status** - every device's IP, model, firmware, uptime, clients, and ping
  - Restart reason on hover: firmware, commanded, power loss, hang, panic, or watchdog (Device SSH)
- **Live View** - the 2D or 3D map embedded on the Dashboard
- Access network cards, with paging across several devices:
  - **Cable Modem Stats (DOCSIS)** - channels, power, SNR, correctable and uncorrectable errors
  - **ONT Stats (GPON / XGS-PON)** (or **Fiber Stats** for Active Ethernet) - optical levels, PON status, SFP health
  - **Cellular Stats (5G / LTE)** - RSRP, RSRQ, SNR, band, cell, tower, neighbors; **Reset Radio** when stuck off 5G
  - **Starlink Stats** - obstruction sky map, loss, power, Ethernet speed, uptime, GPS, last outage
  - Cable modem and ONT error counts cover the last 24 hours, or since reboot (InfluxDB)
- Sites overview (multi-site): site, agent, and agents-online counts

## Multi-Site

- Manage many UniFi sites from one server, each with its own database and InfluxDB buckets
- **Sites** page - per-site cards with console status, license, agents online, Site Health, and quick switch
- Dashboard **Sites** card - every site's Site Health and top issue, worst first; **Less** / **More** views; click a row to switch to that site
- Site switcher in the top bar on every page
- **Add a site** wizard - create the site, connect its console, then set up an [On-Site Agent](#on-site-agent) or skip it
- Onboard a site over an existing site-to-site VPN with no agent
  - Gets Security Audit, Wi-Fi Optimizer, Performance Tweaks, Adaptive SQM, WAN Steering, and SNMP device health
- Onboard a site through an [On-Site Agent](#on-site-agent) for the full feature set, including behind CGNAT
- Per site: rename, disable or enable, remove, and assign users and roles
- Per site: reach the UniFi Console, devices, modems, and ONTs through the agent tunnel
- Per agent site: client speed test target override
- Three sites free for personal use; a lapsed license keeps a site running through a 10-day grace period
- License checks never downgrade a license on failure; a restricted site pauses until relicensed
- Agent management - status, version, **Upgrade** commands, **Run It for Me** gateway upgrade, enrollment tokens
- Per site: Automatic Blocking mode, blocked-source count, and [cross-site blocking](#automatic-blocking) with **Turn On Recommended for All Sites**
- [Trusted Sources](#trusted-sources) - every site's WAN addresses as UniFi Network Lists, so sites admit each other and no one else

### On-Site Agent

- Single outbound HTTPS tunnel per site: no inbound ports, works behind CGNAT
- Proxies the site's UniFi Console, SNMP, and device SSH, fenced to the site's own network
- Latency and loss probes, per-WAN vantage, Upstream Discovery, and Network Tools probes
- SNMP polling buffered across outages
- Per-client WAN usage at the gateway for Bandwidth Hogs
- UniFi Cable Internet DOCSIS capture (agent on the gateway)
- [Automatic Blocking](#automatic-blocking) enforcement on the gateway in seconds, inbound and outbound, with Exceptions (agent on the gateway)
- LAN speed test server (OpenSpeedTest and iperf3) and site-local WAN speed tests
- LAN speed test hosted on the UniFi gateway itself (agent on the gateway, `--lan-speed-test`)
- Buffers up to 12 hours of results to disk through outages; watchdog restarts a stuck agent
- Falls back to HTTPS heartbeats when the tunnel cannot connect; refuses plain HTTP
- **Reconnecting often** - flags an agent whose tunnel keeps dropping, linked to the agent README's reverse proxy notes
- `proxyAllowedCidrs` pins which addresses the tunnel may reach; every dial is logged on the agent host
- Several agents per site, for example one on the gateway and one on a LAN host
- Runs on Docker, systemd, Proxmox LXC, or the UniFi gateway itself
- Used by: [ISP Health](#isp-health), [Network Performance](#network-performance), [Bandwidth Hogs](#live-view),
  [Network Tools](#network-tools), [WAN Speed Test](#wan-speed-test), [LAN Speed Test](#lan-speed-test),
  [Client Speed Test](#client-speed-test), and [UniFi Cable Internet](#supported-hardware) stats

## Monitoring

Time-series monitoring on your own InfluxDB, fed by SNMP polling (InfluxDB).

- Every chart: drag to zoom, synced crosshair across the tab, Ctrl-click a series to isolate it
- Statistics tables under charts with mean, min, max, and latest
- Restart and alert markers on chart time axes
- Saved history stays viewable while the UniFi Console is unreachable
- Devices without SNMP get health from the UniFi API
- Wired clients the console drops stay online while their switch port carries traffic (SNMP)

### Live View

- Live stat tiles: **WAN Download**, **WAN Upload**, **ISP Health**, ISP and transit RTT and loss
- Gateway tiles: RTT, loss, CPU, memory, temperature, **Fabric Ingress**, **Fabric Egress**
- WAN pill selector - one WAN, several to compare, or all
- Live WAN throughput chart
- **3D LAN Flow Map** - live 3D topology with animated download and upload flows for all LAN traffic
  - Overlays: Wi-Fi clients, wired clients, WAN globes, buildings and floor plans from the Signal Map
  - Search by name or MAC, filter by band, WASD and mouse camera, fullscreen
  - **Reposition Device**; run Upstream Discovery from a WAN globe
  - Double-click a client for Client Performance, a switch or gateway for Port Statistics, or a WAN globe for its ISP Health
- **LAN Topology Flow Map** (2D) - the same live data as a pan-and-zoom flow diagram
  - Per-client Wi-Fi LAN traffic, including LAN-only transfers ([AP Telemetry](#ap-telemetry))
- Both maps: device tooltips, full-duplex load coloring, mesh and MLO backhaul, UniFi Device Bridge and Building Bridge links
- Historic playback on both maps - scrub, play, change speed, ranges from 1 hour to 30 days or everything stored (InfluxDB)
- **Bandwidth Hogs** - who is using the WAN now, and who used the most from the last hour to 30 days
  - **WAN** or **LAN + WAN** view; follows the playback timeline
  - Measured at the gateway rather than estimated ([On-Site Agent](#on-site-agent) on the gateway)
- **Port Statistics** - rate, bytes, unicast, multicast, broadcast, errors, and discards per port
- **Add to Dashboard** - pin Live View to the top of the Dashboard
- Advisories: **Flaky Monitoring Targets**, unpolled SNMP devices, Upstream Discovery results ready

### ISP Health

- One score for the internet connection, split into **Access Layer**, **ISP Network**, and **Transit**
- Technology-aware thresholds: GPON, XGS-PON, DOCSIS, PPPoE, Direct Ethernet, Fixed Wireless, Satellite, Cellular, DSL
  - PPPoE detected from the gateway; re-score under a different access technology
- Factors: Speed vs Plan, Idle Latency, Packet Loss, Loaded Latency, Loaded Loss, Physical Link
  - WAN Speed Test loss feeds Packet Loss and Loaded Loss
  - Physical Link reads ONT optics, DOCSIS RF, cellular signal, or the Starlink dish
- **Uptime** and outages, with where the path broke and a per-hop recovery timeline
  - LAN outages kept apart from WAN; **that was me** excludes a self-caused outage
- **Findings** with links into the charts; Smart Queues and Adaptive SQM recommendations
- Per-hop detail: lowest RTT, P90 jitter, loss, address, and reverse DNS
- **Networks on Your Path** - per-ASN grading, including IX peering
- **Path & Congestion Events** - congestion events, path shifts, and path changes
- **Per-Network RTT** chart; BGP tool links (HE BGP Toolkit, bgp.tools, RIPEstat)
- Starlink scoring on sky obstruction, dish loss, and outage burden, with hardware-fault caps
- Per-WAN scores on multi-WAN sites (WAN Vantage)
- **Export PDF** report for any window

### Network Performance

- **Latency & Packet Loss** - RTT, loss, and WAN throughput charts with a stats table
  - Categories: Fabric, ISP, Transit, Internet, Custom; LAN or per-WAN scope
  - Ranges from 15 minutes to 30 days, custom ranges, synced hover, jump to Live View
- **Investigate** - step through **Packet Loss Events** and **Loaded Loss Events**
- **Upstream Path Discovery** - ICMP and UDP traceroutes that map OLT/CMTS, ISP edge, transit ASNs, and internet hosts
  - Sets the access technology, saves monitoring targets, and re-runs every 7 days
  - Detects hops that left the path and asks whether the ISP changed
  - Metered WANs get fewer targets and a slower cadence
- **Flaky Monitoring Targets** - targets losing more than their peers, with **Disable** and **Disable all**
- **Latency Targets** - add, rename, pause, remove, and restore targets
  - ICMP or TCP probes, per-WAN path, interval from 2 seconds to 5 minutes
- **Multi-WAN Monitoring - Vantages** - give each WAN an [On-Site Agent](#on-site-agent) or a policy-routed source address
  - Edit, verify, and assign targets per vantage

### Device Stats

- **Device Health** charts - temperature, CPU, memory, and fan speed for every device on one screen
- Custom OID and Health Check charts
- UniFi power appliances (UPS) shown as Smart Power
- **Temperature Alert Thresholds** for switches and gateways

### SFP Stats

- Every SFP module: RX/TX power, temperature, voltage, and bias current
- Mark a module as PON, Active Ethernet, or regular SFP; monitor or unmonitor it
- **SFP Diagnostics** charts, including PON errors, host link errors, and GEM frames
- PON details: PLOAM state, ONU ID, FEC, ONT uptime; GPON vs XGS-PON detection
- **SFP Anomalies** investigation and **SFP Alert Thresholds**
- Option to ignore single-poll DDM spikes on SFP ONTs
- **Attach to SFP Module** - a Realtek ONT stick or Network Optimizer Custom ONT joins its module as one card
  - **SFP Temp** and **ONT Temp** both shown; choose which one the PON temperature alert checks
  - **Refresh** on the Dashboard reads the module and polls its attached ONT at once

### Cable Modem Stats

- **Cable Modem Signal History** - downstream power and SNR, upstream power, FEC errors per channel
- **Channel Spectrum** - downstream and upstream channels by frequency, colored by SNR (downstream) and power (upstream)
- Modem details (DOCSIS state, locked channels, firmware, uptime, last reinit) and event log

### ONT Stats

- **ONT Signal History** - RX/TX power, temperature, PON errors, host link errors, GEM frames
- PON status and type; **ONT Alert Thresholds**
- **PON Status** table: **Registered**, **OLT** (vendor decoded, with model where reported), **OLT Version**, **Firmware**, and ONT uptime

### Cellular Stats

- **Cellular Signal History** - RSRP, RSRQ, SNR, and signal quality per band and per modem
- Carrier, band, bandwidth, cell ID, roaming, module, and firmware
- LTE and NR5G tracked separately in NSA dual-connectivity mode

### Starlink Stats

- Obstruction sky map from the dish's own SNR grid
- **Starlink Terminal History** - power draw, ping drop rate, sky obstruction, outage seconds, GPS satellites, alignment offset
- Active dish alerts; several dishes per site
- Polled from the dish's local gRPC API, with no Starlink account

### Data Usage

- Per-WAN data caps with a warning level, opened in [Alerts & Schedule - Data Usage](#alerts--schedule)

### Setup

- SNMP auto-detection from the console, with v2c and v3 guidance
- Adopts a changed SNMP community string from UniFi when devices stop answering
- **Set up InfluxDB** wizard - detects InfluxDB, provisions buckets and a least-privilege token
  - 90 days of detail and 365 days of long-term trends
- **Enable Monitoring** / **Disable Monitoring**; InfluxDB test and token update
- **Custom OID Polling** - poll any OID per device or interface, as integer, float, or string
- **Custom Health Checks** - run a command on a device over SSH and act on the result (Device SSH)
  - Parse a number, a regex group, a line count, or the exit code against a threshold
  - Alert, restart a service, kill a process, or reboot the device, with cooldown and daily cap
  - Templates, including "UniFi Network JVM GC Thrash"
- **Monitoring Interfaces** - reboot-safe gateway route to a modem or ONT behind the WAN (Gateway SSH)
  - Management VLAN, alias IP for duplicate-IP WANs, watchdog, SNAT

## Network Tools

- **Run a Probe** - ping, traceroute, or forward and reverse DNS lookup
  - From the server, an [On-Site Agent](#on-site-agent), a WAN vantage, or any UniFi device (Device SSH)
  - ICMP, TCP, or UDP (traceroute only); choose the gateway's WAN interface
  - DNS lookups use the vantage's own resolver, which shows DNS segregation per network
  - **Other Sites** targets: each of your other sites' latest WAN address, for inter-site traces
- **Inspect a Gateway Interface** - **Addresses and DHCP Lease**, **SFP Module**, and **Neighbors** (Gateway SSH)
- **Gateway Diagnostics** - **Run Diagnostics** parses the kernel log into categories (Gateway SSH)
- **Console Support File** - **Generate & Download**, optionally with all applications

## Client Performance

- Detects your own device, or pick any client from the client selector
- Live signal gauge, AP TX and RX rates, and live download and upload
- Wired clients: switch port, link speed, live throughput, and port errors and drops (SNMP)
- VPN clients (Tailscale, Teleport, One-Click and remote-user VPN, site-to-site) get a simplified view with speed tests and path traces
- Jump to the client on the Live View; its own Speed Map and Signal Map are built in
- Rename a client and set or clear its **Fixed IP** in place
- MLO links, AP Lock, channel, and width
- **Roam** - **Change Band** or **Change AP** to move a client during a walk test ([AP Telemetry](#ap-telemetry))
  - On a gateway with built-in Wi-Fi: **Change AP** only, for 5 GHz non-MLO clients
- **Start Logging** - log a remote client's signal at poll rate without GPS
- GPS walk test - signal heatmap from your phone while the page is open (HTTPS)
- **Speed** - live throughput, **Run Speed Test** / **Quick Test**, speed and latency history, result map
- **Signal** - noise, SNR, TX power, radio peers, read-only **Signal Map**, signal history, and log
- **Connection** - **Network Path** with bottleneck, AP history, connection events, and trace changes
- **Data** (Data Usage) - WAN vs LAN + WAN totals, usage chart, and per-application breakdown
  - Rolled up hourly per client and per port, with a 30-day backfill on first run

## Config Optimizer

- **AP Lock Detection** - mobile clients locked to one AP
- **Trunk VLAN Consistency** - trunk links where one side is missing VLANs
- **Port Profile Suggestions** - **Create Profile**, **Apply Profile**, **Extend Profile**
  - 802.1X check for trunk and AP profiles
- **Performance Suggestions**
  - Hardware acceleration, jumbo frames, and flow control settings
  - Known firmware issues on your gateway's version, such as an SQM performance regression
  - Upgrades UniFi leaves to you, such as a newer CyberSecure (Suricata) IDS/IPS engine (shown on the Dashboard)
  - Smart Queues not shaping a WAN
- **WAN Link Speed Keeps Dropping** - a WAN port that keeps renegotiating below its usual speed
- **Cellular Data Savings** - streaming, cloud sync, and game downloads not rate-limited on a cellular WAN

## Performance Tweaks

Gateway tweaks for UCG-Fiber, UXG-Fiber, UCG-Max, and UXG-Max (Gateway SSH).

- **Fan Control Tuning** - lower fan-temperature setpoints
- **MongoDB on SSD** and **PostgreSQL on SSD** - move the database to NVMe (UCG-Fiber, UCG-Max)
- **Logging Offload** - journald to RAM, fewer eMMC writes
- **SFP+ 2.5 Gig SGMII+ Patch** for port 6 or port 7 (Fiber models) - 2.5 Gig links for GPON ONT sticks and MoCA adapters
- Deploy, update, redeploy, remove, or mark as manually deployed; health checks per tweak
- Mutes standard alerts while a deploy step restarts UniFi Network
- **Install UDM Boot**; firmware upgrade impact notes; GPON stick compatibility guide

## Change Control

- UniFi Network keeps whole-console backups (.unf) that restore all at once; Change Control adds per-change history, diffs, and object-level roll back
- **Overview** - what needs attention across every site: regressions, changes no admin made, drift, plans in progress
  - Sites ranked worst first by Site Health
- **Site Health** - one hourly score per site: the share of user-time the network served well
  - Internet (outages, loss, latency, bufferbloat on the primary WAN), device availability, Wi-Fi satisfaction, and LAN errors and loss
  - Separates ISP impairment from local causes (gateway reboot, gateway offline, gateway CPU)
  - Names the top issue in words, with the points it cost and a trend arrow
  - Backfills 90 days from history at first start
- **Changes** - multi-site timeline of every config change, firmware step, and Network app upgrade, with who made it
  - Source: the admin from UniFi's audit log, Network Optimizer's own writes, or "No admin recorded"
  - Site Health lane, and stat lanes on the same time axis (InfluxDB): primary WAN latency and loss, WAN throughput, gateway CPU, Wi-Fi clients by band, and Wi-Fi PHY rate per band
  - Each change shows the fields that moved, Site Health and the Security Audit before and after, and incidents that followed
  - Alert when Site Health falls after a change
  - **Roll Back** one change
- **Snapshots** - the full UniFi Network config, stored encrypted outside the console
  - Taken on every change, daily, before every apply, Firmware Rollout, and Network app upgrade
  - **Compare** any two snapshots or one against live; **Download YAML**; **Pin**; **Roll Back** to a snapshot
  - Baseline and **Drift from Baseline**, with **Return to Baseline** and **Accept as Baseline**
  - **Templates** - save collections from one site, apply them to others, or use one as the Baseline
  - **UniFi Console Backups** - keeps copies of UniFi's own .unf autobackups for download (Site Admin)
- **Edit** - edit the config as YAML with live checks, then **Review Plan**; upload or download YAML files
  - Remove an object with one click; references to it in lists are removed in the same plan
  - Edit a saved template the same way
- **Review Plan** - every write before it runs, with Security Audit impact, lockout checks, and acknowledgements
  - Approvals, scheduled applies, commit-confirmed watch with automatic revert, and a before/after report
  - On agent sites the [On-Site Agent](#on-site-agent) holds the revert if the tunnel drops (On-Site Agent)
- **Migrate**
  - **Restore a Snapshot** onto this site, another site, or a blank UniFi Console
    - Legacy firewall rules restored onto a zone-based console become zone policies, checked by the Security Audit
  - **Replace Device** - move a switch, AP, or gateway's config to new hardware, same model or not
    - Port mapping by role (uplink, LAG, PoE, SFP), radio clamping by band, and a re-cabling sheet PDF
    - Older firmware on the new device links to Firmware Rollout to align it first
  - **Adopt Pending Devices** whose MAC is in a snapshot
  - **Set Up Zones** - group networks into firewall zones by trust or purpose, checked by the Security Audit
    - **Translate Legacy Rules** after UniFi moves a site to zones, from its last legacy snapshot
  - **Re-address Networks** - move networks to new subnets by pattern or per network, with every address in them: reservations, DNS records, port forwards, firewall groups and policies, routes, device static IPs, and Network Optimizer's own settings
    - Addresses on a WAN's carrier side or a VPN's far side stay as they are
    - The dry run lists clients with the address set on the device (from the gateway's DHCP leases, Gateway SSH), telling them from reserved clients that are only idle or offline
    - Warns when Network Optimizer's host, a management network, or the console moves, and names devices that need a new inform address
    - **Lower Lease Times** a lease ahead so clients move within minutes (the move puts them back), and **Reconnect Clients** after it: wireless clients rejoin, PoE-powered wired clients optionally power-cycle
- Alerts for security regressions, changes no admin made, management-path changes, drift, and UniFi Console admin and update settings
- **Settings** - retention, change detection, commit-confirmed window, approvals

## Firmware Rollout

- Upgrade a whole site in waves, timed to the quietest window in your own traffic history
- One canary per model first; the rest wait for its health to match pre-upgrade
- Leaf devices first, gateway last; access points that hear each other never upgrade together
- Release channel per console, device type, or model: **Official (GA)**, **Release Candidate**, **Early Access**
- Exclude devices, models, or device types
- Update the UniFi Network application and UniFi OS as part of the rollout
- Covers UniFi cellular modems; downgrades only when you pick an older build or roll back, and never for UniFi OS or UniFi Network
- Console backup before the rollout; turns off UniFi auto-update schedules that would collide
- Pins a [Change Control](#change-control) snapshot first; pauses for review when the Network app update leaves a security regression or a change no admin made
- After a firmware roll back, **Compare Config Snapshot** against the snapshot taken before the rollout
- **Start now**, **Schedule once**, or **Autopilot** on every new firmware, with a heads-up
- Pace (Conservative, Balanced, Fast), approval at every wave, minimum release age
- Live map and step table; pause, resume, abort, postpone, deploy now, re-plan
- Health gate postpones a start while critical alerts are open
- A dark console or dropped agent pauses the rollout instead of failing it
- Tries SSH when a device ignores the upgrade command
- Optional mute of offline, restart, and WAN outage alerts for each device as it flashes
- Re-scans mesh backhaul once both halves of a mesh pair are upgraded
- Firmware any site's console offers is pooled for every site
- Post-upgrade check for loss, CPU, and memory regression; per-device **Roll back**
- **Deploy Firmware by URL** and **Deploy Known Firmware**
- **History** and **Rollout Report** with before-and-after CPU and memory; **Export PDF**

## Wi-Fi Optimizer

### Overview

- **Wi-Fi Health Score** from signal, channel health, satisfaction, airtime, roaming, and capacity
- **Client Band Distribution**, **Channel Optimization** summary, and per-AP radio cards
- Mesh uplink details and **Re-pair Uplink**
- **Health Issues** with recommendations; **Acknowledge** hides an issue but keeps it in the score
- Health Issue checks:
  - Signal and coverage: weak signal, weak signal population, coverage gaps, sticky clients ([AP Telemetry](#ap-telemetry))
  - Channels: co-channel interference, high power overlap, non-standard 2.4 GHz channel, wide channel with weak clients or unused width
  - Utilization: high utilization (own clients vs interference), raised noise floor ([AP Telemetry](#ap-telemetry)), high TX retry rates
  - Load: high AP load, load imbalance, high 2.4 GHz concentration
  - Clients: legacy client airtime, IoT SSID separation, DHCP issues, high latency despite good signal ([AP Telemetry](#ap-telemetry))
  - Settings: band steering, Roaming Assistant, minimum RSSI, minimum data rates, TX power levels, MLO, 6 GHz disabled
  - Roaming failures
- Deliberately set channels and TX power are noted instead of flagged

### Metrics, RF Environment, and Environment

- **Metrics** - airtime, interference, and TX retry charts per AP and band
- **RF Environment** - neighboring networks, channel density heatmap, cleanest channels, DFS status
  - **Run quick scan** spectrum scan from Channels; per-AP neighbor scans every 30 seconds ([AP Telemetry](#ap-telemetry))
- **Environment** - performance by time of day and weekly interference heatmap

### Channels (Channel Recommendation)

- Current channel map, radios table, and channel issues per band
- **Recommend Best Channels** - a physical RF model of the site, solved for the plan with the least real interference
  - Propagation from floor plan, walls, and antenna patterns, each AP's real transmit power, and contention at the CCA threshold
  - Measured airtime, noise floor, triangulated neighbors, and months of per-channel outcome history beyond the console's retention
  - Searches the whole site, then checks each AP: every move clears an improvement bar and never lands on a measurably worse channel
  - Respects mesh and MLO backhaul, DFS preference (include, avoid, prefer), regulatory limits, and **Pin Channel**
  - Width recommendations from actual client usage ([AP Telemetry](#ap-telemetry))
  - A radio that just moved soaks on its channel unless interference is high
- **Apply Recommended Channels** - writes the plan to UniFi Network in waves, server-side
- Interference measured before and after a move ([AP Telemetry](#ap-telemetry))

### Clients and Coverage

- **Client Stats** - searchable client table, signal and TX retry history, connection events
- **Roaming** - roaming topology, AP pair success rates, 802.11r usage, roaming clients
- **Power/Coverage** - signal distribution, EIRP per AP, coverage quality, overlap
- **Load Balance** - clients per AP and imbalance
- **Connectivity** - success rate per connection stage and per AP
- **Band Steering** - steering opportunities and clients by Wi-Fi generation
- **Airtime** - utilization, airtime by Wi-Fi generation, airtime hogs, TX retries

### Speed Map and Signal Map

- **Speed Map** - speed test results on a map, with AP placement and GPS-accuracy filter
- **Signal Map** - RF propagation heatmap on your own floor plan
  - Buildings and floors; floor plan underlays from PNG, JPEG, HEIC, or PDF
  - Heatmap adjusted by real client signal measurements
  - Walls and doors from a material list (drywall to metal), drawn with snapping
  - Place real APs with mount and facing; add planned APs from the model catalog
  - Simulate TX power, antenna mode, or a disabled AP before changing anything
  - Walk-test signal markers over the heatmap

### AP Telemetry

Opt-in data straight from the access points, turned on in **Settings - AP Telemetry** (Device SSH; Gateway SSH for a gateway). Supports U6 and U7 access points, MIPS
models such as U6-Lite, U6-Mesh, and UAP-AC-Pro, and gateways with built-in Wi-Fi.

- Deploys a small in-memory agent to each AP; redeploys after reboots and firmware updates
- Per-AP deploy, update, repair, remove, exclude, and capability report
- Client Performance shows signal and rates twice a second (once a second from WiFiman), following clients through roams
- Per-client LAN throughput, retries, TCP stalls, and airtime
- Roam records with timing; BSS-transition steering for the **Roam** button
- Requests are HMAC-signed and replay-protected; the agent listens on the management network only
- Never redeployed to an access point while Firmware Rollout is flashing it
- Radio health, neighbor scans every 30 seconds, and channel move detection
- Used by: [Client Performance](#client-performance), [Live View](#live-view), [Health Issues](#overview),
  [RF Environment](#metrics-rf-environment-and-environment), [Channel Recommendation](#channels-channel-recommendation),
  and [radio alerts](#alerts--schedule)

## Security Audit

- Score out of 100 from Firewall Rules, VLAN Security, Port Security, DNS Security, and UPnP Security checks, plus a hardening bonus
- **Firewall Rules** - any-any, overly broad, orphaned, shadowed, and out-of-order rules
  - Missing or bypassed VLAN isolation and internet blocks; missing management access (cloud, firmware, AFC, NTP, 5G/LTE)
  - Networks that can reach the gateway's SSH and web UI; strict once a Management Access network is set
- **VLAN Security** - IoT, printers, cameras, NVRs, and security systems on the wrong VLAN
  - Uses UniFi fingerprints, MAC OUI, port names, and UniFi Protect; checks wired, wireless, and offline clients
  - Covers zone-based firewalls, L3 switch-routed VLANs, and IPv4 and IPv6 separately
  - Networks that should be isolated or have no internet; management devices without fixed IPs
  - **Fix with zones** on isolation findings opens Set Up Zones in [Change Control](#change-control)
- **Port Security** - MAC restriction, port lock, unused ports, port isolation, excessive tagged VLANs, subnet mismatches
- **DNS Security** - DoH setup, DNS leak prevention, DoT, DoQ, and DoH bypass, WAN DNS order, DNAT coverage
  - Third-party DNS detection (Pi-hole, AdGuard Home, Technitium, NextDNS, ControlD), IPv6 DNS bypass
- **UPnP Security** - UPnP enabled, privileged ports exposed, static forwards
- Threat-aware check for actively targeted port forwards
- **Acknowledge** false positives; override a network's **Purpose**, including marking your Management Access network
- **Network Reference**, **DNS Security** table, **Hardening Measures in Place**, **Switch & Port Details**, wireless clients by AP
- **Download PDF** report
- Allow-lists in Settings: streaming devices, media players, TVs, printers, grace periods, trusted DNS redirects

## UPnP Inspector

- Every UPnP mapping and static port forward, grouped by device
- Active, idle, and expiring status; lease times and traffic
- Notes per mapping
- Filter by rule type and protocol; auto-refresh

## Threat Intelligence

- Collects IPS events (IPS v1 and v2), traffic flows, and [Web Application Firewall](#web-application-firewall) events, with history backfill
- Suricata signature enrichment
- **Overview** - totals, threat timeline, kill chain distribution, blocked vs detected
  - **Top Sources**, **Top Targeted Ports**, **Attack Patterns** (scan, brute force, exploit campaign, DDoS)
  - **Protection Setup** - checklist from watching to blocking, with **Turn On Recommended**
- **Blocking** - every blocked source with its reason and end time, plus those shared from your other sites by site; **Release**; each links to its IP details
  - **Public Block List Drops** - outbound connections the gateway dropped, by device, address, and port, with **Allow**
- **Exposure** - port forwards cross-referenced with threats; **Geo-Block Recommendation**
- **Geographic** - country breakdown (MaxMind GeoIP)
- **Attack Sequences** - source IPs moving through two or more kill chain stages
  - **Public Block List Contacts** - devices that connected to, or tried to reach, a listed address
- **Search** - by IP, CIDR, country, or ASN
- IP, port, and protocol drill-downs; **Block** and **Release** any public address from its details
  - Names the public block list an address is on, in its header and in **Destinations Accessed**
  - **Add Exception** for a listed address, or for a LAN device that reached listed addresses
- CrowdSec CTI reputation, known behaviors, and MITRE ATT&CK techniques per IP
  - Lookups cached for 30 days and held to a daily quota
- **Data Retention** setting, 90 days by default; the first collection backfills 30 days
- **Noise Filters** - hide traffic and suppress alerts by IP, CIDR, port, or description
  - Categories: Noise (hidden), Infrastructure, and Trusted User (kept, and listed apart in the Security Audit PDF)
- Time ranges from 1 hour to 90 days, or custom

### Automatic Blocking

Set up in **Settings - Security & Alerts**, per site.

- Blocks the sources the WAF and attack pattern detection flag, with longer bans for repeat offenders
- **Where Blocks Are Enforced** - the gateway, the [Web Application Firewall](#web-application-firewall), and **Cloudflare Edge Blocking**
  - With an [On-Site Agent](#on-site-agent) on the gateway, blocks apply in seconds with no gateway reprovisioning
- Every site starts in **Detection Only**, which writes nothing; **Turn On Recommended** switches it to **Blocking**
  - Picks the **Home** or **Self-Hosting** profile from the site's port forwards; **Business** and **Lockdown** in the profile picker
- **Cross-site blocking** - a source attacking one site is blocked at every site sharing blocks
  - One attack, defended across your whole fleet: an attacker caught at one site is shut out of all of them
  - **Turn On Recommended for All Sites** from **Settings - Multi-Site**; each site's own Trusted Sources still decide what it enforces
- **Public Block Lists** - Spamhaus DROP by default; abuse.ch ThreatFox and Q-Feeds (with your own key), CINS Army, IPsum, blocklist.de Strong IPs, DShield Top 20, abuse.ch Feodo Tracker, and Tor exit relays optional
  - Up to 100,000 entries per site, least useful lists cut first
  - Optional outbound block keeps an infected device from reaching its botnet command server (attacker lists stay inbound only)
  - A device reaching a listed address raises an alert, Detection Only included
- **Exceptions** - let traffic through the public lists, Outbound or Inbound, by device, address, protocol, and port
  - Optional expiry and note; sources Automatic Blocking blocks itself stay blocked
  - Add one from **Public Block List Drops**, an address's details, or a device's drill-down
  - Applied by the [On-Site Agent](#on-site-agent) on the gateway and as Allow policies in UniFi Network
- **What Triggers a Block** thresholds and **Maximum blocked sources**

### WAN Flood Protection

Set up in **Settings - Security & Alerts**, per site, and run by the gateway's [On-Site Agent](#on-site-agent).
Requires UniFi OS 6.0 or later on the gateway.

- Limits unsolicited new UDP and TCP SYN connections to the gateway, per source
- Drops reflection floods from DNS, NTP, memcached, and other amplification services
- Drops a detected flood's source at the WAN, lifted after the flood stops
- Limits new connections per port forward so a flood cannot fill the connection table
- **Off**, **Detection Only**, and **Protecting** modes, with tuning under **Advanced**
- **Never limit or drop** addresses and ranges
- **Blocking** card: per-WAN status, **Active Drops** (**Release** each), **Floods in the Last 7 Days**
- Alerts when a flood starts, the connection table fills, or protection is not enforced
- A **Protection Setup** step wherever the gateway's On-Site Agent can run it

### Trusted Sources

- Every site's WAN IPv4 and IPv6 addresses and public IPv6 prefixes, tracked automatically as they change
- Never blocked anywhere, by any site, on any target
- Published to UniFi Network as Network Lists, kept current through WAN address changes
  - A port forward or firewall policy that admits only your own sites, with no static IPs and no DDNS
  - A **+ Cloudflare** variant for hosts proxied through Cloudflare
- Pushed to the WAF as its `trusted` allow list
- Choose which of your other sites each site trusts; add your own addresses and **Blocking Exceptions**

## Web Application Firewall

netopt-waf, the OWASP CRS firewall in [NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy), in front of Network Optimizer and your self-hosted apps.

- Sees inside HTTPS requests: `.env` and credential probing, injection, path traversal, command execution
- Events land in Threat Intelligence and alerts like IPS events, with what each rule matched
- **Overview** - **Mode** card, **Top Rules** and **Top Hosts**, each opening **Events** filtered to it
- **Events** - every request the WAF flagged: source, host, request, rule, and action
- **Exclusions** - **Add Exclusion** from an event, applied with no restart; built-in sets for common apps
- **Blocking** - the sources Automatic Blocking holds at the WAF
- **Setup** - one WAF per site, reached through the site's [On-Site Agent](#on-site-agent) when routed that way
- Switch between **Detection Only** and **Blocking** from Network Optimizer; newer netopt-waf and CRS releases offered
- Docker `waf` compose profile; bundled with the Windows MSI's Traefik

## Adaptive SQM

Requires Gateway SSH and UniFi Smart Queues.

- Adjusts SQM rates from scheduled speed tests and backs off on latency
- Download and upload shaping, on any two WANs you choose, each configured separately; works on GRE and cellular WAN interfaces
- Connection profiles: DOCSIS, Starlink, GPON, XGS-PON, DSL, Fixed Wireless, Fixed LTE/5G
- **Congestion Schedule** - default or learned profile, range, severity, upload strength
- **Congestion Profile Learning** - learns your line's weekly congestion pattern over 7 days
  - Samples wait for an idle WAN and stay clear of scheduled SQM speed tests
- Morning and evening speed tests, boot delay, ping target, Ookla server override, larger download burst
- **Live SQM Status** per WAN; **Run SQM Adjustment** on demand
- Deploy, deploy settings only, deploy monitor only, remove; persists through reboots
- Logs per WAN for debugging

## WAN Steering

Requires Gateway SSH.

- **Steer by protocol and port** - UniFi's Policy-Based Routes match source device or network and destination IP or domain; WAN Steering adds protocol and port matching, and can split a rule's traffic across WANs by **Ratio**
- Send chosen traffic out a chosen WAN while the rest stays on the primary
- **Traffic Rules** by source CIDR, destination CIDR, source MAC, protocol, and ports
- Each rule sends a **Ratio** of matching connections to its **Target WAN**; VLAN-tagged and GRE WANs
- Rule ordering, enable and disable, hot-reload config deploy
- Health-check failover with backoff; restores rules after reprovisioning
- **Daemon Status** and **WAN Interfaces** views

## WAN Speed Test

- **Gateway (Direct)** - runs on the gateway against Cloudflare or UniFi servers (Gateway SSH)
- From the server or an [On-Site Agent](#on-site-agent) through the LAN
- Any WAN, any combination of WANs, or all at once; **Max Load** for multi-gig lines
- Download, upload, latency, jitter, and loaded latency (bufferbloat)
- Packet loss idle and under load in each direction, on the **Packet Loss** chart tab
- Flags a result as not a valid test when Monitoring saw enough other WAN traffic to explain it (InfluxDB)
- **WAN History** - speed, latency, loaded latency, and jitter charts, filtered by WAN
- Notes, WAN reassignment, **Analyze Path**, and a jump to Live View at the test time

## Client WAN Test

- Browser WAN speed test from any device against your own external OpenSpeedTest server (HTTPS strongly recommended)
- Shareable URL; results identified by device, with a path trace through the WAN hop
- History by device and server
- Deploy command for the external server in Settings

## LAN Speed Test

- iperf3 tests from the gateway and to any UniFi device (Gateway SSH, Device SSH)
- Auto-discovers UniFi devices; add custom devices with their own SSH credentials and iperf3 settings
- Path analysis - every hop, link speeds (LAG-aware), bottleneck, Wi-Fi signal, efficiency, inter-VLAN routing
- Runs from the [On-Site Agent](#on-site-agent) at agent sites
- History with search across any device in the path; firmware versions recorded
- Parallel streams per device type; one test duration

## Client Speed Test

- Bundles OpenSpeedTest and an iperf3 server, and keeps every result for history and analysis
- Browser speed test from any phone, tablet, or laptop, with no SSH
- iperf3 to the server from the command line, from any device; each run is captured and stored
- Results identified by device, with path trace, Wi-Fi channel and MLO links, and personal bests
- **Speed / Coverage Map** - results with GPS location, colored by speed (HTTPS)
  - Place APs, filter by LAN or external, GPS accuracy filter, AP popups

## Alerts & Schedule

- **Schedule** - Security Audit, WAN speed tests, LAN speed tests, and Adaptive SQM learning on a schedule
  - Security Audit runs every 12 hours by default
  - Scheduled WAN tests follow a renamed WAN; a WAN that no longer exists disables its schedule
- **Data Usage** - per-WAN data cap with warning, monthly reset or pay-as-you-go bucket, usage history
  - Keeps counting through gateway reboots and counter resets
- **Active Alerts** - acknowledge and resolve, singly or in bulk
- **History** - every alert by source and severity
- **Rules** - event pattern, threshold, severity, cooldown, escalation, digest-only, target devices
- **Incidents** - related alerts grouped automatically
  - Config changes in the 2 hours before each incident, with **Roll Back** ([Change Control](#change-control))
- Alerts cover:
  - Security Audit score drops and new findings; threat attack chains and patterns; Web Application Firewall events
  - A device reaching, or trying to reach, an address on a public block list
  - WAN port below its usual link speed or at half duplex, and its recovery
  - ONT management unresponsive while its SFP still reads light; ONT rebooted
  - WAN, LAN, and client speed test regressions
  - UniFi Console and On-Site Agent connection loss; an On-Site Agent tunnel that keeps reconnecting
  - Monitoring target offline, sustained loss, full and partial WAN outages
  - SFP levels; Wi-Fi radio stopped transmitting or resetting ([AP Telemetry](#ap-telemetry))
  - Device offline, rebooted, high temperature, gateway CPU and memory; Custom Health Check results
  - Cable modem, ONT, cellular, and Starlink signal and error conditions
  - Data usage warnings and caps; scheduled task failures; Wi-Fi congestion
  - Every Firmware Rollout stage, regression, and rollback
- Fewer false alarms:
  - No offline alert while UniFi reports a device upgrading or provisioning
  - One offline alert per device, however many sources notice
  - On-Site Agent offline alert only after 3 minutes continuously offline
- Delivery: Email (SMTP), Discord, Slack, Microsoft Teams, ntfy, webhooks with HMAC signing
  - Minimum severity per channel; daily or weekly digest
  - Each alert names the site it came from; webhooks retry with backoff

## Settings

- **Connection** (UniFi Console Connection) - local account or API key, site picker, connection test
  - Reconnects on its own after a console outage or restart
- **Gateway SSH** and **Device SSH** credentials; iperf3 status on the gateway
- **Managed SSH Key** - generate or upload one key and install it on the gateway, surviving reboots
- **Monitoring** - SNMP status, InfluxDB, Adaptive SQM monitor, and cable modem, ONT, cellular, and Starlink devices
- **AP Telemetry** - turn on and manage the AP agents; see [Wi-Fi Optimizer - AP Telemetry](#ap-telemetry)
- **Speed Tests** - LAN test defaults and external WAN speed test servers
- **Security & Alerts** - audit allow-lists, alert channels, threat collection, MaxMind GeoIP, CrowdSec CTI
- **Application** - admin password, licensing, pre-release update notices, guided tours, kiosk mode, satellite imagery (Mapbox)
  - Back up and restore all settings or a full export (`.nopt`); clear cached data
- **Identity** and **Audit Log** - see [Accounts and Access](#accounts-and-access)
- **Multi-Site** - see [Multi-Site](#multi-site)
- **Search settings** box across every card
- Floating Save on desktop while a card's own Save is out of view; leaving with unsaved changes asks first

## Supported Hardware

- **UniFi**
  - Consoles: Dream Machines (UDM), Cloud Gateways (UCG), Dream Routers (UDR), Dream Wall (UDW)
  - Consoles: UniFi Express (UX, UX7), Enterprise Fortress Gateways (EFG, EF-Core), CloudKey
  - Consoles: self-hosted UniFi OS Server, or the legacy UniFi Network Server
  - Every UniFi gateway, switch, and access point, with or without SNMP support
  - New models are added as Ubiquiti releases them
- **Cable modems**
  - ARRIS Surfboard - S33, S34, SB8200, SB6183, T25
  - Motorola - MB8611, MB8600, MB7621
  - Netgear - CM600, CM700, CM1000, CM1200, CM2050V (with OFDM/OFDMA)
  - Sagemcom - F3896LG (Virgin Media Hub 5, Ziggo SmartWifi)
  - Technicolor - CGA437A, CGA4233VOO, CGA4322DE, CGA6444VF
  - Vodafone Station - ARRIS TG3442DE
  - Xfinity / Cox / Comcast Business - XB8, XB10, CGM4981, CGA4332
  - UniFi Cable Internet ([On-Site Agent](#on-site-agent) on the gateway)
- **Fiber ONTs**
  - AT&T - BGW320, BGW210
  - Quantum Fiber - Q1000K
  - Nokia - XS-010X-Q
  - Deutsche Telekom - Glasfaser-Modem 2
  - Zyxel - PMG3000-D20B
  - Realtek RTL960x sticks - ODI DFP-34X-2C2, ODI DFP-34X-2IY3, V-SOL V2801F, T&W TWCGPON657, Luleey LL-XS2510
  - 8311 firmware sticks - WAS-110, X-ONU-SFPP, WT-ONU-STICK
  - Any GPON or XGS-PON ONT SFP module with DDM
  - Any other SFP module with DDM
  - Any device through the Network Optimizer Custom JSON contract
- **Cellular**
  - Ubiquiti - U-LTE, U5G-Max, U5G-Backup
  - Netgear Nighthawk - modems and hotspots (MR5200, M6)
  - GL.iNet / Quectel - GL-X3000, GL-XE3000, GL-X2000, GL-E750V2, GL-E5800 (Mudi 7)
  - Inseego - FX4100
  - Zyxel - NR7101, NR7102, NR7301, NR7302, NR7303, NR5103E, NR5103 v2, NR5307
  - Zyxel - FWA505, FWA510, FWA710, LTE3202, LTE5398, LTE7490
- **Satellite**
  - Starlink dishes

## Accounts and Access

- Built-in admin account; named users in **Settings - Identity**
- Roles: Admin, Operator, Viewer, globally or per site
  - Settings that apply to every site need the global Admin role
- A password an administrator sets for someone else must be changed at their next sign-in
- Single sign-on: OpenID Connect, SAML, and UniFi Identity, with claim mapping and just-in-time users
- Require MFA or a passkey per role; SSO-only sign-in
- Authenticator app (TOTP), recovery codes, and passkeys per user; sign out everywhere
- Account lockout after 5 failed sign-ins; sign-in attempts rate-limited per address
- Disabling a user ends their open sessions
- **Audit Log** - every configuration and device change, filterable, exportable to CSV or JSON
  - Also records sign-ins, lockouts, refusals, and Network Optimizer's own automatic actions
  - Keeps 365 days
- Password reset scripts for Docker, macOS, Linux, and Windows

## Integrations and API

- Bring your own Grafana on the InfluxDB buckets; the schema only ever adds fields
  - Every WAN and LAN speed test result exported to the long-term bucket, history included
- Custom ONT stats contract (Network Optimizer Custom) for any device you can script
- OpenSpeedTest and iperf3 wrapped with persistent storage: every run, from any device, kept and analyzed
- Alerts, schedules, audit log, and config backup APIs
- PDF reports for Security Audit, ISP Health, and Firmware Rollout
- Health check endpoint

## Installation and Platforms

- Docker on Linux, Synology, QNAP, and Unraid
- Proxmox VE LXC one-liner, with optional HTTPS and an optional VLAN tag
- Windows MSI with optional speed test server and Traefik HTTPS; setup asks for server IP, hostname, and reverse proxy hostname
- macOS native and Linux native (including ARM64); Proxmox LXC on ARM Proxmox ports
- IPv4 and IPv6 dual-stack; SQLite safe on NFS, SMB, and FUSE storage
- Home Assistant add-ons
- HTTPS through [NetworkOptimizer-Proxy](https://github.com/Ozark-Connect/NetworkOptimizer-Proxy) (Traefik and Let's Encrypt), including the On-Site Agent tunnel route (`add-agent-tunnel.sh`)
- External WAN speed test server for a VPS
- Installable as an app (PWA) on iPhone, iPad, and Android, with pull-to-refresh and a Back button
- Guided tours of what's new in each release
- Encrypted credential storage, with an optional external key file
- Daily log file on every platform (`./logs` on Docker), seven days kept
- Real client addresses behind a reverse proxy or Cloudflare (`TRUSTED_PROXIES`, `TRUST_CF_CONNECTING_IP`)
- Update notices checked by your browser against GitHub; the server sends nothing
- Pre-release (preview) channel
