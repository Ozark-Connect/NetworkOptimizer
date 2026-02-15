#!/usr/bin/env bash

# Network Optimizer - Password Reset Script
# https://github.com/Ozark-Connect/NetworkOptimizer
#
# Resets the admin password by clearing it from the database and restarting
# the service. Works with Docker, macOS native, and Linux native deployments.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/reset-password.sh | bash
#   bash reset-password.sh [--docker|--macos|--linux] [--container NAME] [--data-dir PATH] [--force]

set -euo pipefail

# =============================================================================
# Colors and Formatting (matches proxmox/install.sh)
# =============================================================================
if [[ -t 1 ]]; then
    readonly RD='\033[0;31m'
    readonly GN='\033[0;32m'
    readonly YW='\033[0;33m'
    readonly BL='\033[0;34m'
    readonly CY='\033[0;36m'
    readonly BLD='\033[1m'
    readonly CL='\033[0m'
else
    readonly RD='' GN='' YW='' BL='' CY='' BLD='' CL=''
fi

msg_info()  { echo -e "${BL}[INFO]${CL} $1"; }
msg_ok()    { echo -e "${GN}[OK]${CL} $1"; }
msg_warn()  { echo -e "${YW}[WARN]${CL} $1"; }
msg_error() { echo -e "${RD}[ERROR]${CL} $1"; }

header() {
    echo ""
    echo -e "${BLD}${CY}Network Optimizer - Password Reset${CL}"
    echo -e "${BLD}${CY}===================================${CL}"
    echo ""
}

# =============================================================================
# Defaults
# =============================================================================
MODE=""                     # docker, macos, linux (auto-detected if empty)
CONTAINER="network-optimizer"
DATA_DIR=""
FORCE=false
TIMEOUT=60
HEALTH_URL="http://localhost:8042/api/health"

# =============================================================================
# Parse Arguments
# =============================================================================
while [[ $# -gt 0 ]]; do
    case "$1" in
        --docker)    MODE="docker"; shift ;;
        --macos)     MODE="macos";  shift ;;
        --linux)     MODE="linux";  shift ;;
        --container) CONTAINER="$2"; shift 2 ;;
        --data-dir)  DATA_DIR="$2";  shift 2 ;;
        --force)     FORCE=true;     shift ;;
        --timeout)   TIMEOUT="$2";   shift 2 ;;
        -h|--help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --docker          Force Docker mode"
            echo "  --macos           Force macOS native mode"
            echo "  --linux           Force Linux native mode"
            echo "  --container NAME  Docker container name (default: network-optimizer)"
            echo "  --data-dir PATH   Override database directory path"
            echo "  --force           Skip confirmation prompt"
            echo "  --timeout SECS    Health check timeout (default: 60)"
            echo "  -h, --help        Show this help"
            exit 0
            ;;
        *)
            msg_error "Unknown option: $1"
            echo "Use --help for usage information."
            exit 1
            ;;
    esac
done

# Auto-force when stdin is not a terminal (e.g., curl | bash)
if [[ ! -t 0 ]]; then
    FORCE=true
fi

# =============================================================================
# Auto-detect Mode
# =============================================================================
detect_mode() {
    if [[ -n "$MODE" ]]; then
        return
    fi

    # Check for Docker container
    if command -v docker &>/dev/null; then
        if docker ps -a --format '{{.Names}}' 2>/dev/null | grep -q "^${CONTAINER}$"; then
            MODE="docker"
            msg_info "Detected Docker container: $CONTAINER"
            return
        fi
    fi

    # Check for macOS native install
    if [[ "$(uname)" == "Darwin" ]]; then
        if [[ -d "$HOME/network-optimizer" ]] || \
           [[ -f "$HOME/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist" ]]; then
            MODE="macos"
            msg_info "Detected macOS native installation"
            return
        fi
    fi

    # Check for Linux native install
    if [[ "$(uname)" == "Linux" ]]; then
        if systemctl list-unit-files 2>/dev/null | grep -qi "networkoptimizer\|network-optimizer"; then
            MODE="linux"
            msg_info "Detected Linux native installation (systemd)"
            return
        fi
        if pgrep -f "NetworkOptimizer.Web" &>/dev/null; then
            MODE="linux"
            msg_info "Detected running NetworkOptimizer process"
            return
        fi
        if [[ -d "/opt/network-optimizer" ]]; then
            MODE="linux"
            msg_info "Detected Linux installation at /opt/network-optimizer"
            return
        fi
    fi

    msg_error "Could not auto-detect installation type."
    echo ""
    echo "Please specify one of:"
    echo "  --docker   Docker container"
    echo "  --macos    macOS native install"
    echo "  --linux    Linux native install"
    exit 1
}

