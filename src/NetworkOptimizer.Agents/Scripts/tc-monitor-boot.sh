#!/bin/sh
# =============================================================================
# TC Monitor - UniFi on_boot.d Script
# =============================================================================
# This script sets up a lightweight HTTP server that exposes SQM/Traffic Control
# statistics via a simple JSON API. It's designed to run on UniFi gateways
# (UDM, UCG, etc.) and be polled by Network Optimizer.
#
# Installation:
#   1. Copy this file to /data/on_boot.d/20-tc-monitor.sh
#   2. Make executable: chmod +x /data/on_boot.d/20-tc-monitor.sh
#   3. (Optional) Create config file: /data/tc-monitor/interfaces.conf
#   4. Run it once: /data/on_boot.d/20-tc-monitor.sh
#   5. It will auto-start on every boot via systemd
#
# Configuration file format (/data/tc-monitor/interfaces.conf):
#   # One interface per line, or space-separated
#   # Format: tc_interface:friendly_name
#   ifbeth4:Yelcot
#   ifbeth0:Starlink
#
# Or via environment variable:
#   export TC_MONITOR_INTERFACES="ifbeth4:Yelcot ifbeth0:Starlink"
#
# The server listens on port 8088 and returns JSON like:
#   {
#     "timestamp": "2024-12-08T12:00:00Z",
#     "interfaces": [
#       { "name": "Yelcot", "interface": "ifbeth4", "rate_mbps": 256.0 },
#       { "name": "Starlink", "interface": "ifbeth0", "rate_mbps": 100.0 }
#     ]
#   }
#
# Or legacy format (backwards compatible):
#   {
#     "timestamp": "2024-12-08T12:00:00Z",
#     "wan1": { "name": "Yelcot", "interface": "ifbeth4", "rate_mbps": 256.0 },
#     "wan2": { "name": "Starlink", "interface": "ifbeth0", "rate_mbps": 100.0 }
#   }
#
# To test: curl http://<gateway-ip>:8088/
# =============================================================================

TC_MONITOR_DIR="/data/tc-monitor"
LOG_FILE="/var/log/tc-monitor.log"
SERVICE_NAME="tc-monitor"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
PORT="${TC_MONITOR_PORT:-8088}"
CONFIG_FILE="${TC_MONITOR_DIR}/interfaces.conf"

# Output format: "interfaces" (new array format) or "legacy" (wan1/wan2 format)
OUTPUT_FORMAT="${TC_MONITOR_FORMAT:-both}"

# Default configuration if no config file or environment variable
DEFAULT_INTERFACES="ifbeth2:WAN1 ifbeth0:WAN2"

echo "$(date): Setting up TC Monitor systemd service..." >> "$LOG_FILE"

# Create the TC monitor directory if it doesn't exist
if [ ! -d "$TC_MONITOR_DIR" ]; then
    echo "$(date): Creating TC monitor directory: $TC_MONITOR_DIR" >> "$LOG_FILE"
    mkdir -p "$TC_MONITOR_DIR"
fi

# Only create default config if it doesn't exist (preserve user config)
if [ ! -f "$CONFIG_FILE" ]; then
    if [ -n "$TC_MONITOR_INTERFACES" ]; then
        echo "$TC_MONITOR_INTERFACES" > "$CONFIG_FILE"
        echo "$(date): Created config from environment variable" >> "$LOG_FILE"
    else
        echo "$DEFAULT_INTERFACES" > "$CONFIG_FILE"
        echo "$(date): Created default config (edit $CONFIG_FILE to customize)" >> "$LOG_FILE"
    fi
fi

# Create the TC monitor handler script
cat > "$TC_MONITOR_DIR/tc-monitor.sh" << 'HANDLER_EOF'
#!/bin/sh
# TC (Traffic Control) monitoring endpoint
# Returns JSON with current SQM/FQ_CoDel rates for all configured interfaces
# Supports both new "interfaces" array format and legacy "wan1/wan2" format

SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"
INTERFACES_FILE="$SCRIPT_DIR/interfaces.conf"
OUTPUT_FORMAT="${TC_MONITOR_FORMAT:-both}"

# Function to extract rate from tc class show output
get_tc_rate() {
    local interface=$1
    # Example output: class htb 1:1 root rate 256Mbit ceil 256Mbit burst 1440b cburst 1440b
    tc class show dev "$interface" 2>/dev/null | grep -o 'rate [0-9.]*[MGK]*bit' | head -n1 | awk '{print $2}'
}

# Function to convert rate to Mbps (returns decimal)
rate_to_mbps() {
    local rate=$1
    if [ -z "$rate" ]; then
        echo "0"
        return
    fi

    if echo "$rate" | grep -q "Mbit"; then
        echo "$rate" | sed 's/Mbit//'
    elif echo "$rate" | grep -q "Gbit"; then
        echo "$rate" | sed 's/Gbit//' | awk '{printf "%.1f", $1 * 1000}'
    elif echo "$rate" | grep -q "Kbit"; then
        echo "$rate" | sed 's/Kbit//' | awk '{printf "%.3f", $1 / 1000}'
    elif echo "$rate" | grep -q "bit"; then
        echo "$rate" | sed 's/bit//' | awk '{printf "%.6f", $1 / 1000000}'
    else
        echo "0"
    fi
}

# Get current timestamp
timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

# Read interface configuration (supports one per line or space-separated)
if [ -f "$INTERFACES_FILE" ]; then
    # Read file, strip comments, normalize to space-separated
    INTERFACES=$(grep -v '^#' "$INTERFACES_FILE" | tr '\n' ' ' | sed 's/  */ /g')
