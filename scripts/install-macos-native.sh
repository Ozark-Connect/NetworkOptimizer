#!/bin/bash
# Install Network Optimizer natively on macOS
# Usage: ./scripts/install-macos-native.sh
#
# This script:
# 1. Installs prerequisites via Homebrew
# 2. Builds the application (or uses pre-built if available)
# 3. Signs binaries for macOS
# 4. Sets up OpenSpeedTest with nginx for browser-based speed testing
# 5. Creates launchd service for auto-start

set -e

# Configuration
INSTALL_DIR="$HOME/network-optimizer"
DATA_DIR="$HOME/Library/Application Support/NetworkOptimizer"
LAUNCH_AGENT_DIR="$HOME/Library/LaunchAgents"
LAUNCH_AGENT_FILE="net.ozarkconnect.networkoptimizer.plist"
OLD_LAUNCH_AGENT_FILE="com.networkoptimizer.app.plist"  # For migration from older installs

# Detect architecture
ARCH=$(uname -m)
if [ "$ARCH" = "arm64" ]; then
    RUNTIME="osx-arm64"
    BREW_PREFIX="/opt/homebrew"
else
    RUNTIME="osx-x64"
    BREW_PREFIX="/usr/local"
fi

echo "=== Network Optimizer macOS Native Installation ==="
echo ""
echo "Architecture: $ARCH ($RUNTIME)"
echo "Install directory: $INSTALL_DIR"
echo ""

# Check if running from repo root
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if [ ! -f "$REPO_ROOT/src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj" ]; then
    echo "Error: This script must be run from the NetworkOptimizer repository."
    echo "Clone the repo first: git clone https://github.com/Ozark-Connect/NetworkOptimizer.git"
    exit 1
fi

# Backup existing installation if present
if [ -d "$DATA_DIR" ] || [ -d "$INSTALL_DIR" ]; then
    BACKUP_DIR="$HOME/network-optimizer-backup-$(date +%Y%m%d-%H%M%S)"
    echo "Backing up existing installation to $BACKUP_DIR..."
    mkdir -p "$BACKUP_DIR"

    # Backup data directory contents (DB, keys, etc.)
    if [ -f "$DATA_DIR/network_optimizer.db" ]; then
        cp "$DATA_DIR/network_optimizer.db" "$BACKUP_DIR/"
        echo "  ✓ Database backed up"
    fi
    if [ -f "$DATA_DIR/.credential_key" ]; then
        cp "$DATA_DIR/.credential_key" "$BACKUP_DIR/"
        echo "  ✓ Credential key backed up"
    fi
    if [ -d "$DATA_DIR/keys" ]; then
        cp -r "$DATA_DIR/keys" "$BACKUP_DIR/"
        echo "  ✓ Encryption keys backed up"
    fi

    # Backup start.sh (has custom env config)
    if [ -f "$INSTALL_DIR/start.sh" ]; then
        cp "$INSTALL_DIR/start.sh" "$BACKUP_DIR/"
        echo "  ✓ Startup script backed up"
    fi

    echo "Backup complete: $BACKUP_DIR"
    echo ""
fi

# Step 1: Install prerequisites
echo "[1/9] Installing prerequisites..."
if ! command -v brew &> /dev/null; then
    echo "Installing Homebrew..."
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    eval "$($BREW_PREFIX/bin/brew shellenv)"
fi

# Ensure brew is in PATH
eval "$($BREW_PREFIX/bin/brew shellenv)"

echo "Installing required packages..."
brew install sshpass iperf3 nginx go 2>/dev/null || true

# Check for .NET SDK
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET SDK..."
    brew install dotnet
fi

# Verify .NET version
DOTNET_VERSION=$(dotnet --version 2>/dev/null | cut -d. -f1)
if [ "$DOTNET_VERSION" -lt 8 ]; then
    echo "Warning: .NET $DOTNET_VERSION detected. Network Optimizer requires .NET 8 or later."
    echo "Updating .NET SDK..."
    brew upgrade dotnet || brew install dotnet
