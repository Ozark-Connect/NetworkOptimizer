# Development Scripts

Bash scripts for common development tasks. Works with Git Bash on Windows or native bash on macOS/Linux.

## Usage

```bash
# Make scripts executable (first time only)
chmod +x scripts/*.sh

# Run a script
./scripts/test.sh
```

## Available Scripts

| Script | Description |
|--------|-------------|
| `build.sh [Debug\|Release]` | Build the project |
| `test.sh` | Run all tests |
| `coverage.sh` | Run tests with code coverage report |
| `watch.sh` | Run web app with hot reload |
| `clean.sh` | Clean build artifacts and coverage |
| `publish.sh [output-dir]` | Publish for production |
| `docker-build.sh` | Build Docker image |
| `docker-run.sh` | Run container locally (port 8042) |
| `docker-stop.sh` | Stop container |
| `build-installer.ps1` | Build Windows MSI installer (PowerShell) |
| `install-macos-native.sh` | Install natively on macOS |

## Windows Installer

Build the MSI installer for Windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
```

Output: `publish/NetworkOptimizer-{version}-win-x64.msi`

The installer creates a single-file executable (~67 MB) with all dependencies embedded.

## macOS Native Installation

Install Network Optimizer natively on macOS (no Docker required):

```bash
# Clone the repository
git clone https://github.com/Ozark-Connect/NetworkOptimizer.git
cd NetworkOptimizer

# Run the installer
./scripts/install-macos-native.sh
```

The script:
1. Installs prerequisites via Homebrew (iperf3, nginx, .NET SDK)
2. Builds a single-file executable (~58 MB)
3. Sets up OpenSpeedTest with nginx
4. Creates a launchd service for auto-start

Install location: `~/network-optimizer/`

Service management:
```bash
# Stop
launchctl unload ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# Start
launchctl load ~/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist

# Logs
tail -f ~/network-optimizer/logs/stdout.log
```

## Code Coverage

The `coverage.sh` script generates a coverage report:

```bash
./scripts/coverage.sh
```

Coverage results are saved to `./coverage/`. To get an HTML report, install ReportGenerator:

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
```

Then run `coverage.sh` again - it will automatically generate the HTML report.