# =============================================================================
# Check for sqlite3
# =============================================================================
check_sqlite3() {
    if command -v sqlite3 &>/dev/null; then
        return 0
    fi

    msg_error "sqlite3 is not installed."
    echo ""
    if [[ "$(uname)" == "Darwin" ]]; then
        echo "sqlite3 should be included with macOS. Try:"
        echo "  brew install sqlite3"
    elif command -v apt-get &>/dev/null; then
        echo "Install with:  sudo apt-get install -y sqlite3"
    elif command -v dnf &>/dev/null; then
        echo "Install with:  sudo dnf install -y sqlite"
    elif command -v pacman &>/dev/null; then
        echo "Install with:  sudo pacman -S sqlite"
    else
        echo "Install sqlite3 using your package manager."
    fi
    exit 1
}

# =============================================================================
# Wait for health endpoint
# =============================================================================
wait_for_health() {
    msg_info "Waiting for application to start..."
    local deadline=$((SECONDS + TIMEOUT))

    while [[ $SECONDS -lt $deadline ]]; do
        if curl -sf "$HEALTH_URL" -o /dev/null --max-time 3 2>/dev/null; then
            msg_ok "Application is ready"
            return 0
        fi
        sleep 2
    done

    msg_warn "Health check timed out after ${TIMEOUT}s. The service may still be starting."
    return 1
}

# =============================================================================
# Confirm with user
# =============================================================================
confirm() {
    if [[ "$FORCE" == true ]]; then
        return 0
    fi

    echo "This will:"
    echo "  1. Stop the Network Optimizer service"
    echo "  2. Clear the admin password from the database"
    echo "  3. Restart the service"
    echo "  4. Display the new auto-generated temporary password"
    echo ""
    read -rp "Continue? (y/N) " answer
    if [[ ! "$answer" =~ ^[Yy] ]]; then
        echo "Cancelled."
        exit 0
    fi
    echo ""
}

# =============================================================================
# Docker Mode
# =============================================================================
reset_docker() {
    msg_info "Mode: Docker (container: $CONTAINER)"
    echo ""

    # Check container exists
    if ! docker ps -a --format '{{.Names}}' 2>/dev/null | grep -q "^${CONTAINER}$"; then
        msg_error "Container '$CONTAINER' not found."
        echo "Use --container NAME to specify a different container name."
        exit 1
    fi

    # If container is stopped, start it temporarily for docker exec
    if ! docker ps --format '{{.Names}}' 2>/dev/null | grep -q "^${CONTAINER}$"; then
        msg_warn "Container is stopped. Starting it temporarily..."
        docker start "$CONTAINER" >/dev/null
        sleep 3
    fi

    confirm

    # Clear password via docker exec
    msg_info "Clearing admin password..."
    docker exec "$CONTAINER" sqlite3 /app/data/network_optimizer.db \
        "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"
    msg_ok "Password cleared"

    # Restart container
    msg_info "Restarting container..."
    docker restart "$CONTAINER" >/dev/null
    msg_ok "Container restarted"

    # Wait for health
    wait_for_health || true

    # Extract password from docker logs
    echo ""
    local password
    password=$(docker logs --since 2m "$CONTAINER" 2>&1 \
        | grep "Password:" | tail -1 \
        | sed -E 's/.*Password:\s+//' | tr -d '[:space:]')

    show_result "$password"
}

# =============================================================================
# macOS Native Mode
# =============================================================================
reset_macos() {
    msg_info "Mode: macOS native"
    echo ""

    local plist="$HOME/Library/LaunchAgents/net.ozarkconnect.networkoptimizer.plist"
    local db_dir="${DATA_DIR:-$HOME/Library/Application Support/NetworkOptimizer}"
    local db_path="$db_dir/network_optimizer.db"
    local log_file="$HOME/network-optimizer/logs/stdout.log"

    # Verify database
    if [[ ! -f "$db_path" ]]; then
        msg_error "Database not found at: $db_path"
        echo "Use --data-dir to specify the correct data directory."
        exit 1
    fi
    msg_ok "Database found: $db_path"

    check_sqlite3
    confirm

    # Stop service
    if [[ -f "$plist" ]]; then
        msg_info "Stopping service..."
        launchctl unload "$plist" 2>/dev/null || true
        msg_ok "Service stopped"
    else
        msg_warn "LaunchAgent plist not found at $plist"
        msg_warn "You may need to stop the service manually."
    fi

    # Clear password
    msg_info "Clearing admin password..."
    sqlite3 "$db_path" "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"
    msg_ok "Password cleared"

    # Start service
    if [[ -f "$plist" ]]; then
        msg_info "Starting service..."
        launchctl load "$plist"
        msg_ok "Service started"
    else
        msg_warn "Cannot auto-start - start the service manually."
    fi

    # Wait for health
    wait_for_health || true

    # Extract password from log
    echo ""
    local password=""
    if [[ -f "$log_file" ]]; then
        password=$(tail -100 "$log_file" \
            | grep "Password:" | tail -1 \
            | sed -E 's/.*Password:\s+//' | tr -d '[:space:]')
    fi

    show_result "$password"
}