fi

# Step 2: Clean up old installation files (preserving user config and logs)
echo ""
echo "[2/9] Cleaning up old installation files..."
if [ -d "$INSTALL_DIR" ]; then
    cd "$INSTALL_DIR"
    # Remove old non-single-file artifacts (DLLs, pdb, runtimes folder, etc.)
    rm -rf *.dll *.pdb *.json runtimes/ BuildHost-*/ LatoFont/ 2>/dev/null || true
    # Note: start.sh, logs/, SpeedTest/, wwwroot/, Templates/ are preserved or rebuilt
fi

# Step 3: Build the application
echo ""
echo "[3/9] Building Network Optimizer for $RUNTIME..."
cd "$REPO_ROOT"
dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj \
    -c Release \
    -r "$RUNTIME" \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -o "$INSTALL_DIR"

# Step 3b: Build cfspeedtest binary for gateway deployment
echo ""
echo "[3b/9] Building cfspeedtest for gateway (linux/arm64)..."
if command -v go &> /dev/null; then
    CFSPEEDTEST_SRC="$REPO_ROOT/src/cfspeedtest"
    if [ -d "$CFSPEEDTEST_SRC" ]; then
        mkdir -p "$INSTALL_DIR/tools"
        cd "$CFSPEEDTEST_SRC"
        CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath \
            -ldflags "-s -w" \
            -o "$INSTALL_DIR/tools/cfspeedtest-linux-arm64" .
        echo "Built cfspeedtest for linux/arm64"
    else
        echo "Warning: cfspeedtest source not found at $CFSPEEDTEST_SRC"
    fi
else
    echo "Warning: Go not installed - gateway speed test binary not available"
    echo "  Install with: brew install go"
fi

# Step 4: Sign binary (single-file executable has native libs embedded)
echo ""
echo "[4/9] Signing binary..."
cd "$INSTALL_DIR"
codesign --force --sign - NetworkOptimizer.Web
echo "Verifying signature..."
codesign -v NetworkOptimizer.Web

# Step 5: Create startup script
echo ""
echo "[5/9] Creating startup script..."

# Get local IP address for display purposes (app auto-detects its own IP)
LOCAL_IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo "your-mac-ip")

cat > "$INSTALL_DIR/start.sh" << EOF
#!/bin/bash
cd "\$(dirname "\$0")"

# Add Homebrew to PATH
export PATH="$BREW_PREFIX/bin:/usr/local/bin:\$PATH"

# Environment configuration
export TZ="${TZ:-America/Chicago}"
export ASPNETCORE_URLS="http://0.0.0.0:8042"

# Enable iperf3 server for CLI-based client speed testing (port 5201)
export Iperf3Server__Enabled=true

# OpenSpeedTest configuration (browser-based speed tests on port 3005)
export OPENSPEEDTEST_PORT=3005

# Optional: Set admin password (otherwise auto-generated on first run)
# export APP_PASSWORD="your-secure-password"

# Start the application
./NetworkOptimizer.Web
EOF

chmod +x "$INSTALL_DIR/start.sh"

# Restore backed up start.sh if it exists (preserves user's env config on upgrade)
if [ -n "${BACKUP_DIR:-}" ] && [ -f "$BACKUP_DIR/start.sh" ]; then
    cp "$BACKUP_DIR/start.sh" "$INSTALL_DIR/start.sh"
    echo "  ✓ Restored custom startup configuration from backup"
fi

# Step 6: Create log directory
echo ""
echo "[6/9] Creating directories..."
mkdir -p "$INSTALL_DIR/logs"
mkdir -p "$DATA_DIR"
mkdir -p "$LAUNCH_AGENT_DIR"

# Step 7: Set up OpenSpeedTest with nginx
echo ""
echo "[7/9] Setting up OpenSpeedTest..."

SPEEDTEST_DIR="$INSTALL_DIR/SpeedTest"
mkdir -p "$SPEEDTEST_DIR"/{conf,logs,temp,html/assets/{css,js,fonts,images/icons}}

