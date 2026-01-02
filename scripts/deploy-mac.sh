#!/bin/bash
# Deploy Network Optimizer to Mac production box (native, non-Docker)
# Usage: ./scripts/deploy-mac.sh

set -e

MAC_HOST="noel@192.168.50.10"
MAC_APP_DIR="~/network-optimizer"
PUBLISH_DIR="./publish/osx-arm64"
TARBALL="network-optimizer-osx-arm64.tar.gz"

echo "=== Network Optimizer Mac Deployment ==="
echo ""

# Step 1: Publish
echo "[1/4] Publishing for macOS ARM64..."
dotnet publish src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj -c Release -r osx-arm64 --self-contained -o "$PUBLISH_DIR"

# Step 2: Create tarball
echo "[2/4] Creating tarball..."
cd publish
tar -czf "$TARBALL" osx-arm64
cd ..

# Step 3: Copy and extract
echo "[3/4] Copying to Mac and extracting..."
scp "publish/$TARBALL" "$MAC_HOST:/tmp/"
ssh "$MAC_HOST" "cd $MAC_APP_DIR && tar -xzf /tmp/$TARBALL --strip-components=1"

# Step 4: Fix permissions, sign, and restart
echo "[4/4] Fixing permissions, signing binaries, and restarting service..."
ssh "$MAC_HOST" "cd $MAC_APP_DIR && chmod +x NetworkOptimizer.Web && find . -name '*.dylib' -exec codesign --force --sign - {} \; && codesign --force --sign - NetworkOptimizer.Web"
ssh "$MAC_HOST" "launchctl unload ~/Library/LaunchAgents/com.networkoptimizer.app.plist 2>/dev/null || true; launchctl load ~/Library/LaunchAgents/com.networkoptimizer.app.plist"

# Verify
echo ""
echo "Waiting for startup..."
sleep 3
ssh "$MAC_HOST" "curl -s http://localhost:8042/api/health"
echo ""
echo ""
echo "=== Deployment complete ==="
echo "Access at: http://192.168.50.10:8042"
