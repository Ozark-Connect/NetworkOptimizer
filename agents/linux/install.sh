#!/bin/bash
#
# Network Optimizer Agent - Installation Script for Linux
#
# This script installs the Network Optimizer Agent on a Linux system.
# It copies scripts, sets permissions, installs systemd service, and starts it.
#
# Usage:
#   sudo ./install.sh
#
# Author: Network Optimizer Agent
# Version: 1.0
#

set -e

###############################################################################
# CONFIGURATION
###############################################################################

# Installation paths
INSTALL_DIR="/opt/network-optimizer-agent"
SERVICE_FILE="/etc/systemd/system/network-optimizer-agent.service"
LOG_DIR="/var/log"

# Source directory (where this script is located)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Log file
INSTALL_LOG="${LOG_DIR}/network-optimizer-agent-install.log"

###############################################################################
# FUNCTIONS
###############################################################################

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" | tee -a "$INSTALL_LOG"
}

error() {
    log "ERROR: $*"
    exit 1
}

check_root() {
    if [ "$EUID" -ne 0 ]; then
        error "This script must be run as root (use sudo)"
    fi
}

check_platform() {
    if [ ! -f /etc/os-release ]; then
        error "Cannot detect Linux distribution"
    fi

    log "Platform: $(cat /etc/os-release | grep PRETTY_NAME | cut -d'"' -f2)"
}

create_directories() {
    log "Creating directories..."

    # Create installation directory
    if [ ! -d "$INSTALL_DIR" ]; then
        mkdir -p "$INSTALL_DIR"
        log "Created $INSTALL_DIR"
    fi

    log "Directories created successfully"
}

install_scripts() {
    log "Installing agent script..."

    # Copy agent script
    if [ -f "$SCRIPT_DIR/network-optimizer-agent.sh" ]; then
        cp "$SCRIPT_DIR/network-optimizer-agent.sh" "$INSTALL_DIR/"
        chmod +x "$INSTALL_DIR/network-optimizer-agent.sh"
        log "Installed: $INSTALL_DIR/network-optimizer-agent.sh"
    else
        error "Agent script not found: $SCRIPT_DIR/network-optimizer-agent.sh"
    fi

    log "Scripts installed successfully"
}

install_systemd_service() {
    log "Installing systemd service..."

    # Copy service file
    if [ -f "$SCRIPT_DIR/network-optimizer-agent.service" ]; then
        cp "$SCRIPT_DIR/network-optimizer-agent.service" "$SERVICE_FILE"
        log "Installed: $SERVICE_FILE"
    else
        error "Service file not found: $SCRIPT_DIR/network-optimizer-agent.service"
    fi

    # Reload systemd
    systemctl daemon-reload
    log "Systemd daemon reloaded"

    log "Systemd service installed successfully"
}

install_dependencies() {
    log "Installing dependencies..."

    # Detect package manager
    local pkg_manager=""
    if command -v apt-get &> /dev/null; then
        pkg_manager="apt"
    elif command -v yum &> /dev/null; then
        pkg_manager="yum"
    elif command -v dnf &> /dev/null; then
        pkg_manager="dnf"
    elif command -v pacman &> /dev/null; then
        pkg_manager="pacman"
    else
        log "WARNING: Could not detect package manager. Please install dependencies manually:"
        log "  - bc (calculator)"
        log "  - curl (HTTP client)"
        return
    fi

    log "Detected package manager: $pkg_manager"

    local deps_needed=0

    # Check for bc
    if ! command -v bc &> /dev/null; then
        log "Installing bc..."
        case $pkg_manager in
            apt)
                apt-get update -qq
                apt-get install -y bc
                ;;
            yum|dnf)
                $pkg_manager install -y bc
                ;;
            pacman)
                pacman -S --noconfirm bc
                ;;
        esac
        deps_needed=1
    fi

    # Check for curl
    if ! command -v curl &> /dev/null; then
        log "Installing curl..."
        case $pkg_manager in
            apt)
                apt-get install -y curl
                ;;
            yum|dnf)
                $pkg_manager install -y curl
                ;;
            pacman)
                pacman -S --noconfirm curl
                ;;
        esac
        deps_needed=1
    fi

    if [ $deps_needed -eq 0 ]; then
        log "All dependencies already installed"
    else
        log "Dependencies installed successfully"
    fi
}