# =============================================================================
# Linux Native Mode
# =============================================================================
reset_linux() {
    msg_info "Mode: Linux native"
    echo ""

    # Find the systemd service name
    local service_name=""
    for name in networkoptimizer NetworkOptimizer network-optimizer; do
        if systemctl list-unit-files "${name}.service" &>/dev/null 2>&1; then
            if systemctl list-unit-files "${name}.service" 2>/dev/null | grep -q "$name"; then
                service_name="$name"
                break
            fi
        fi
    done

    # Find database
    local db_path=""
    if [[ -n "$DATA_DIR" ]]; then
        db_path="$DATA_DIR/network_optimizer.db"
    else
        for candidate in \
            "/opt/network-optimizer/data/network_optimizer.db" \
            "$HOME/.local/share/NetworkOptimizer/network_optimizer.db" \
            "/var/lib/network-optimizer/network_optimizer.db"; do
            if [[ -f "$candidate" ]]; then
                db_path="$candidate"
                break
            fi
        done
    fi

    if [[ -z "$db_path" ]] || [[ ! -f "$db_path" ]]; then
        msg_error "Database not found."
        echo "Searched:"
        echo "  /opt/network-optimizer/data/network_optimizer.db"
        echo "  ~/.local/share/NetworkOptimizer/network_optimizer.db"
        echo "  /var/lib/network-optimizer/network_optimizer.db"
        echo ""
        echo "Use --data-dir to specify the correct data directory."
        exit 1
    fi
    msg_ok "Database found: $db_path"

    check_sqlite3
    confirm

    # Stop service
    if [[ -n "$service_name" ]]; then
        msg_info "Stopping service ($service_name)..."
        sudo systemctl stop "$service_name"
        msg_ok "Service stopped"
    else
        msg_warn "No systemd service found. Attempting to kill the process..."
        if pkill -f "NetworkOptimizer.Web" 2>/dev/null; then
            msg_ok "Process stopped"
        else
            msg_warn "Could not stop process. It may not be running."
        fi
    fi

    # Clear password
    msg_info "Clearing admin password..."
    sqlite3 "$db_path" "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"
    msg_ok "Password cleared"

    # Start service
    if [[ -n "$service_name" ]]; then
        msg_info "Starting service ($service_name)..."
        sudo systemctl start "$service_name"
        msg_ok "Service started"
    else
        msg_warn "No systemd service found. Start the application manually."
        msg_warn "The new password will appear in the application logs."
        echo ""
        return
    fi

    # Wait for health
    wait_for_health || true

    # Extract password from journalctl or log file
    echo ""
    local password=""
    if [[ -n "$service_name" ]]; then
        password=$(journalctl -u "$service_name" --since "2 minutes ago" --no-pager 2>/dev/null \
            | grep "Password:" | tail -1 \
            | sed -E 's/.*Password:\s+//' | tr -d '[:space:]')
    fi

    # Fallback: check log files
    if [[ -z "$password" ]]; then
        for log_file in \
            "/opt/network-optimizer/logs/stdout.log" \
            "$HOME/.local/share/NetworkOptimizer/logs/stdout.log"; do
            if [[ -f "$log_file" ]]; then
                password=$(tail -100 "$log_file" \
                    | grep "Password:" | tail -1 \
                    | sed -E 's/.*Password:\s+//' | tr -d '[:space:]')
                if [[ -n "$password" ]]; then break; fi
            fi
        done
    fi

    show_result "$password"
}

# =============================================================================
# Display Result
# =============================================================================
show_result() {
    local password="$1"

    if [[ -n "$password" ]]; then
        echo -e "${GN}===================================${CL}"
        echo -e "${GN}  Password reset successful!${CL}"
        echo -e "${GN}===================================${CL}"
        echo ""
        echo -e "  Temporary password: ${CY}${BLD}${password}${CL}"
        echo ""
        echo "  Log in to Network Optimizer with this password,"
        echo "  then go to Settings to set a permanent one."
        echo ""
    else
        msg_warn "Password reset completed, but could not extract the new password from logs."
        echo ""
        echo "Check the logs manually:"
        if [[ "$MODE" == "docker" ]]; then
            echo "  docker logs $CONTAINER 2>&1 | grep -A5 'AUTO-GENERATED'"
        elif [[ "$MODE" == "macos" ]]; then
            echo "  grep 'Password:' ~/network-optimizer/logs/stdout.log | tail -1"
        else
            echo "  journalctl -u networkoptimizer --since '5 minutes ago' | grep 'Password:'"
        fi
        echo ""
        echo "Look for the line containing 'AUTO-GENERATED ADMIN PASSWORD'."
        echo ""
    fi
}

# =============================================================================
# Main
# =============================================================================
header
detect_mode
echo ""

case "$MODE" in
    docker) reset_docker ;;
    macos)  reset_macos  ;;
    linux)  reset_linux  ;;
    *)
        msg_error "Unknown mode: $MODE"
        exit 1
        ;;
esac