else
    INTERFACES="ifbeth2:WAN1 ifbeth0:WAN2"
fi

# Collect interface data
interface_data=""
wan_count=0
for iface_config in $INTERFACES; do
    interface=$(echo "$iface_config" | cut -d: -f1)
    name=$(echo "$iface_config" | cut -d: -f2)

    # Skip empty entries
    [ -z "$interface" ] && continue

    rate_raw=$(get_tc_rate "$interface")
    rate_mbps=$(rate_to_mbps "$rate_raw")

    # Check if interface exists
    if ip link show "$interface" > /dev/null 2>&1; then
        status="active"
    else
        status="not_found"
        rate_mbps="0"
        rate_raw=""
    fi

    wan_count=$((wan_count + 1))
    interface_data="${interface_data}${wan_count}|${name}|${interface}|${rate_mbps}|${rate_raw}|${status}
"
done

# Build JSON response
echo "{"
echo "  \"timestamp\": \"$timestamp\","

# Output both formats for maximum compatibility
if [ "$OUTPUT_FORMAT" = "legacy" ] || [ "$OUTPUT_FORMAT" = "both" ]; then
    # Legacy wan1/wan2 format
    wan_num=0
    echo "$interface_data" | while IFS='|' read -r idx name interface rate_mbps rate_raw status; do
        [ -z "$idx" ] && continue
        wan_num=$((wan_num + 1))
        if [ $wan_num -gt 1 ]; then
            echo ","
        fi
        printf '  "wan%d": {"name": "%s", "interface": "%s", "rate_mbps": %s, "rate_raw": "%s", "status": "%s"}' \
            "$wan_num" "$name" "$interface" "$rate_mbps" "$rate_raw" "$status"
    done
fi

if [ "$OUTPUT_FORMAT" = "both" ]; then
    echo ","
fi

if [ "$OUTPUT_FORMAT" = "interfaces" ] || [ "$OUTPUT_FORMAT" = "both" ]; then
    # New interfaces array format
    echo "  \"interfaces\": ["
    first=true
    echo "$interface_data" | while IFS='|' read -r idx name interface rate_mbps rate_raw status; do
        [ -z "$idx" ] && continue
        if [ "$first" = true ]; then
            first=false
        else
            echo ","
        fi
        printf '    {"name": "%s", "interface": "%s", "rate_mbps": %s, "rate_raw": "%s", "status": "%s"}' \
            "$name" "$interface" "$rate_mbps" "$rate_raw" "$status"
    done
    echo ""
    echo "  ]"
fi

echo "}"
HANDLER_EOF

chmod +x "$TC_MONITOR_DIR/tc-monitor.sh"

# Create the HTTP server script using netcat
cat > "$TC_MONITOR_DIR/tc-server.sh" << 'SERVER_EOF'
#!/bin/sh
# Ultra-lightweight HTTP server for TC monitoring using netcat
# Minimal resource usage - perfect for UniFi gateways

PORT="${TC_MONITOR_PORT:-8088}"
SCRIPT_DIR="$(dirname "$(readlink -f "$0")")"

echo "Starting TC Monitor HTTP server on port $PORT..."
echo "Endpoint: http://0.0.0.0:$PORT/"

while true; do
    {
        echo "HTTP/1.0 200 OK"
        echo "Content-Type: application/json"
        echo "Access-Control-Allow-Origin: *"
        echo "Cache-Control: no-cache"
        echo ""
        "$SCRIPT_DIR/tc-monitor.sh"
    } | nc -l -p "$PORT" -q 1 > /dev/null 2>&1

    # Small delay to prevent CPU spin if netcat fails
    sleep 0.1
done
SERVER_EOF

chmod +x "$TC_MONITOR_DIR/tc-server.sh"

# Create systemd service file
cat > "$SERVICE_FILE" << SERVICE_EOF
[Unit]
Description=TC Monitor HTTP Server for Network Optimizer
After=network.target
Documentation=https://github.com/ozark-connect/network-optimizer

[Service]
Type=simple
Environment="TC_MONITOR_PORT=$PORT"
ExecStart=$TC_MONITOR_DIR/tc-server.sh
Restart=always
RestartSec=5
StandardOutput=append:/var/log/tc-monitor.log
StandardError=append:/var/log/tc-monitor.log
User=root

# Security hardening
ProtectSystem=strict
ReadWritePaths=/var/log $TC_MONITOR_DIR
PrivateTmp=true

[Install]
WantedBy=multi-user.target
SERVICE_EOF

echo "$(date): Systemd service file installed to $SERVICE_FILE" >> "$LOG_FILE"

# Reload systemd and start service
systemctl daemon-reload
systemctl enable "$SERVICE_NAME" >> "$LOG_FILE" 2>&1
systemctl restart "$SERVICE_NAME" >> "$LOG_FILE" 2>&1

# Verify service started
if systemctl is-active --quiet "$SERVICE_NAME"; then
    echo "$(date): TC Monitor started successfully on port $PORT" >> "$LOG_FILE"
    echo "TC Monitor is running on port $PORT"
    echo "Test with: curl http://localhost:$PORT/"
else
    echo "$(date): ERROR - TC Monitor failed to start" >> "$LOG_FILE"
    systemctl status "$SERVICE_NAME" >> "$LOG_FILE" 2>&1
    exit 1
fi

exit 0
