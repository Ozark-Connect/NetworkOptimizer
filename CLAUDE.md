# Claude Code Project Notes

## Git Workflow

**IMPORTANT: Never merge feature branches to main automatically.**

**IMPORTANT: DO NOT commit hacks, tests, or experimental code to main.** Use a feature branch for testing. Main must always contain production-ready code.

When working on a feature branch:
1. Stay on the feature branch until explicitly told to merge
2. When asked to "deploy", push and deploy the **current branch** - do NOT merge to main first
3. Only merge to main when the user explicitly requests it (e.g., "merge to main", "ship it", "merge and deploy")

Both NAS and Mac deployments pull from remote, so push the current branch before deploying:
```bash
git push -u origin feature-branch-name
```

The deployment targets will pull whatever branch is pushed. This allows testing features before merging to main.

## Git Worktrees

The repo uses git worktrees to allow working on multiple branches simultaneously without switching.

**Directory structure:**
```
NetworkOptimizer/
├── main-work/          # main branch
├── dev-work/           # dev branch (persistent)
├── feature-xyz/        # feature/xyz branch (temporary)
└── bugfix-abc/         # bugfix/abc branch (temporary)
```

**Branch usage (gitflow naming):**
- `main-work/` → `main` branch - Production-ready code
- `dev-work/` → `dev` branch - Accumulates small fixes; periodically merged to main
- `feature-xyz/` → `feature/xyz` branch - Larger features that need isolation; removed after merge
- `bugfix-xyz/` → `bugfix/xyz` branch - Bug fixes; removed after merge

### Create a New Feature Worktree

```bash
# From main-work directory
cd C:\Users\tjvc4\OneDrive\StartupProjects\NetworkOptimizer\main-work

# Create new branch and worktree in one command (gitflow naming: feature/name)
git worktree add ../feature-xyz -b feature/xyz

# Or for an existing remote branch
git worktree add ../feature-xyz feature/xyz

# Copy untracked and ignored files (e.g., .env, scripts/local-dev/) to the new worktree
pwsh ./scripts/local-dev/copy-untracked.ps1 ../feature-xyz -IncludeIgnored
```

### List Worktrees

```bash
git worktree list
```

### Remove a Worktree

After merging a feature branch:
```bash
# Remove the worktree directory
git worktree remove ../feature-xyz

# Delete the branch (local and remote)
git branch -d feature/xyz
git push origin --delete feature/xyz

# Or manually delete and prune
rm -rf ../feature-xyz
git worktree prune
```

### Best Practices

- Keep `main-work/` on main - don't switch branches there
- Create a new worktree for each feature branch
- Use gitflow branch names (`feature/xyz`, `bugfix/xyz`) with kebab-case folder names (`feature-xyz/`, `bugfix-xyz/`)
- Remove worktrees after branches are merged
- Each worktree has its own working directory state (uncommitted changes, node_modules, etc.)
- Always run `pwsh ./scripts/local-dev/copy-untracked.ps1 ../new-worktree -IncludeIgnored` after creating a worktree to copy local files

## Merge & Release Procedure

When preparing to merge a feature branch to main:

1. Clean up commit history on feature branch (squash/rebase if needed)
2. Force push cleaned commits to feature branch
3. Create/update PR with description
4. **Ask: "Are we planning to release now?"**
5. If releasing:
   - Draft a GitHub release with notes covering all changes **since the last release tag** (not just this PR)
   - Use `gh release create vX.Y.Z --draft --title "vX.Y.Z" --notes "..."`
   - Release notes should be ready before merge
6. User reviews and merges PR to main
7. After merge, tag and push:
   ```bash
   git fetch origin main && git tag vX.Y.Z origin/main && git push origin vX.Y.Z
   ```
8. Pipeline automatically publishes the draft release matching the tag

**IMPORTANT:** Do NOT publish releases manually - the pipeline handles this when the tag is pushed.

## Building Windows Installer (MSI)

**IMPORTANT:** Build the MSI **after** pushing the release tag so MinVer picks up the correct version.

