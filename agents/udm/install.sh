#!/bin/bash
#
# Network Optimizer Agent - Installation Script for UDM/UCG
#
# This script installs the Network Optimizer Agent on a UniFi Dream Machine
# or Cloud Gateway. It copies scripts, sets permissions, installs cron jobs,
# and starts services.
#
# Usage:
#   ./install.sh
#
# Author: Network Optimizer Agent
# Version: 1.0
#

set -e

###############################################################################
# CONFIGURATION
###############################################################################

# Installation paths
ON_BOOT_DIR="/data/on_boot.d"
AGENT_DIR="/data/network-optimizer-agent"
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
        error "This script must be run as root"
    fi
}

check_platform() {
    if [ ! -d "/data" ]; then
        error "This does not appear to be a UniFi Dream Machine or Cloud Gateway"
    fi

    log "Platform check passed"
}

create_directories() {
    log "Creating directories..."

    # Create on_boot.d directory if it doesn't exist
    if [ ! -d "$ON_BOOT_DIR" ]; then
        mkdir -p "$ON_BOOT_DIR"
        log "Created $ON_BOOT_DIR"
    fi

    # Create agent directory
    if [ ! -d "$AGENT_DIR" ]; then
        mkdir -p "$AGENT_DIR"
        log "Created $AGENT_DIR"
    fi

    log "Directories created successfully"
}

install_scripts() {
    log "Installing agent scripts..."

    # Copy on-boot script
    if [ -f "$SCRIPT_DIR/50-network-optimizer-agent.sh" ]; then
        cp "$SCRIPT_DIR/50-network-optimizer-agent.sh" "$ON_BOOT_DIR/"
        chmod +x "$ON_BOOT_DIR/50-network-optimizer-agent.sh"
        log "Installed on-boot script: $ON_BOOT_DIR/50-network-optimizer-agent.sh"
    else
        error "On-boot script not found: $SCRIPT_DIR/50-network-optimizer-agent.sh"
    fi

    # Copy agent scripts
    for script in sqm-manager.sh sqm-ping-monitor.sh metrics-collector.sh; do
        if [ -f "$SCRIPT_DIR/$script" ]; then
            cp "$SCRIPT_DIR/$script" "$AGENT_DIR/"
            chmod +x "$AGENT_DIR/$script"
            log "Installed: $AGENT_DIR/$script"
        else
            error "Agent script not found: $SCRIPT_DIR/$script"
        fi
    done

    log "Scripts installed successfully"
}

install_dependencies() {
    log "Installing dependencies..."

    # Update package lists
    apt-get update -qq

    local deps_needed=0

    # Check for speedtest
    if ! which speedtest > /dev/null 2>&1; then
        log "Installing Ookla Speedtest..."
        apt-get remove -y speedtest > /dev/null 2>&1 || true
        curl -s https://packagecloud.io/install/repositories/ookla/speedtest-cli/script.deb.sh | bash
        apt-get install -y speedtest
        deps_needed=1
    fi

    # Check for bc
    if ! which bc > /dev/null 2>&1; then
        log "Installing bc..."
        apt-get install -y bc
        deps_needed=1
    fi

    # Check for jq
    if ! which jq > /dev/null 2>&1; then
        log "Installing jq..."
        apt-get install -y jq
        deps_needed=1
    fi

    # Check for curl
    if ! which curl > /dev/null 2>&1; then
        log "Installing curl..."
        apt-get install -y curl
        deps_needed=1
    fi

    if [ $deps_needed -eq 0 ]; then
        log "All dependencies already installed"
    else
        log "Dependencies installed successfully"
    fi
}

setup_cron_jobs() {
    log "Setting up cron jobs..."

    # Remove any existing cron jobs for these scripts
    (crontab -l 2>/dev/null | grep -v "$AGENT_DIR/sqm-manager.sh" | \
        grep -v "$AGENT_DIR/sqm-ping-monitor.sh" | \
        grep -v "$AGENT_DIR/metrics-collector.sh") | crontab - 2>/dev/null || true

    # Add new cron jobs
    (
        crontab -l 2>/dev/null || true

        # SQM Manager: Run speedtest at 6 AM and 6:30 PM
        echo "0 6 * * * export PATH=\"\$PATH\"; export HOME=/root; $AGENT_DIR/sqm-manager.sh >> /var/log/sqm-manager.log 2>&1"
        echo "30 18 * * * export PATH=\"\$PATH\"; export HOME=/root; $AGENT_DIR/sqm-manager.sh >> /var/log/sqm-manager.log 2>&1"

        # SQM Ping Monitor: Every 5 minutes (skip during speedtest windows)
        echo "*/5 * * * * export PATH=\"\$PATH\"; export HOME=/root; if [ \"\$(date +\\%H:\\%M)\" != \"06:00\" ] && [ \"\$(date +\\%H:\\%M)\" != \"18:30\" ]; then $AGENT_DIR/sqm-ping-monitor.sh >> /var/log/sqm-ping-monitor.log 2>&1; fi"

        # Metrics Collector: Every minute
        echo "* * * * * export PATH=\"\$PATH\"; export HOME=/root; $AGENT_DIR/metrics-collector.sh >> /var/log/metrics-collector.log 2>&1"
    ) | crontab -

    log "Cron jobs configured successfully"
}