configure_agent() {
    log ""
    log "========================================="
    log "IMPORTANT: Configuration Required"
    log "========================================="
    log ""
    log "Please edit the systemd service file to configure the agent:"
    log "  sudo nano $SERVICE_FILE"
    log ""
    log "Key settings to configure:"
    log "  Environment=\"INFLUXDB_URL=http://your-influxdb-host:8086\""
    log "  Environment=\"INFLUXDB_TOKEN=your-token-here\""
    log "  Environment=\"INFLUXDB_ORG=your-org\""
    log "  Environment=\"INFLUXDB_BUCKET=network-metrics\""
    log "  Environment=\"COLLECTION_INTERVAL=60\""
    log "  Environment=\"COLLECT_DOCKER_STATS=false\""
    log "  Environment=\"NETWORK_INTERFACES=eth0\""
    log ""
    log "After editing, reload systemd:"
    log "  sudo systemctl daemon-reload"
    log ""
}

enable_service() {
    log "Enabling systemd service..."

    # Enable the service to start on boot
    systemctl enable network-optimizer-agent.service
    log "Service enabled to start on boot"

    log "Service enabled successfully"
}

start_service() {
    log "Starting service..."

    # Start the service
    systemctl start network-optimizer-agent.service
    log "Service started"

    # Wait a moment for the service to initialize
    sleep 2

    # Check service status
    if systemctl is-active --quiet network-optimizer-agent.service; then
        log "Service is running"
    else
        log "WARNING: Service may not be running. Check status with:"
        log "  sudo systemctl status network-optimizer-agent.service"
    fi

    log "Service start complete"
}

verify_installation() {
    log "Verifying installation..."

    local errors=0

    # Check if script is installed
    if [ ! -f "$INSTALL_DIR/network-optimizer-agent.sh" ]; then
        log "ERROR: Agent script not found"
        errors=$((errors + 1))
    fi

    if [ ! -x "$INSTALL_DIR/network-optimizer-agent.sh" ]; then
        log "ERROR: Agent script not executable"
        errors=$((errors + 1))
    fi

    # Check if service file is installed
    if [ ! -f "$SERVICE_FILE" ]; then
        log "ERROR: Service file not found"
        errors=$((errors + 1))
    fi

    # Check if service is enabled
    if ! systemctl is-enabled --quiet network-optimizer-agent.service 2>/dev/null; then
        log "WARNING: Service is not enabled"
        errors=$((errors + 1))
    fi

    # Check if dependencies are installed
    for cmd in bc curl; do
        if ! command -v "$cmd" &> /dev/null; then
            log "ERROR: Dependency not installed: $cmd"
            errors=$((errors + 1))
        fi
    done

    if [ $errors -eq 0 ]; then
        log "Verification passed: All components installed successfully"
        return 0
    else
        log "Verification found $errors issues (see above)"
        return 1
    fi
}

show_next_steps() {
    log ""
    log "========================================="
    log "Next Steps"
    log "========================================="
    log ""
    log "1. Configure the agent by editing the service file:"
    log "   sudo nano $SERVICE_FILE"
    log ""
    log "2. After configuration, reload systemd:"
    log "   sudo systemctl daemon-reload"
    log ""
    log "3. Restart the service:"
    log "   sudo systemctl restart network-optimizer-agent.service"
    log ""
    log "4. Check service status:"
    log "   sudo systemctl status network-optimizer-agent.service"
    log ""
    log "5. View logs:"
    log "   sudo journalctl -u network-optimizer-agent.service -f"
    log "   sudo tail -f /var/log/network-optimizer-agent.log"
    log ""
    log "6. To stop the service:"
    log "   sudo systemctl stop network-optimizer-agent.service"
    log ""
    log "7. To disable the service:"
    log "   sudo systemctl disable network-optimizer-agent.service"
    log ""
}

###############################################################################
# MAIN EXECUTION
###############################################################################

main() {
    log "========================================="
    log "Network Optimizer Agent Installation"
    log "========================================="
    log "Installation directory: $INSTALL_DIR"
    log "Service file: $SERVICE_FILE"
    log ""

    check_root
    check_platform
    create_directories
    install_dependencies
    install_scripts
    install_systemd_service
    configure_agent
    enable_service
    start_service
    verify_installation

    log ""
    log "========================================="
    log "Installation Complete!"
    log "========================================="

    show_next_steps

    log "========================================="
}

# Run main function
main