```powershell
# Build the installer (publishes app, downloads dependencies, creates MSI)
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

Output: `publish/NetworkOptimizer-{version}-win-x64.msi`

Get SHA256 hash for release notes:
```bash
certutil -hashfile publish/NetworkOptimizer-{version}-win-x64.msi SHA256
```

Upload MSI to draft release:
```bash
gh release upload vX.Y.Z "publish/NetworkOptimizer-X.Y.Z-win-x64.msi"
```

Verify upload integrity (download and compare SHA256):
```bash
gh release download vX.Y.Z --pattern "*.msi" --dir /tmp/verify-msi --clobber && certutil -hashfile /tmp/verify-msi/NetworkOptimizer-X.Y.Z-win-x64.msi SHA256
```

The script automatically:
1. Publishes the app as self-contained for win-x64
2. Downloads nginx and iperf3 if not present
3. Builds the WiX installer with version from MinVer (based on latest git tag)

## Pre-PR Checklist

Before creating or updating a pull request, ensure the following:

1. **Zero build warnings** - Run `dotnet build` and verify `0 Warning(s)` in the output
2. **All tests pass** - Run `dotnet test` and confirm all tests pass
3. **No unintended changes** - Review `git diff` to ensure only intended changes are included

Build warnings must be fixed, not suppressed, unless there's a documented reason. Common warning fixes:
- CS8601/CS8602 (null reference): Use null-coalescing (`??`) or null-conditional (`?.`) operators
- CS8634 (nullability constraint): Adjust generic type parameters or use `null!` for intentional null in tests
- xUnit1031 (blocking operations): Convert to `async Task` and use `await`

## Local Development (Windows)

**Note:** Local Docker container removed for security. Use NAS deployment for testing.

### Run with Docker
```bash
cd docker
docker compose build network-optimizer
docker compose up -d
```

### Run without Docker
```bash
cd src/NetworkOptimizer.Web
dotnet run
```
Access at http://localhost:5000

### Build Only
```bash
dotnet build
```

### Publish
```bash
cd src/NetworkOptimizer.Web
dotnet publish -c Release -o C:/NetworkOptimizer
```

## macOS Testing/Development

macOS doesn't support `network_mode: host`, so use the macOS-specific compose file:

```bash
cd docker
docker compose -f docker-compose.macos.yml build
docker compose -f docker-compose.macos.yml up -d
```

Access at http://localhost:8042 (wait ~60 seconds for startup)

**No .env file required** - defaults work out of the box. Optionally create one to set `APP_PASSWORD`.

### View Logs
```bash
docker logs -f network-optimizer
```

### Stop
```bash
docker compose -f docker-compose.macos.yml down
```

## Production Deployment

**Production Server:** `root@nas:/opt/network-optimizer`
**UniFi Gateway:** `root@unifi` (for SQM scripts, crontab, tc status)

### ⚠️ IMPORTANT: Verify NAS Branch Before Deploying

**Always check/switch the NAS repo to the correct branch before deploying.** The NAS repo can get stuck on old branches.

```bash
# Check current branch on NAS
ssh root@nas "cd /opt/network-optimizer && git branch --show-current"

# Switch to main if needed
ssh root@nas "cd /opt/network-optimizer && git checkout main && git pull"
```

### Deploy Process

1. Commit and push changes:
   ```bash
   git add -A && git commit -m "message" && git push
   ```

2. SSH to NAS, verify branch, and pull:
   ```bash
   ssh root@nas "cd /opt/network-optimizer && git checkout main && git pull"
   ```

3. Rebuild and restart the appropriate container(s):

   **Main app** (C# code changes):
   ```bash
   ssh root@nas "cd /opt/network-optimizer/docker && docker compose build network-optimizer && docker compose up -d network-optimizer"
   ```

   **SpeedTest** (src/OpenSpeedTest/* changes - JS, CSS, HTML):
   ```bash
   ssh root@nas "cd /opt/network-optimizer/docker && docker compose build network-optimizer-speedtest && docker compose up -d network-optimizer-speedtest"
   ```

   **Both** (when changes span both):
   ```bash
   ssh root@nas "cd /opt/network-optimizer/docker && docker compose build && docker compose up -d"
   ```

### Quick Deploy (all-in-one)

**Main app only:**
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git checkout main && git pull && cd docker && docker compose build network-optimizer && docker compose up -d network-optimizer"
```