configure_agent() {
    log "Configuring agent..."
    log ""
    log "Please configure the following environment variables:"
    log "  - Edit $AGENT_DIR/sqm-manager.sh for SQM settings"
    log "  - Edit $AGENT_DIR/sqm-ping-monitor.sh for ping monitor settings"
    log "  - Edit $AGENT_DIR/metrics-collector.sh for InfluxDB settings"
    log ""
    log "Key settings to configure:"
    log "  WAN_INTERFACE: Your WAN interface (default: eth2)"
    log "  IFB_INTERFACE: Your IFB interface (default: ifbeth2)"
    log "  MAX_DOWNLOAD_SPEED: Max download speed in Mbps (default: 285)"
    log "  MIN_DOWNLOAD_SPEED: Min download speed in Mbps (default: 190)"
    log "  ISP_PING_HOST: Upstream ping target (default: 40.134.217.121)"
    log "  BASELINE_LATENCY: Baseline latency in ms (default: 17.9)"
    log "  INFLUXDB_URL: InfluxDB URL (default: http://your-influxdb-host:8086)"
    log "  INFLUXDB_TOKEN: InfluxDB authentication token"
    log "  INFLUXDB_ORG: InfluxDB organization"
    log "  INFLUXDB_BUCKET: InfluxDB bucket (default: network-metrics)"
    log ""
}

start_services() {
    log "Starting services..."

    # Run the on-boot script once to initialize
    log "Running on-boot script to initialize agent..."
    "$ON_BOOT_DIR/50-network-optimizer-agent.sh" >> "$INSTALL_LOG" 2>&1

    log "Services started successfully"
}

verify_installation() {
    log "Verifying installation..."

    local errors=0

    # Check if scripts are installed
    if [ ! -f "$ON_BOOT_DIR/50-network-optimizer-agent.sh" ]; then
        log "ERROR: On-boot script not found"
        errors=$((errors + 1))
    fi

    for script in sqm-manager.sh sqm-ping-monitor.sh metrics-collector.sh; do
        if [ ! -f "$AGENT_DIR/$script" ]; then
            log "ERROR: Script not found: $AGENT_DIR/$script"
            errors=$((errors + 1))
        fi

        if [ ! -x "$AGENT_DIR/$script" ]; then
            log "ERROR: Script not executable: $AGENT_DIR/$script"
            errors=$((errors + 1))
        fi
    done

    # Check if cron jobs are installed
    if ! crontab -l | grep -q "$AGENT_DIR/sqm-manager.sh"; then
        log "ERROR: SQM Manager cron job not found"
        errors=$((errors + 1))
    fi

    if ! crontab -l | grep -q "$AGENT_DIR/sqm-ping-monitor.sh"; then
        log "ERROR: SQM Ping Monitor cron job not found"
        errors=$((errors + 1))
    fi

    if ! crontab -l | grep -q "$AGENT_DIR/metrics-collector.sh"; then
        log "ERROR: Metrics Collector cron job not found"
        errors=$((errors + 1))
    fi

    # Check if dependencies are installed
    for cmd in speedtest bc jq curl; do
        if ! which "$cmd" > /dev/null 2>&1; then
            log "ERROR: Dependency not installed: $cmd"
            errors=$((errors + 1))
        fi
    done

    if [ $errors -eq 0 ]; then
        log "Verification passed: All components installed successfully"
        return 0
    else
        log "Verification failed: $errors errors found"
        return 1
    fi
}

###############################################################################
# MAIN EXECUTION
###############################################################################

main() {
    log "========================================="
    log "Network Optimizer Agent Installation"
    log "========================================="
    log "Installation directory: $AGENT_DIR"
    log "On-boot directory: $ON_BOOT_DIR"
    log ""

    check_root
    check_platform
    create_directories
    install_dependencies
    install_scripts
    setup_cron_jobs
    configure_agent
    start_services
    verify_installation

    log ""
    log "========================================="
    log "Installation Complete!"
    log "========================================="
    log ""
    log "Next steps:"
    log "1. Configure agent settings in the scripts (see above)"
    log "2. The agent will run automatically on boot"
    log "3. Monitor logs in /var/log/:"
    log "   - /var/log/network-optimizer-agent-setup.log"
    log "   - /var/log/sqm-manager.log"
    log "   - /var/log/sqm-ping-monitor.log"
    log "   - /var/log/metrics-collector.log"
    log ""
    log "To manually run scripts:"
    log "  - SQM Manager: $AGENT_DIR/sqm-manager.sh"
    log "  - Ping Monitor: $AGENT_DIR/sqm-ping-monitor.sh"
    log "  - Metrics Collector: $AGENT_DIR/metrics-collector.sh"
    log ""
    log "Cron jobs:"
    log "  - SQM Manager: 6:00 AM and 6:30 PM daily"
    log "  - Ping Monitor: Every 5 minutes (except during speedtest)"
    log "  - Metrics Collector: Every minute"
    log ""
    log "========================================="
}

# Run main function
main
