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

# Refuse to run as root - everything installs to $HOME, root is never needed
if [ "$(id -u)" = "0" ]; then
    echo "Error: Do not run this script with sudo or as root."
    echo ""
    echo "This installer puts everything in your home directory and does not need"
    echo "root access. Running with sudo causes file ownership problems that break"
    echo "future upgrades."
    echo ""
    echo "If you previously installed with sudo, just run the script normally:"
    echo "  ./scripts/install-macos-native.sh"
    echo ""
    echo "The script will detect and clean up any root-owned files automatically."
    exit 1
fi

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

# Check for root-owned remnants from a previous sudo installation.
# If someone ran this script with sudo, all files and processes end up owned by root.
# A normal user can't overwrite those files or kill those processes, so the next
# install fails. This function detects the problem and fixes it with one sudo prompt.
check_root_remnants() {
    local needs_cleanup=false
    local root_pids=""
    local root_files=false

    # Check for root-owned processes on our ports (8042=app, 3005=nginx, 5201=iperf3)
    for port in 8042 3005 5201; do
        local pids
        pids=$(lsof -i ":$port" -sTCP:LISTEN -t 2>/dev/null) || true
        for pid in $pids; do
            local owner
            owner=$(ps -o user= -p "$pid" 2>/dev/null | tr -d ' ') || true
            if [ "$owner" = "root" ]; then
                needs_cleanup=true
                root_pids="$root_pids $pid"
            fi
        done
    done

    # Check for root-owned install directories and files
    for dir in "$INSTALL_DIR" "$DATA_DIR"; do
        if [ -d "$dir" ] && [ "$(stat -f '%Su' "$dir" 2>/dev/null)" = "root" ]; then
            needs_cleanup=true
            root_files=true
        fi
    done
    if [ -f "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" ] && \
       [ "$(stat -f '%Su' "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" 2>/dev/null)" = "root" ]; then
        needs_cleanup=true
        root_files=true
    fi

    # Check for root-owned .NET directories (blocks dotnet publish)
    for dotdir in "$HOME/.nuget" "$HOME/.dotnet"; do
        if [ -d "$dotdir" ] && [ "$(stat -f '%Su' "$dotdir" 2>/dev/null)" = "root" ]; then
            needs_cleanup=true
            root_files=true
        fi
    done

    if [ "$needs_cleanup" = false ]; then
        return 0
    fi

    echo "Detected remnants from a previous sudo installation:"
    echo ""
    if [ -n "$root_pids" ]; then
        echo "  - Root-owned processes holding ports 8042/3005/5201 (PIDs:$root_pids)"
    fi
    if [ "$root_files" = true ]; then
        echo "  - Root-owned files in install directories"
    fi
    echo ""
    echo "This needs sudo to fix. You'll be prompted for your password once."
    echo ""
    read -rp "Press Enter to clean up, or Ctrl+C to cancel... "

    local current_user
    current_user=$(whoami)

    # Kill root-owned processes on our ports
    if [ -n "$root_pids" ]; then
        echo "Stopping root-owned processes..."
        for pid in $root_pids; do
            sudo kill "$pid" 2>/dev/null || true
        done
        sleep 2
    fi

    # Unload any root-loaded launchd services
    sudo launchctl unload "$LAUNCH_AGENT_DIR/$LAUNCH_AGENT_FILE" 2>/dev/null || true
    sudo launchctl unload "$LAUNCH_AGENT_DIR/$OLD_LAUNCH_AGENT_FILE" 2>/dev/null || true

    # Fix ownership on install directories
    if [ -d "$INSTALL_DIR" ]; then
        echo "Fixing ownership: $INSTALL_DIR"
        sudo chown -R "$current_user:staff" "$INSTALL_DIR"
    fi
    if [ -d "$DATA_DIR" ]; then
        echo "Fixing ownership: $DATA_DIR"
        sudo chown -R "$current_user:staff" "$DATA_DIR"
    fi
    for plist in "$LAUNCH_AGENT_FILE" "$OLD_LAUNCH_AGENT_FILE"; do
        if [ -f "$LAUNCH_AGENT_DIR/$plist" ]; then
            sudo chown "$current_user:staff" "$LAUNCH_AGENT_DIR/$plist"
        fi
    done

    # Fix .NET directories
    for dotdir in "$HOME/.nuget" "$HOME/.dotnet"; do
        if [ -d "$dotdir" ] && [ "$(stat -f '%Su' "$dotdir" 2>/dev/null)" = "root" ]; then
            echo "Fixing ownership: ${dotdir/#$HOME/~}"
            sudo chown -R "$current_user:staff" "$dotdir"
        fi
    done

    echo ""
    echo "Cleanup complete. Continuing with installation..."
    echo ""
}

check_root_remnants

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

# Ensure NuGet cache is writable (stale cache from brew or failed restores can block builds)
if [ -d "$HOME/.nuget/packages" ] && ! touch "$HOME/.nuget/packages/.write-test" 2>/dev/null; then
    echo "NuGet package cache has permission issues, clearing..."
    chmod -R u+w "$HOME/.nuget/packages" 2>/dev/null || true
    rm -rf "$HOME/.nuget/packages"
    if [ -d "$HOME/.nuget/packages" ]; then
        echo "Error: Could not clear NuGet cache. Try running: sudo rm -rf ~/.nuget/packages"
        exit 1
    fi