**SpeedTest only:**
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git checkout main && git pull && cd docker && docker compose build network-optimizer-speedtest && docker compose up -d network-optimizer-speedtest"
```

**Both containers:**
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git checkout main && git pull && cd docker && docker compose build && docker compose up -d"
```

### Full Rebuild (use when Dockerfile or dependencies change)
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git pull && cd docker && docker compose build --no-cache && docker compose up -d"
```

### First-Time Setup
Create `.env` from template:
```bash
ssh root@nas "cd /opt/network-optimizer/docker && cp .env.example .env"
# Then edit .env to set secure passwords
```

### Services

| Service | Port | URL |
|---------|------|-----|
| Web UI | 8042 | https://optimizer.seaturtle.minituna.us (via Caddy) |

### Check Status
```bash
ssh root@nas "docker ps --filter 'name=network-optimizer'"
```

### View Logs
```bash
ssh root@nas "docker logs -f network-optimizer"
```

## Mac Production Box - Native Deployment (VPN Required)

**Server:** `noel@192.168.50.10`
**Access:** http://192.168.50.10:8042 (requires VPN to remote site)
**App Location:** `~/network-optimizer/`
**Data Location:** `~/Library/Application Support/NetworkOptimizer/`

Native deployment used instead of Docker due to Docker Desktop's ~1.8 Gbps network throughput limitation.

### Prerequisites (one-time)
```bash
ssh noel@192.168.50.10 "brew install sshpass iperf3 nginx dotnet"
```

### Quick Deploy
```bash
# From Windows dev machine - builds from source on Mac (faster, no tarball transfer)
./scripts/local-dev/deploy-mac-from-src.sh [branch]
```

This script pushes the branch to origin, SSHs to Mac to pull and build from source, then restarts the service. If no branch is specified, uses the current branch. Faster than transferring a ~100MB tarball.

Alternative: `deploy-mac.sh` builds locally and transfers a tarball (slower but doesn't require .NET SDK on Mac).

### View Logs
```bash
ssh noel@192.168.50.10 "tail -f ~/network-optimizer/logs/stdout.log"
```

### Check Status
```bash
ssh noel@192.168.50.10 "launchctl list | grep networkoptimizer && curl -s http://localhost:8042/api/health"
```

### Service Management
```bash
# Stop
ssh noel@192.168.50.10 "launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist"

# Start
ssh noel@192.168.50.10 "launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist"

