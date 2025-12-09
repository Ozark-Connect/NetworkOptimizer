# UniFi Network Analyzer - Claude Code Bootstrap Prompt

## Project Context

I'm building a local-run commercial application called **UniFi Network Analyzer** (working title) that provides AI-powered network configuration analysis, security auditing, and continuous monitoring for UniFi networks. This targets UniFi prosumers, home lab enthusiasts, and small MSPs who want deep insight into their network health without manual config diving.

### Business Context
- **Target price point:** $30-50 one-time or annual license
- **Distribution:** Local-run only (no cloud/SaaS) - appeals to privacy-conscious UniFi users
- **Target audience:** UniFi prosumers, home labbers, small MSPs managing client networks
- **Differentiator:** Ubiquiti gives you data and config options but no intelligence about whether your config is *good*. We fill that gap.

### My Background
- 18+ years software engineering (70% Java/JVM, 30% C#/.NET)
- Currently running an extensive UniFi home lab (UCG-Fiber, Wi-Fi 7 APs, 10GbE backbone, multiple VLANs)
- Active in UniFi Reddit communities for my consulting business (Ozark Connect)
- Built 5K+ lines of custom monitoring code (SeaTurtleMonitor) already integrated with my UniFi infrastructure

---

## Existing Codebase to Leverage

I have several existing projects that contain reusable components:

### SeaTurtleMonitor (.NET 9)
- SNMP polling for UniFi devices (CPU, memory, interface stats)
- InfluxDB time-series storage
- Grafana dashboard integration
- REST API for external integrations
- Linux system agent, Windows gaming agent
- **Reuse:** Monitoring engine, time-series patterns, dashboard concepts

### OzarkConnect-UniFiPortAudit (Python)
- UniFi API integration for network/VLAN mapping
- Port-by-port security analysis
- IoT device detection and VLAN validation
- PDF and Markdown report generation (ReportLab)
- Executive summary with security posture rating
- **Reuse:** Audit logic, report generation patterns, security analysis rules

### SeaTurtleGamingBuddy (.NET 9, Windows Forms)
- UniFi traffic rule management via API
- System tray application UX patterns
- Microsoft.Extensions.Hosting integration
- **Reuse:** UniFi API interaction patterns, tray app UX

### UniFi Cloud Gateway Customization (Shell)
- DNS leak prevention and watchdog scripts
- SQM/QoS management
- On-boot persistence patterns
- **Reuse:** Domain knowledge of UCG pain points, DNS leak detection logic

---

## Technical Decisions Made

### UniFi API Access Strategy
- Use **local-only admin accounts** (trivial auth, no cloud SSO complexity)
- Simulate browser session to use **web UI API backend** (more complete than official API)
- Credentials never leave the user's network
- Target: UCG, UDM, UDM Pro, UDM SE, and self-hosted controllers

### Stack Direction
- **Option A:** .NET 9 (fastest to ship, I know it cold)
- **Option B:** Rust core + .NET wrapper (learning opportunity, better DRM/obfuscation)
- **Option C:** Tauri (Rust backend + React/TypeScript frontend)
- Leaning toward exploring Rust but open to pragmatic .NET-first approach

### Distribution
- Standalone executable OR Docker container (many users have NAS/Proxmox)
- Local-only, no cloud dependencies
- Simple licensing: hardware-bound key with online activation, offline grace period

### Visualization
- Embedded Grafana (bundle it, run in kiosk mode) OR
- Custom dashboards (Tremor/React) for tighter integration
- Ship pre-built dashboards, user doesn't configure anything

---

## Core Feature Categories

### 1. Configuration Audit Engine
Analyze UniFi config and identify issues/improvements:

**Firewall Rules:**
- Shadowed rules (never hit due to earlier rules)
- Orphaned rules referencing deleted groups/networks
- Overly permissive rules (any/any patterns)
- Missing inter-VLAN isolation where expected
- Rule ordering issues

**VLAN Security:**
- Devices on wrong VLANs (based on MAC OUI, traffic patterns, naming)
- Inter-VLAN routing enabled where it shouldn't be
- DNS/gateway leakage across VLANs
- Missing VLAN isolation for IoT/guest networks
- Trunk port misconfigurations

**Port Security:**
- Port profiles not matching connected devices
- Unused ports without proper disable/isolation
- PoE budget issues
- Storm control gaps
- Spanning tree configuration

**Wi-Fi Optimization:**
- Clients stuck on 2.4GHz that support 5/6GHz
- Band steering configuration conflicts
- Channel width settings fighting each other
- Channel overlap/congestion between APs
- Minimum RSSI not configured or misconfigured
- BSS coloring issues (Wi-Fi 6E/7)

**Device Management:**
- Unnamed/poorly named devices
- Device classification suggestions (MAC OUI + traffic fingerprint)
- Stale/orphaned device entries
- Missing device isolation where appropriate

### 2. Continuous Monitoring Engine
Real-time and historical network health:

- Device availability and response times
- Interface utilization and errors
- CPU/memory on UniFi devices
- Client connection quality metrics
- Anomaly detection (unusual traffic patterns, new devices)
- Change detection (config drift, new rules added)

### 3. Reporting & Alerting
- On-demand audit reports (PDF, Markdown, HTML)
- Scheduled health reports (daily/weekly)
- Real-time alerts (email, webhook, Pushover, ntfy)
- MSP-friendly multi-site report aggregation
- Security posture scoring with trend tracking

### 4. Remediation Suggestions (Phase 2)
- Actionable fix suggestions with explanations
- One-click fixes for common issues (with confirmation)
- Config backup before any changes
- Rollback capability

---

## User Experience Goals

### Setup Flow
1. Download/run application
2. Enter controller IP/hostname
3. Create or enter local-only admin credentials
4. Initial scan runs automatically
5. Dashboard shows health overview + top issues

### Daily Use
- System tray icon (Windows) or menu bar (macOS) with status indicator
- Web dashboard accessible at localhost:PORT
- Notifications for new issues or threshold breaches
- Quick access to full audit report

### MSP Use Case
- Manage multiple controllers from single interface
- Per-client reporting and export
- Bulk audit across all managed networks
- White-label report option (Phase 2)

---

## Your Task

Help me design and architect this application. I want to work through:

1. **Architecture Design**
   - Component diagram showing major subsystems
   - Data flow between components
   - Storage strategy (config cache, time-series, audit results)
   - API design for internal communication

2. **Use Case Refinement**
   - Prioritized feature list for MVP vs Phase 2
   - User stories for core workflows
   - Edge cases and error handling scenarios

3. **UX/UI Design**
   - Dashboard layout and information hierarchy
   - Audit report structure and flow
   - Alert/notification UX
   - Setup wizard flow

4. **Technical Decisions**
   - Stack recommendation with tradeoffs
   - Licensing/DRM approach
   - Grafana embed vs custom dashboards
   - Cross-platform considerations (Windows priority, Linux nice-to-have)

5. **Project Structure**
   - Repository layout
   - Module boundaries
   - Build and packaging strategy
   - Development environment setup

Start by asking clarifying questions if needed, then let's iterate on the architecture and feature prioritization. I want to move toward a working MVP that validates market demand before building out the full vision.

---

## Reference: Existing Code Locations

When I give you access to the codebase, these are the relevant repos:
- `SeaTurtleMonitor/` - Monitoring engine (.NET 9)
- `OzarkConnect-UniFiPortAudit/` - Audit logic (Python)
- `SeaTurtleGamingBuddy/` - UniFi API + tray app patterns (.NET 9)
- `unifi-cloud-gateway-customization/` - UCG scripts and domain knowledge

---

## Success Criteria for MVP

1. User can connect to their UniFi controller with local-only credentials
2. Application performs comprehensive config audit
3. Results displayed in clean web dashboard
4. PDF/Markdown report exportable
5. Basic monitoring with historical graphs
6. Works on Windows (Linux/Docker stretch goal)
7. Simple license key validation

Let's build something UniFi users will actually pay for.