fi
rm -f "$HOME/.nuget/packages/.write-test" 2>/dev/null

dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj \
    -c Release \
    -r "$RUNTIME" \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -o "$INSTALL_DIR"

# Step 3b: Build Go binaries
echo ""
echo "[3b/9] Building Go binaries..."
if command -v go &> /dev/null; then
    mkdir -p "$INSTALL_DIR/tools"

    # Detect Go architecture for local binary
    GO_ARCH="amd64"
    if [ "$ARCH" = "arm64" ]; then
        GO_ARCH="arm64"
    fi

    CFSPEEDTEST_SRC="$REPO_ROOT/src/cfspeedtest"
    if [ -d "$CFSPEEDTEST_SRC" ]; then
        cd "$CFSPEEDTEST_SRC"
        CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath \
            -ldflags "-s -w" \
            -o "$INSTALL_DIR/tools/cfspeedtest-linux-arm64" .
        echo "Built cfspeedtest for linux/arm64"
    else
        echo "Warning: cfspeedtest source not found at $CFSPEEDTEST_SRC"
    fi

    UWNSPEEDTEST_SRC="$REPO_ROOT/src/uwnspeedtest"
    if [ -d "$UWNSPEEDTEST_SRC" ]; then
        cd "$UWNSPEEDTEST_SRC"
        # Build local binary for server-side WAN speed tests
        CGO_ENABLED=0 GOOS=darwin GOARCH=$GO_ARCH go build -trimpath \
            -ldflags "-s -w" \
            -o "$INSTALL_DIR/tools/uwnspeedtest-darwin-$GO_ARCH" .
        echo "Built uwnspeedtest for darwin/$GO_ARCH (local)"
        # Build gateway binary for deployment via SSH to UniFi gateways
        CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath \
            -ldflags "-s -w" \
            -o "$INSTALL_DIR/tools/uwnspeedtest-linux-arm64" .
        echo "Built uwnspeedtest for linux/arm64 (gateway)"
    else
        echo "Warning: uwnspeedtest source not found at $UWNSPEEDTEST_SRC"
    fi
else
    echo "Warning: Go not installed - speed test binaries not available"
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
        log_not_found off;
        server_tokens off;
        error_log /dev/null;

        # Performance tuning
        open_file_cache max=200000 inactive=20s;
        open_file_cache_valid 30s;
        open_file_cache_min_uses 2;
        open_file_cache_errors off;

        # Upload endpoint - reads entire POST body before responding.
        # Without this, the error_page 405 hack responds before the body is
        # fully received, causing ERR_CONNECTION_RESET behind reverse proxies.
        location = /upload {
            add_header 'Access-Control-Allow-Origin' "*" always;
            add_header 'Access-Control-Allow-Headers' 'Accept,Authorization,Cache-Control,Content-Type,DNT,If-Modified-Since,Keep-Alive,Origin,User-Agent,X-Mx-ReqToken,X-Requested-With' always;
            add_header 'Access-Control-Allow-Methods' 'GET, POST, OPTIONS' always;
            add_header Cache-Control 'no-store, no-cache, max-age=0, no-transform';

            client_body_buffer_size 35m;
            client_max_body_size 50m;

            proxy_pass http://127.0.0.1:3005/upload-sink;
            proxy_set_header Host $host;
        }

        location = /upload-sink {
            add_header 'Access-Control-Allow-Origin' "*" always;
            return 200;
        }

        location / {
            add_header 'Access-Control-Allow-Origin' "*" always;
            add_header 'Access-Control-Allow-Headers' 'Accept,Authorization,Cache-Control,Content-Type,DNT,If-Modified-Since,Keep-Alive,Origin,User-Agent,X-Mx-ReqToken,X-Requested-With' always;
            add_header 'Access-Control-Allow-Methods' 'GET, POST, OPTIONS' always;
            add_header Cache-Control 'no-store, no-cache, max-age=0, no-transform';
            if_modified_since off;
            expires off;
            etag off;

            if ($request_method = OPTIONS) {
                add_header 'Access-Control-Allow-Credentials' "true";
                add_header 'Access-Control-Allow-Headers' 'Accept,Authorization,Cache-Control,Content-Type,DNT,If-Modified-Since,Keep-Alive,Origin,User-Agent,X-Mx-ReqToken,X-Requested-With' always;
                add_header 'Access-Control-Allow-Origin' "$http_origin" always;
                add_header 'Access-Control-Allow-Methods' "GET, POST, OPTIONS" always;
                return 200;
            }
        }

        location ~* ^.+\.(?:css|cur|js|jpe?g|gif|htc|ico|png|html|xml|otf|ttf|eot|woff|woff2|svg)$ {
            access_log off;
            expires -1;
            add_header Cache-Control "no-cache, no-store, must-revalidate";
            add_header Vary Accept-Encoding;
            tcp_nodelay off;
            open_file_cache max=3000 inactive=120s;
            open_file_cache_valid 45s;
            open_file_cache_min_uses 2;
            open_file_cache_errors off;
            gzip on;
            gzip_disable "msie6";
            gzip_vary on;
            gzip_proxied any;
            gzip_comp_level 6;
            gzip_buffers 16 8k;
            gzip_http_version 1.1;
            gzip_types text/plain text/css application/json application/x-javascript text/xml application/xml application/xml+rss text/javascript application/javascript image/svg+xml;
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