# Restart
ssh noel@192.168.50.10 "launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist && launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist"
```

## Testing Deployed Changes

**IMPORTANT: Security audits are manual and require authentication.**

- Audits do NOT run automatically on a schedule
- The audit API endpoints require authentication
- You cannot trigger an audit remotely - the user must click "Run Audit" in the web UI
- After deploying, ask the user to run an audit manually, then check the logs

To verify deployed code is working:
1. Deploy the branch
2. Ask the user to run an audit in the web UI
3. Check logs with `ssh root@nas "docker logs network-optimizer 2>&1 | grep -i 'your search term'"`

## Project Structure

- `src/NetworkOptimizer.Web` - Blazor web UI (main app)
- `src/NetworkOptimizer.Audit` - Security audit engine
- `src/NetworkOptimizer.UniFi` - UniFi API client
- `src/NetworkOptimizer.Storage` - SQLite database models
- `src/NetworkOptimizer.Monitoring` - SNMP/SSH polling
- `docker/` - Docker deployment files

## Key Files

- `docker/docker-compose.yml` - Production NAS (app only, host networking, behind Caddy)
- `docker/docker-compose.macos.yml` - macOS Docker testing (port mapping)
- `docker/docker-compose.local.yml` - Local dev with bridge networking
- `docker/Dockerfile` - Container build
- `scripts/local-dev/deploy-mac-from-src.sh` - Mac deployment (builds from source on Mac, optional branch arg)
- `scripts/local-dev/deploy-mac.sh` - Mac deployment (builds locally, transfers tarball)

## Speed Test Directional Concepts

**IMPORTANT: When working on any code involving these directional concepts, ALWAYS verify logic with the user before implementing. LLMs commonly confuse these mappings.**

This application runs iperf3 speed tests where the server is on the local network (NAS/Mac) and clients connect to test their throughput.

### Direction Terminology

| Term | Meaning | Data Flow |
|------|---------|-----------|
| **From Device** | Device sends TO server | Client → Server |
| **To Device** | Server sends TO device | Server → Client |

### iperf3 Server Perspective

The iperf3 results use "download" and "upload" from the SERVER's perspective:

| iperf3 Term | Server Action | UI Label | Direction |
|-------------|---------------|----------|-----------|
| Download | Server receives | From Device | Client uploads to server |
| Upload | Server sends | To Device | Client downloads from server |

### Wi-Fi Rates (AP's Perspective)

Wi-Fi TX/RX rates are from the Access Point's perspective:

| Wi-Fi Term | AP Action | Direction | Limits |
|------------|-----------|-----------|--------|
| **RX rate** | AP receives from client | From Device | Client's upload speed |
| **TX rate** | AP transmits to client | To Device | Client's download speed |

### WAN Speeds (Gateway's Perspective)

WAN download/upload are configured on the gateway and represent ISP speeds:

| WAN Term | Gateway Action | Direction | Limits |
|----------|----------------|-----------|--------|
| **Download** | Gateway receives from WAN | From Device | External client sending to server |
| **Upload** | Gateway sends to WAN | To Device | External client receiving from server |

### NetworkHop Property Mapping

The `IngressSpeedMbps` and `EgressSpeedMbps` properties have DIFFERENT meanings depending on hop type:

**For Wireless Client Hops:**
- `IngressSpeedMbps` = TX rate = **To Device**
- `EgressSpeedMbps` = RX rate = **From Device**

**For WAN/VPN Hops (Tailscale, Teleport, VPN, WAN):**
- `IngressSpeedMbps` = WAN download = **From Device**
- `EgressSpeedMbps` = WAN upload = **To Device**

**For Wired Hops:**
- Both are symmetric (same value)

### Quick Reference Table

| Context | From Device (↓) | To Device (↑) |
|---------|-----------------|---------------|
| UI Display | DownloadMbps | UploadMbps |
| Wi-Fi (AP view) | RX rate | TX rate |
| WAN (gateway view) | Download | Upload |
| Wireless hop | EgressSpeedMbps | IngressSpeedMbps |
| WAN/VPN hop | IngressSpeedMbps | EgressSpeedMbps |

## Sample API Responses

Reference samples from live UniFi API are stored at:
`C:\Users\tjvc4\OneDrive\StartupProjects\OzarkConnect\Research\Network Optimizer\`

- `sample-device-response.txt` - Device/AP data including radio tables
- `sample-fingerprint-response.txt` - Client fingerprint data
- `sample-firewall-policies-response.txt` - Firewall rules
- `sample-settings-response.txt` - Controller settings

Use these to verify JSON field names and data formats when implementing new features.

## Database Migrations (EF Core)

When modifying database models, you must create a proper EF Core migration. Migrations run automatically on app startup via `db.Database.Migrate()` in Program.cs.

### Required Files for Each Migration

Every migration requires THREE files in `src/NetworkOptimizer.Storage/Migrations/`:

1. **Migration file**: `YYYYMMDDHHMMSS_MigrationName.cs` - Contains `Up()` and `Down()` methods
2. **Designer file**: `YYYYMMDDHHMMSS_MigrationName.Designer.cs` - Contains model snapshot at migration time
3. **Update snapshot**: `NetworkOptimizerDbContextModelSnapshot.cs` - Must include the new property/table

### Creating a Migration Manually

1. Create the migration file with `Up()` and `Down()` methods:
```csharp
// 20251217200000_AddNewColumn.cs
public partial class AddNewColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NewColumn",
            table: "TableName",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "NewColumn", table: "TableName");
    }
}
```

2. Create the designer file (copy structure from previous migration, update class name and migration ID)

3. Update `NetworkOptimizerDbContextModelSnapshot.cs` to include the new property in the entity definition

### Common Mistake

If you forget the `.Designer.cs` file or don't update the snapshot, you'll get errors like:
```
SQLite Error 1: 'no such column: d.NewColumn'
```

## Static File Downloads

Files in `src/NetworkOptimizer.Web/wwwroot/downloads/` are served at `/downloads/`

Example: `/downloads/iperf3_3.18-1_mips-3.4.ipk`

## REST API Conventions

### URL Structure

Follow strict RESTful conventions:

- **No verbs in URLs** - Use nouns only (collections and entities)
- **Collections**: `/api/speedtest/results`, `/api/devices`
- **Entities**: `/api/speedtest/results/{id}`, `/api/devices/{mac}`
- **Entity fields**: `/api/speedtest/results/{id}/download` - get single field
- **Filtering**: Use query params, not path segments
  - ✅ `/api/speedtest/results?ip=192.168.1.100`
  - ❌ `/api/speedtest/results/ip/192.168.1.100`

### HTTP Methods

- `GET` - Retrieve resource(s)
- `POST` - Create new resource
- `PUT` - Replace entire resource
- `PATCH` - Partial update
- `DELETE` - Remove resource

### Authentication

- Authenticated endpoints: `/api/{resource}`
- Public (anonymous) endpoints: `/api/public/{resource}`

Example:
```
POST /api/public/speedtest/results  # Anonymous - for external clients (OpenSpeedTest)
GET  /api/speedtest/results         # Authenticated - view results
```

### Bad Examples (Don't Do This)

```
❌ /api/speedtest/submit      # Verb
❌ /api/getResults            # Verb + camelCase
❌ /api/speedtest/doTest      # Verb
❌ /api/create-device         # Verb
```

## CSS Conventions

### Responsive Breakpoints

Standard breakpoints for responsive layouts:

- **768px** - Default mobile/tablet breakpoint (most common)
- **1024px** - Tablet landscape / small desktop
- **900px** - Use when 768px is too aggressive

```css
@@media (max-width: 768px) {
    /* Mobile styles */
}
```

Use 768px as the default unless a specific layout requires a different breakpoint.

### Tooltips

**Always use styled tooltips, never plain HTML `title` attributes.**

Use the `data-tooltip` attribute for styled Tippy.js tooltips:

```html
<!-- Simple tooltip -->
<div class="rule-status" data-tooltip="Active">
    <span class="status-dot"></span>
