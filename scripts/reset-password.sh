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
DB_PATH=""                  # resolved by the native modes
DOCKER_DB_PATH="/app/data/network_optimizer.db"

# Runs one statement against the application database, whichever way this mode reaches it.
#
# The app is usually running and writing while we do this - the Docker path never stops the
# container at all - so an unqualified write loses a coin toss against the app's own
# transaction and dies with "database is locked". busy_timeout makes sqlite wait for its turn
# instead of failing instantly, which is the difference between a reset that works and one the
# operator has to keep re-running.
#
# Set through .timeout rather than "PRAGMA busy_timeout = ...", which returns the new value as
# a result row and would prepend 15000 to the output of every query that reads one back.
run_sql() {
    if [[ "$MODE" == "docker" ]]; then
        docker exec "$CONTAINER" sqlite3 -cmd ".timeout 15000" "$DOCKER_DB_PATH" "$1"
    else
        sqlite3 -cmd ".timeout 15000" "$DB_PATH" "$1"
    fi
}

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
    if ! run_sql "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"; then
        msg_error "Could not clear the password - the database is busy."
        msg_error "Nothing was changed. Wait a moment and run this script again."
        exit 1
    fi
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
        | sed -E 's/.*Password:[[:space:]]+//' | tr -d '[:space:]')

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
    DB_PATH="$db_path"

    # Detect install directory from plist WorkingDirectory or running process
    local install_dir=""
    if [[ -f "$plist" ]]; then
        install_dir=$(/usr/libexec/PlistBuddy -c "Print :WorkingDirectory" "$plist" 2>/dev/null || true)
    fi
    if [[ -z "$install_dir" ]]; then
        install_dir=$(ps aux | grep 'NetworkOptimizer.Web' | grep -v grep | awk '{for(i=11;i<=NF;i++) printf "%s ",$i}' | sed 's|/NetworkOptimizer.Web.*||' | tr -d '[:space:]')
    fi
    if [[ -z "$install_dir" ]]; then
        install_dir="$HOME/network-optimizer"
    fi

    local log_file="$install_dir/logs/stdout.log"
    msg_info "Install directory: $install_dir"

    # Verify database
    if [[ ! -f "$db_path" ]]; then
        msg_error "Database not found at: $db_path"
        echo "Use --data-dir to specify the correct data directory."
        exit 1
    fi
    msg_ok "Database found: $db_path"

    check_sqlite3
    confirm

    # Record log size before restart so we only read new output
    local log_size_before=0
    if [[ -f "$log_file" ]]; then
        log_size_before=$(wc -c < "$log_file")
    fi

    # Stop service
    if [[ -f "$plist" ]]; then
        msg_info "Stopping service..."
        launchctl unload "$plist" 2>/dev/null || true
        sleep 2
        msg_ok "Service stopped"
    else
        msg_warn "LaunchAgent plist not found at $plist"
        msg_warn "You may need to stop the service manually."
    fi

    # Clear password
    msg_info "Clearing admin password..."
    if ! run_sql "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"; then
        msg_error "Could not clear the password - the database is busy."
        msg_error "Nothing was changed. Wait a moment and run this script again."
        exit 1
    fi
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

    # Poll for new password in log (up to 15s)
    echo ""
    local password=""
    if [[ -f "$log_file" ]]; then
        local deadline=$((SECONDS + 15))
        while [[ $SECONDS -lt $deadline ]]; do
            local new_bytes=$(( $(wc -c < "$log_file") - log_size_before ))
            if [[ $new_bytes -gt 0 ]]; then
                password=$(tail -c "$new_bytes" "$log_file" \
                    | grep "Password:" | tail -1 \
                    | sed -E 's/.*Password:[[:space:]]+//' | tr -d '[:space:]')
                if [[ -n "$password" ]]; then break; fi
            fi
            sleep 1
        done
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

    # Detect install directory from systemd or running process
    local install_dir=""
    if [[ -n "$service_name" ]]; then
        install_dir=$(systemctl show "$service_name" -p WorkingDirectory --value 2>/dev/null || true)
    fi
    if [[ -z "$install_dir" ]]; then
        install_dir=$(readlink -f /proc/$(pgrep -f "NetworkOptimizer.Web" | head -1)/cwd 2>/dev/null || true)
    fi
    if [[ -z "$install_dir" ]]; then
        install_dir="/opt/network-optimizer"
    fi
    msg_info "Install directory: $install_dir"

    check_sqlite3
    confirm

    # Record log size before restart so we only read new output
    local log_file="$install_dir/logs/stdout.log"
    local log_size_before=0
    if [[ -f "$log_file" ]]; then
        log_size_before=$(wc -c < "$log_file")
    fi

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
    if ! run_sql "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"; then
        msg_error "Could not clear the password - the database is busy."
        msg_error "Nothing was changed. Wait a moment and run this script again."
        exit 1
    fi
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

    # Poll for new password in journalctl or log file (up to 15s)
    echo ""
    local password=""
    local deadline=$((SECONDS + 15))
    while [[ $SECONDS -lt $deadline ]] && [[ -z "$password" ]]; do
        if [[ -n "$service_name" ]]; then
            password=$(journalctl -u "$service_name" --since "2 minutes ago" --no-pager 2>/dev/null \
                | grep "Password:" | tail -1 \
                | sed -E 's/.*Password:[[:space:]]+//' | tr -d '[:space:]')
        fi

        # Fallback: check new log output only
        if [[ -z "$password" ]] && [[ -f "$log_file" ]]; then
            local new_bytes=$(( $(wc -c < "$log_file") - log_size_before ))
            if [[ $new_bytes -gt 0 ]]; then
                password=$(tail -c "$new_bytes" "$log_file" \
                    | grep "Password:" | tail -1 \
                    | sed -E 's/.*Password:[[:space:]]+//' | tr -d '[:space:]')
            fi
        fi

        if [[ -z "$password" ]]; then sleep 1; fi
    done

    show_result "$password"
}