# Copy nginx configuration
if [ -f "$REPO_ROOT/src/OpenSpeedTest/index.html" ]; then
    # Copy mime.types from Homebrew's nginx
    if [ -f "$BREW_PREFIX/etc/nginx/mime.types" ]; then
        cp "$BREW_PREFIX/etc/nginx/mime.types" "$SPEEDTEST_DIR/conf/"
    else
        echo "Warning: mime.types not found at $BREW_PREFIX/etc/nginx/mime.types"
    fi

    # Create nginx.conf optimized for SpeedTest (based on Docker config)
    cat > "$SPEEDTEST_DIR/conf/nginx.conf" << 'NGINXCONF'
# Run in foreground so the app can track the process
daemon off;
worker_processes 1;
error_log logs/error.log;
pid logs/nginx.pid;

events {
    worker_connections 1024;
}

http {
    include mime.types;
    default_type application/octet-stream;
    sendfile on;
    tcp_nodelay on;
    tcp_nopush on;
    keepalive_timeout 65;
    access_log off;
    gzip off;

    server {
        listen 3005;
        server_name _;
        root html;
        index index.html;
        client_max_body_size 50m;
        error_page 405 =200 $uri;

        location / {
            add_header 'Access-Control-Allow-Origin' "*" always;
            add_header 'Access-Control-Allow-Headers' 'Accept,Authorization,Cache-Control,Content-Type,DNT,If-Modified-Since,Keep-Alive,Origin,User-Agent,X-Mx-ReqToken,X-Requested-With' always;
            add_header 'Access-Control-Allow-Methods' 'GET, POST, OPTIONS' always;
            add_header Cache-Control 'no-store, no-cache, max-age=0, no-transform';

            if ($request_method = OPTIONS) {
                add_header 'Access-Control-Allow-Credentials' "true";
                add_header 'Access-Control-Allow-Origin' "$http_origin" always;
                return 200;
            }
        }

        location ~* ^.+\.(?:css|js|png|svg|woff2?|ttf|eot)$ {
            expires -1;
            add_header Cache-Control "no-cache, no-store, must-revalidate";
        }
    }
}
NGINXCONF

    # Copy OpenSpeedTest HTML files
    cp "$REPO_ROOT/src/OpenSpeedTest/index.html" "$SPEEDTEST_DIR/html/"
    cp "$REPO_ROOT/src/OpenSpeedTest/hosted.html" "$SPEEDTEST_DIR/html/"
    cp "$REPO_ROOT/src/OpenSpeedTest/downloading" "$SPEEDTEST_DIR/html/"
    cp "$REPO_ROOT/src/OpenSpeedTest/upload" "$SPEEDTEST_DIR/html/"

    # Copy assets
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/css/"* "$SPEEDTEST_DIR/html/assets/css/" 2>/dev/null || true
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/js/"* "$SPEEDTEST_DIR/html/assets/js/" 2>/dev/null || true
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/fonts/"* "$SPEEDTEST_DIR/html/assets/fonts/" 2>/dev/null || true
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/images/"*.svg "$SPEEDTEST_DIR/html/assets/images/" 2>/dev/null || true
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/images/icons/"* "$SPEEDTEST_DIR/html/assets/images/icons/" 2>/dev/null || true

    # Copy config.js template and inject runtime values (same approach as Docker entrypoint)
    cp "$REPO_ROOT/src/OpenSpeedTest/assets/js/config.js" "$SPEEDTEST_DIR/html/assets/js/config.js"

    # Replace placeholders - use __DYNAMIC__ so URL is constructed client-side from browser location
    sed -i '' "s|__SAVE_DATA__|true|g" "$SPEEDTEST_DIR/html/assets/js/config.js"
    sed -i '' "s|__SAVE_DATA_URL__|__DYNAMIC__|g" "$SPEEDTEST_DIR/html/assets/js/config.js"
    sed -i '' "s|__API_PATH__|/api/public/speedtest/results|g" "$SPEEDTEST_DIR/html/assets/js/config.js"

    SPEEDTEST_AVAILABLE=true
    echo "OpenSpeedTest files installed"