</div>

<!-- Tooltip with icon (for help/info) -->
<span class="tooltip-icon tooltip-icon-sm" data-tooltip="Explanation text">?</span>
```

The app uses Tippy.js with a custom theme that matches the dark UI. Tooltips are automatically initialized for elements with `data-tooltip` attributes.

## Code Organization

### Shared Utility Methods

**IMPORTANT: Before implementing a utility method, check if it already exists in the codebase.**

Common utility methods should be placed in `NetworkOptimizer.Core.Helpers/` to avoid code duplication across projects. Before writing a new helper:

1. **Search first** - Use grep/glob to check if similar functionality exists:
   ```bash
   # Example: Looking for IP/subnet utilities
   grep -r "IsIpInSubnet\|ParseCidr\|subnet" src/ --include="*.cs"
   ```

2. **Check existing helpers** - Review `src/NetworkOptimizer.Core/Helpers/`:
   - `NetworkUtilities.cs` - IP address and subnet operations (IsIpInSubnet, IsIpInAnySubnet)
   - Other helpers as they're added

3. **Consolidate duplicates** - If you find similar code in multiple places, consolidate it into a shared helper with tests

### Where to Put Shared Code

| Type | Location | Example |
|------|----------|---------|
| Network/IP utilities | `NetworkOptimizer.Core/Helpers/NetworkUtilities.cs` | IsIpInSubnet, ParseCidr |
| String utilities | `NetworkOptimizer.Core/Helpers/StringUtilities.cs` | Formatting, parsing |
| JSON utilities | `NetworkOptimizer.Core/Helpers/JsonUtilities.cs` | Safe property access |
| Project-specific | Within the project | UniFi-specific parsing |

### Best Practices

- Shared helpers should be **stateless static methods** when possible
- Add **comprehensive tests** when creating shared helpers
- Use **descriptive method names** that indicate what the method does
- Document **edge cases** in XML comments (null handling, invalid input, etc.)
- Consider **performance** for methods called frequently (avoid allocations in hot paths)

## Testing Guidelines

### No Personal Information in Tests

**IMPORTANT:** Never include personal or network-specific information in test files:

- ❌ Real names (e.g., "TJ", "Kira", usernames)
- ❌ Real network names (e.g., "FN VPN", specific VLAN names)
- ❌ Real IP addresses from the user's network
- ❌ Real MAC addresses
- ❌ Real device names or hostnames

Use generic placeholders instead:

- ✅ Generic names: "User1", "Admin", "TestUser"
- ✅ Generic networks: "VPN", "IoT", "Management"
- ✅ RFC 5737 test IPs: 192.0.2.x, 198.51.100.x, 203.0.113.x
- ✅ Generic MACs: aa:bb:cc:dd:ee:ff, 00:11:22:33:44:55
- ✅ Generic hostnames: "device1", "switch1", "ap-test"

### Testing Update Checker

To test the update notification banner, rebuild with an older version:

```bash
# On NAS - rebuild with a specific version
ssh root@nas "cd /opt/network-optimizer/docker && docker compose build --build-arg VERSION=0.8.3 network-optimizer && docker compose up -d network-optimizer"
```

The app will report v0.8.3 and the update checker will detect the newer release on GitHub.

### Test Parallelization (Future Goal)

**Current State:** Tests run in ~2+ minutes with low CPU usage. xUnit parallelizes test classes (collections), but tests within a class run sequentially. Large test classes (100+ tests) become bottlenecks.

**Blockers:**
- Some tests may share state or use shared test fixtures
- Database tests may conflict if running in parallel
- Need to audit test isolation before enabling full parallelization

**Best Practices for New Tests:**
- Keep test classes small (~20-30 tests max)
- Avoid shared mutable state between tests
- Use fresh instances/data per test
- If tests need shared setup, ensure it's read-only
- Consider splitting large test files into logical groupings

**Future Work:**
1. Audit existing large test classes for shared state
2. Split large classes into smaller focused classes
3. Ensure DB tests use isolated contexts
4. Add xunit.runner.json with explicit parallelization settings

## SQLite Database Reference

**Database file:** `network_optimizer.db` (note: underscore, not hyphen)

**Location:**
- NAS Docker: `/app/data/network_optimizer.db`
- Mac native: `~/Library/Application Support/NetworkOptimizer/network_optimizer.db`

### Common Queries

```bash
# List all tables
sqlite3 network_optimizer.db "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"