# =============================================================================
# Apply the regenerated password to the Identity admin account
# =============================================================================
# Sign-in reads AspNetUsers.PasswordHash, and the app only copies the legacy password
# across when the admin account does not exist yet. So on an install that has already
# migrated, clearing AdminSettings alone regenerates and prints a password that is then
# refused at the login page. Copy the freshly generated hash across ourselves.
#
# The hash is copied, never re-derived, so this needs no crypto and no plaintext. It is
# written in the old dotted PBKDF2 format, which the app still accepts and quietly
# upgrades on first sign-in.
#
# The account is updated, never deleted: deleting it cannot be undone and would take the
# admin's site memberships and roles with it.
sync_identity_admin() {
    # Older installs have no Identity tables - there the legacy row is the whole story and
    # the reset above is already complete.
    local has_identity
    has_identity=$(run_sql "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='AspNetUsers';" 2>/dev/null || echo "0")
    if [[ "$has_identity" != "1" ]]; then
        return 0
    fi

    # The app writes the new hash while it starts; wait for it rather than race it.
    local legacy_hash="" deadline=$((SECONDS + 20))
    while [[ $SECONDS -lt $deadline ]]; do
        legacy_hash=$(run_sql "SELECT ifnull(Password,'') FROM AdminSettings LIMIT 1;" 2>/dev/null || echo "")
        if [[ -n "$legacy_hash" ]]; then
            break
        fi
        sleep 1
    done

    if [[ -z "$legacy_hash" ]]; then
        msg_warn "Could not read the regenerated password; the admin account was left unchanged."
        return 1
    fi

    msg_info "Applying the new password to the admin account..."
    # Checked explicitly rather than left to set -e: this function is called as part of an
    # || list, which switches errexit off for everything inside it, so a failed write would
    # otherwise fall straight through to the success message and print a password that does
    # not work - the one outcome this whole script exists to avoid.
    if ! run_sql "UPDATE AspNetUsers
                     SET PasswordHash = '${legacy_hash}',
                         PasswordIsTemporary = 1,
                         IsEnabled = 1,
                         LockoutEnd = NULL,
                         AccessFailedCount = 0,
                         SecurityStamp = lower(hex(randomblob(16)))
                   WHERE NormalizedUserName = 'ADMIN';" >/dev/null; then
        msg_error "Could not update the admin account."
        msg_error "The password below will NOT work. Re-run this script to try again."
        return 1
    fi
    msg_ok "Admin account updated"
}

# =============================================================================
# Warn about settings that refuse the new password even after a successful reset
# =============================================================================
# The reset only restores the password. Two other settings can still turn the login
# away, and from the login page both look exactly like a wrong password - so say so
# here rather than leave someone retyping a password that was never the problem.
# Neither is changed automatically: one would weaken the install's SSO policy, the
# other would throw away an MFA enrollment.
warn_about_blockers() {
    local sso_only mfa_on

    sso_only=$(run_sql "SELECT Value FROM SystemSettings WHERE Key = 'auth.local_login_disabled';" 2>/dev/null || echo "")
    if [[ "$sso_only" == "true" ]]; then
        echo ""
        msg_warn "Local logins are disabled on this install (single sign-on only)."
        msg_warn "The password below will be refused until an administrator re-enables"
        msg_warn "local login, or you restart with NETOPT_RECOVERY=1 to bypass it once."
    fi

    mfa_on=$(run_sql "SELECT TwoFactorEnabled FROM AspNetUsers WHERE NormalizedUserName = 'ADMIN';" 2>/dev/null || echo "")
    if [[ "$mfa_on" == "1" ]]; then
        echo ""
        msg_warn "The admin account has two-factor authentication enabled."
        msg_warn "You will still be asked for your authenticator code after signing in."
        msg_warn "Use a saved recovery code if you no longer have the authenticator."
    fi
}

# =============================================================================
# Display Result
# =============================================================================
show_result() {
    local password="$1"

    sync_identity_admin || true
    warn_about_blockers

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