else
    echo "Warning: OpenSpeedTest source files not found. Skipping SpeedTest setup."
    echo "Browser-based speed testing will not be available."
    SPEEDTEST_AVAILABLE=false
fi

# Step 8: Create launchd plist for main app
echo ""
echo "[8/9] Creating launchd service..."

cat > "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>net.ozarkconnect.networkoptimizer</string>
    <key>ProgramArguments</key>
    <array>
        <string>$INSTALL_DIR/start.sh</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$INSTALL_DIR</string>
    <key>KeepAlive</key>
    <true/>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$INSTALL_DIR/logs/stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$INSTALL_DIR/logs/stderr.log</string>
</dict>
</plist>
EOF

# Step 9: Start services
# Note: The app manages nginx and iperf3 internally - no separate launchd services needed
echo ""
echo "[9/9] Starting services..."

# Migrate from old plist name if present
if [ -f "$LAUNCH_AGENT_DIR/$OLD_LAUNCH_AGENT_FILE" ]; then
    echo "Migrating from old service name..."
    launchctl unload "$LAUNCH_AGENT_DIR/$OLD_LAUNCH_AGENT_FILE" 2>/dev/null || true
    rm -f "$LAUNCH_AGENT_DIR/$OLD_LAUNCH_AGENT_FILE"
    # Also remove the old speedtest plist if it exists
    launchctl unload "$LAUNCH_AGENT_DIR/com.networkoptimizer.speedtest.plist" 2>/dev/null || true
    rm -f "$LAUNCH_AGENT_DIR/com.networkoptimizer.speedtest.plist"
fi

# Gracefully stop any orphaned processes from previous installs
pkill -f "NetworkOptimizer.Web" 2>/dev/null || true
pkill iperf3 2>/dev/null || true
pkill nginx 2>/dev/null || true
sleep 2  # Give processes time to shut down gracefully

# Unload if already loaded (ignore errors)
launchctl unload "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" 2>/dev/null || true
launchctl load "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE"

# Wait for startup and verify
echo ""
echo "Waiting for service to start..."

# Check launchd service status
if launchctl list | grep -q "net.ozarkconnect.networkoptimizer"; then
    echo "✓ Network Optimizer service is running"
else
    echo "✗ Network Optimizer service failed to start"
    echo "  Check logs: tail -f $INSTALL_DIR/logs/stderr.log"
fi

# Wait for health endpoint with retries
echo "Waiting for application to be ready..."
HEALTH_OK=false
for i in {1..12}; do
    if curl -sL http://localhost:8042/api/health | grep -qi "healthy"; then
        HEALTH_OK=true
        break
    fi
    sleep 5
done

echo ""
echo "=== Installation Complete ==="
echo ""
if [ "$HEALTH_OK" = true ]; then
    echo "✓ Health check passed"
else
    echo "✗ Health check failed after 60 seconds"
    echo "  The app may still be starting. Check logs: tail -f $INSTALL_DIR/logs/stdout.log"
fi

echo ""
echo "=== Access Information ==="
echo ""
echo "Web UI:      http://localhost:8042"
echo "             http://$LOCAL_IP:8042 (from other devices)"
if [ "$SPEEDTEST_AVAILABLE" = true ]; then
    echo ""
    echo "SpeedTest:   http://localhost:3005"
    echo "             http://$LOCAL_IP:3005 (from other devices)"
fi
echo ""
echo "On first run, check logs for the auto-generated admin password:"
echo "  grep -A5 'AUTO-GENERATED' $INSTALL_DIR/logs/stdout.log"
echo ""
echo "Service management:"
echo "  Stop:    launchctl unload ~/Library/LaunchAgents/$LAUNCH_AGENT_FILE"
echo "  Start:   launchctl load ~/Library/LaunchAgents/$LAUNCH_AGENT_FILE"
echo "  Logs:    tail -f $INSTALL_DIR/logs/stdout.log"
echo ""