# Reset admin password (forces re-setup on next access)
sqlite3 network_optimizer.db "UPDATE AdminSettings SET Password = NULL;"

# View recent speed test results
sqlite3 network_optimizer.db "SELECT TestTime, DeviceHost, DownloadBitsPerSecond/1e6 as DownMbps, UploadBitsPerSecond/1e6 as UpMbps FROM Iperf3Results ORDER BY TestTime DESC LIMIT 10;"

# Check UniFi connection settings
sqlite3 network_optimizer.db "SELECT ControllerUrl, Username FROM UniFiConnectionSettings;"

# View dismissed audit issues
sqlite3 network_optimizer.db "SELECT * FROM DismissedIssues;"
```

### NAS Docker Execution

```bash
# Run query on NAS
ssh root@nas "docker exec network-optimizer sqlite3 /app/data/network_optimizer.db \"YOUR_QUERY_HERE\""

# Example: Reset admin password on NAS
ssh root@nas "docker exec network-optimizer sqlite3 /app/data/network_optimizer.db \"UPDATE AdminSettings SET Password = NULL;\""
```

### Tables Reference

| Table | Purpose |
|-------|---------|
| AdminSettings | App password (single row) |
| UniFiConnectionSettings | Controller URL, credentials |
| Iperf3Results | Speed test results (all types) |
| DeviceSshConfigurations | Per-device SSH overrides |
| SqmWanConfigurations | SQM settings per WAN |
| AuditResults | Security audit findings |
| DismissedIssues | User-dismissed audit issues |
