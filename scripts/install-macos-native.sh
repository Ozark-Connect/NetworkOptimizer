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
LAUNCH_AGENT_FILE="com.networkoptimizer.app.plist"
SPEEDTEST_LAUNCH_AGENT_FILE="com.networkoptimizer.speedtest.plist"

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

# Step 1: Install prerequisites
echo "[1/8] Installing prerequisites..."
if ! command -v brew &> /dev/null; then
    echo "Installing Homebrew..."
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    eval "$($BREW_PREFIX/bin/brew shellenv)"
fi

# Ensure brew is in PATH
eval "$($BREW_PREFIX/bin/brew shellenv)"

echo "Installing required packages..."
brew install sshpass iperf3 nginx 2>/dev/null || true

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

# Step 2: Build the application
echo ""
echo "[2/8] Building Network Optimizer for $RUNTIME..."
cd "$REPO_ROOT"
dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj \
    -c Release \
    -r "$RUNTIME" \
    --self-contained \
    -o "$INSTALL_DIR"

# Step 3: Sign binaries
echo ""
echo "[3/8] Signing binaries..."
cd "$INSTALL_DIR"
find . -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --sign - NetworkOptimizer.Web
echo "Verifying signature..."
codesign -v NetworkOptimizer.Web

# Step 4: Create startup script
echo ""
echo "[4/8] Creating startup script..."

# Get local IP address for HOST_IP
LOCAL_IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo "")
if [ -z "$LOCAL_IP" ]; then
    echo "Warning: Could not detect local IP. You'll need to set HOST_IP manually in start.sh"
    LOCAL_IP="REPLACE_WITH_YOUR_IP"
fi

cat > "$INSTALL_DIR/start.sh" << EOF
#!/bin/bash
cd "\$(dirname "\$0")"

# Add Homebrew to PATH
export PATH="$BREW_PREFIX/bin:/usr/local/bin:\$PATH"

# Environment configuration
export TZ="${TZ:-America/Chicago}"
export ASPNETCORE_URLS="http://0.0.0.0:8042"

# Host IP - required for speed test result tracking and path analysis
export HOST_IP="$LOCAL_IP"

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

# Step 5: Create log directory
echo ""
echo "[5/8] Creating directories..."
mkdir -p "$INSTALL_DIR/logs"
mkdir -p "$DATA_DIR"
mkdir -p "$LAUNCH_AGENT_DIR"

# Step 6: Set up OpenSpeedTest with nginx
echo ""
echo "[6/8] Setting up OpenSpeedTest..."

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

    # Generate config.js with API URL
    cat > "$SPEEDTEST_DIR/html/assets/js/config.js" << CONFIGJS
/**
 * OpenSpeedTest Configuration
 * Generated by install-macos-native.sh
 */
var saveData = true;
var saveDataURL = "http://$LOCAL_IP:8042/api/public/speedtest/results";
var apiPath = "/api/public/speedtest/results";

// If __DYNAMIC__, construct URL from browser location
if (saveDataURL === "__DYNAMIC__") {
    saveDataURL = window.location.protocol + "//" + window.location.hostname + ":8042" + apiPath;
}

// Fix for missing variable bug in OpenSpeedTest
var OpenSpeedTestdb = "";
CONFIGJS

    SPEEDTEST_AVAILABLE=true
    echo "OpenSpeedTest files installed"
else
    echo "Warning: OpenSpeedTest source files not found. Skipping SpeedTest setup."
    echo "Browser-based speed testing will not be available."
    SPEEDTEST_AVAILABLE=false
fi

# Step 7: Create launchd plist for main app
echo ""
echo "[7/8] Creating launchd service..."

cat > "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.networkoptimizer.app</string>
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

# Create launchd plist for SpeedTest (nginx)
if [ "$SPEEDTEST_AVAILABLE" = true ]; then
    cat > "$LAUNCH_AGENT_DIR/$SPEEDTEST_LAUNCH_AGENT_FILE" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.networkoptimizer.speedtest</string>
    <key>ProgramArguments</key>
    <array>
        <string>$BREW_PREFIX/bin/nginx</string>
        <string>-c</string>
        <string>$SPEEDTEST_DIR/conf/nginx.conf</string>
        <string>-p</string>
        <string>$SPEEDTEST_DIR</string>
        <string>-g</string>
        <string>daemon off;</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$SPEEDTEST_DIR</string>
    <key>KeepAlive</key>
    <true/>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$SPEEDTEST_DIR/logs/stdout.log</string>
    <key>StandardErrorPath</key>
    <string>$SPEEDTEST_DIR/logs/stderr.log</string>
</dict>
</plist>
EOF
fi

# Step 8: Start services
echo ""
echo "[8/8] Starting services..."

# Unload if already loaded (ignore errors)
launchctl unload "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" 2>/dev/null || true
launchctl load "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE"

if [ "$SPEEDTEST_AVAILABLE" = true ]; then
    launchctl unload "$LAUNCH_AGENT_DIR/$SPEEDTEST_LAUNCH_AGENT_FILE" 2>/dev/null || true
    launchctl load "$LAUNCH_AGENT_DIR/$SPEEDTEST_LAUNCH_AGENT_FILE"
fi

# Wait for startup
echo ""
echo "Waiting for services to start..."
sleep 5

# Verify
echo ""
echo "=== Installation Complete ==="
echo ""
echo "Checking service status..."
if launchctl list | grep -q "com.networkoptimizer.app"; then
    echo "✓ Network Optimizer service is running"
else
    echo "✗ Network Optimizer service failed to start"
    echo "  Check logs: tail -f $INSTALL_DIR/logs/stderr.log"
fi

if [ "$SPEEDTEST_AVAILABLE" = true ]; then
    if launchctl list | grep -q "com.networkoptimizer.speedtest"; then
        echo "✓ SpeedTest (nginx) service is running"
    else
        echo "✗ SpeedTest service failed to start"
        echo "  Check logs: tail -f $SPEEDTEST_DIR/logs/stderr.log"
    fi
fi

# Test health endpoint
echo ""
if curl -s http://localhost:8042/api/health | grep -q "ok\|Healthy"; then
    echo "✓ Health check passed"
else
    echo "✗ Health check failed (may still be starting up)"
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
