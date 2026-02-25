#!/usr/bin/env bash

# Network Optimizer for UniFi - Proxmox LXC Installation Script
# https://github.com/Ozark-Connect/NetworkOptimizer
#
# This script creates a Proxmox LXC container and installs Network Optimizer
# using Docker Compose. Designed for the homelab community.
#
# Usage:
#   bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install.sh)"
#
# Requirements:
#   - Proxmox VE 7.0 or later
#   - Internet access for downloading container template and Docker images
#   - Sufficient storage (10GB minimum recommended)

set -Eeuo pipefail

# =============================================================================
# Configuration Defaults
# =============================================================================
APP_NAME="Network Optimizer"
GITHUB_REPO="Ozark-Connect/NetworkOptimizer"
GITHUB_BRANCH="main"

# Container defaults
DEFAULT_HOSTNAME="network-optimizer"
DEFAULT_DISK_SIZE="10"
DEFAULT_RAM="2048"
DEFAULT_SWAP="512"
DEFAULT_CPU="2"
DEFAULT_BRIDGE="vmbr0"
DEFAULT_STORAGE="local-lvm"
DEFAULT_TEMPLATE_STORAGE="local"

# Application defaults
DEFAULT_TZ="America/New_York"
DEFAULT_SPEEDTEST_PORT="3005"

# =============================================================================
# Colors and Formatting
# =============================================================================
readonly RD='\033[0;31m'    # Red
readonly GN='\033[0;32m'    # Green
readonly YW='\033[0;33m'    # Yellow
readonly BL='\033[0;34m'    # Blue
readonly MG='\033[0;35m'    # Magenta
readonly CY='\033[0;36m'    # Cyan
readonly WH='\033[0;37m'    # White
readonly BLD='\033[1m'      # Bold
readonly DIM='\033[2m'      # Dim
readonly CL='\033[0m'       # Clear/Reset

# =============================================================================
# Helper Functions
# =============================================================================
msg_info() {
    echo -e "${BL}[INFO]${CL} $1"
}

msg_ok() {
    echo -e "${GN}[OK]${CL} $1"
}

msg_warn() {
    echo -e "${YW}[WARN]${CL} $1"
}

msg_error() {
    echo -e "${RD}[ERROR]${CL} $1"
}

header() {
    echo -e "\n${BLD}${CY}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${CL}"
    echo -e "${BLD}${CY}  $1${CL}"
    echo -e "${BLD}${CY}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${CL}\n"
}

cleanup() {
    local exit_code=$?
    if [[ $exit_code -ne 0 ]]; then
        echo ""
        msg_error "Installation failed. Check the output above for errors."
        if [[ -n "${CT_ID:-}" ]] && pct status "$CT_ID" &>/dev/null; then
            echo -e "${DIM}To clean up the failed container:${CL}"
            echo -e "${DIM}  pct stop $CT_ID 2>/dev/null; pct destroy $CT_ID${CL}"
        fi
    fi
}

trap cleanup EXIT

# =============================================================================
# Validation Functions
# =============================================================================
check_root() {
    if [[ $EUID -ne 0 ]]; then
        msg_error "This script must be run as root on Proxmox VE."
        echo -e "${DIM}Try: sudo bash install.sh${CL}"
        exit 1
    fi
}

check_proxmox() {
    if ! command -v pveversion &> /dev/null; then
        msg_error "This script must be run on Proxmox VE."
        echo -e "${DIM}Proxmox VE not detected. Please run this script on your Proxmox host.${CL}"
        exit 1
    fi

    local pve_version
    pve_version=$(pveversion --verbose | grep "pve-manager" | awk '{print $2}' | cut -d'/' -f1)
    msg_ok "Proxmox VE $pve_version detected"
}

get_next_ct_id() {
    local id=100
    while pct status "$id" &>/dev/null || qm status "$id" &>/dev/null 2>&1; do
        ((id++))
    done
    echo "$id"
}

validate_ct_id() {
    local id=$1
    if ! [[ "$id" =~ ^[0-9]+$ ]]; then
        msg_error "Container ID must be a number."
        return 1
    fi
    if [[ "$id" -lt 100 ]]; then
        msg_error "Container ID must be 100 or greater."
        return 1
    fi
    if pct status "$id" &>/dev/null || qm status "$id" &>/dev/null 2>&1; then
        msg_error "ID $id already exists (VM or container)."
        return 1
    fi
    return 0
}

get_storage_list() {
    pvesm status -content rootdir 2>/dev/null | awk 'NR>1 {print $1}' | tr '\n' ' '
}

get_template_storage_list() {
    pvesm status -content vztmpl 2>/dev/null | awk 'NR>1 {print $1}' | tr '\n' ' '
}

get_bridge_list() {
    ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | tr '\n' ' '
}

validate_storage() {
    local storage=$1
    local content_type=$2
    if ! pvesm status -content "$content_type" 2>/dev/null | awk 'NR>1 {print $1}' | grep -qw "$storage"; then
        return 1
    fi
    return 0
}

# Find the Debian template based on version selection
find_debian_template() {
    local storage=$1
    local version=${2:-12}

    # Update template list
    pveam update &>/dev/null || true

    # Find the latest debian template for the selected version
    local template
    template=$(pveam available -section system 2>/dev/null | grep "debian-${version}-standard" | tail -1 | awk '{print $2}')

    if [[ -z "$template" ]]; then
        msg_error "Could not find Debian ${version} template in repository."
        msg_info "Available templates:"
        pveam available -section system 2>/dev/null | grep -i debian | head -5
        exit 1
    fi

    echo "$template"
}

# =============================================================================
# Interactive Configuration
# =============================================================================
show_banner() {
    clear
    echo -e "${BLD}${MG}"
    cat << "EOF"
    _   __     __                      __     ____        __  _           _
   / | / /__  / /__      ______  _____/ /__  / __ \____  / /_(_)___ ___  (_)___  ___  _____
  /  |/ / _ \/ __/ | /| / / __ \/ ___/ //_/ / / / / __ \/ __/ / __ `__ \/ /_  / / _ \/ ___/
 / /|  /  __/ /_ | |/ |/ / /_/ / /  / ,<   / /_/ / /_/ / /_/ / / / / / / / / /_/  __/ /
/_/ |_/\___/\__/ |__/|__/\____/_/  /_/|_|  \____/ .___/\__/_/_/ /_/ /_/_/ /___/\___/_/
                                               /_/
EOF
    echo -e "${CL}"
    echo -e "${DIM}Proxmox LXC Installation Script${CL}"
    echo -e "${DIM}https://github.com/${GITHUB_REPO}${CL}\n"
}

configure_container() {
    header "Container Configuration"

    # Container ID
    local default_id
    default_id=$(get_next_ct_id)
    echo -e "${WH}Container ID${CL} ${DIM}(next available: $default_id)${CL}"
    read -rp "Enter CT ID [$default_id]: " CT_ID
    CT_ID=${CT_ID:-$default_id}
    if ! validate_ct_id "$CT_ID"; then
        exit 1
    fi

    # Hostname
    echo -e "\n${WH}Hostname${CL}"
    read -rp "Enter hostname [$DEFAULT_HOSTNAME]: " CT_HOSTNAME
    CT_HOSTNAME=${CT_HOSTNAME:-$DEFAULT_HOSTNAME}

    # Debian version
    echo -e "\n${WH}Debian Version${CL}"
    echo -e "${DIM}Debian 13 (Trixie) is the current stable release.${CL}"
    echo -e "${DIM}Debian 12 (Bookworm) also supported if preferred.${CL}"
    read -rp "Debian version [13]: " DEBIAN_VERSION
    DEBIAN_VERSION=${DEBIAN_VERSION:-13}

    # Resources
    echo -e "\n${WH}Resources${CL}"
    read -rp "RAM in MB [$DEFAULT_RAM]: " CT_RAM
    CT_RAM=${CT_RAM:-$DEFAULT_RAM}

    read -rp "Swap in MB [$DEFAULT_SWAP]: " CT_SWAP
    CT_SWAP=${CT_SWAP:-$DEFAULT_SWAP}

    read -rp "CPU cores [$DEFAULT_CPU]: " CT_CPU
    CT_CPU=${CT_CPU:-$DEFAULT_CPU}

    read -rp "Disk size in GB [$DEFAULT_DISK_SIZE]: " CT_DISK
    CT_DISK=${CT_DISK:-$DEFAULT_DISK_SIZE}

    # Storage
    local available_storage
    available_storage=$(get_storage_list)
    echo -e "\n${WH}Storage${CL} ${DIM}(available: $available_storage)${CL}"
    read -rp "Storage for container [$DEFAULT_STORAGE]: " CT_STORAGE
    CT_STORAGE=${CT_STORAGE:-$DEFAULT_STORAGE}

    if ! validate_storage "$CT_STORAGE" "rootdir"; then
        msg_error "Storage '$CT_STORAGE' not found or doesn't support rootdir content."
        msg_info "Available storage: $available_storage"
        exit 1
    fi

    local available_template_storage
    available_template_storage=$(get_template_storage_list)
    echo -e "\n${WH}Template Storage${CL} ${DIM}(available: $available_template_storage)${CL}"
    read -rp "Storage for templates [$DEFAULT_TEMPLATE_STORAGE]: " TEMPLATE_STORAGE
    TEMPLATE_STORAGE=${TEMPLATE_STORAGE:-$DEFAULT_TEMPLATE_STORAGE}

    if ! validate_storage "$TEMPLATE_STORAGE" "vztmpl"; then
        msg_error "Storage '$TEMPLATE_STORAGE' not found or doesn't support vztmpl content."
        msg_info "Available storage: $available_template_storage"
        exit 1
    fi

    # Network
    local available_bridges
    available_bridges=$(get_bridge_list)
    echo -e "\n${WH}Network Bridge${CL} ${DIM}(available: $available_bridges)${CL}"
    read -rp "Network bridge [$DEFAULT_BRIDGE]: " CT_BRIDGE
    CT_BRIDGE=${CT_BRIDGE:-$DEFAULT_BRIDGE}

    echo -e "\n${WH}IP Configuration${CL}"
    echo -e "${DIM}Enter 'dhcp' for DHCP or static IP in CIDR format (e.g., 192.168.1.100/24)${CL}"
    read -rp "IP address [dhcp]: " CT_IP
    CT_IP=${CT_IP:-dhcp}

    # Initialize gateway and DNS to empty
    CT_GW=""
    CT_DNS=""

    if [[ "$CT_IP" != "dhcp" ]]; then
        read -rp "Gateway IP: " CT_GW
        if [[ -z "$CT_GW" ]]; then
            msg_error "Gateway is required for static IP configuration."
            exit 1
        fi

        echo -e "${DIM}DNS server (press Enter to use gateway as DNS)${CL}"
        read -rp "DNS server [$CT_GW]: " CT_DNS
        CT_DNS=${CT_DNS:-$CT_GW}
    fi
}

configure_application() {
    header "Application Configuration"

    # Timezone
    echo -e "${WH}Timezone${CL}"
    echo -e "${DIM}Examples: America/New_York, America/Chicago, America/Los_Angeles, Europe/London${CL}"
    read -rp "Timezone [$DEFAULT_TZ]: " APP_TZ
    APP_TZ=${APP_TZ:-$DEFAULT_TZ}

    # OpenSpeedTest port
    echo -e "\n${WH}OpenSpeedTest Port${CL}"
    echo -e "${DIM}Browser-based speed testing (main web UI is always on port 8042)${CL}"
    read -rp "OpenSpeedTest port [$DEFAULT_SPEEDTEST_PORT]: " APP_SPEEDTEST_PORT
    APP_SPEEDTEST_PORT=${APP_SPEEDTEST_PORT:-$DEFAULT_SPEEDTEST_PORT}

    # iperf3 server
    echo -e "\n${WH}iperf3 Server${CL}"
    echo -e "${DIM}Enable CLI-based speed testing from network devices (port 5201)${CL}"
    read -rp "Enable iperf3 server? [y/N]: " iperf3_response
    if [[ "${iperf3_response,,}" =~ ^(y|yes)$ ]]; then
        APP_IPERF3_ENABLED="true"
    else
        APP_IPERF3_ENABLED="false"
    fi

    # Hostname-based access (for local DNS users)
    echo -e "\n${WH}Hostname-Based Access${CL}"
    echo -e "${DIM}Enable if you have local DNS (e.g., Pi-hole) resolving the container hostname.${CL}"
    echo -e "${DIM}Uses hostname for redirects, speed test links, and CORS. Requires working DNS.${CL}"
    echo -e "${DIM}If disabled, IP address is used instead (works without DNS setup).${CL}"
    read -rp "Enable hostname-based access? [y/N]: " hostname_redirect_response
    if [[ "${hostname_redirect_response,,}" =~ ^(y|yes)$ ]]; then
        APP_HOSTNAME_REDIRECT="true"
    else
        APP_HOSTNAME_REDIRECT="false"
    fi

    # Initialize Traefik and proxy variables
    APP_TRAEFIK_ENABLED="false"
    TRAEFIK_ACME_EMAIL=""
    TRAEFIK_CF_DNS_API_TOKEN=""
    TRAEFIK_OPTIMIZER_HOSTNAME=""
    TRAEFIK_SPEEDTEST_HOSTNAME=""
    APP_REVERSE_PROXY_HOST=""
    APP_GEOLOCATION="false"
    APP_OPENSPEEDTEST_HOST=""

    # HTTPS via Traefik
    echo -e "\n${WH}HTTPS via Traefik${CL}"
    echo -e "${DIM}Automatic HTTPS with Let's Encrypt certificates via Cloudflare DNS.${CL}"
    echo -e "${DIM}Enables geo location tagging and solves the HTTP/1.1 speed test requirement.${CL}"
    echo -e "${DIM}Requires a domain managed by Cloudflare.${CL}"
    read -rp "Set up HTTPS via Traefik? [y/N]: " traefik_response
    if [[ "${traefik_response,,}" =~ ^(y|yes)$ ]]; then
        APP_TRAEFIK_ENABLED="true"

        echo ""
        read -rp "ACME email (for Let's Encrypt): " TRAEFIK_ACME_EMAIL
        if [[ -z "$TRAEFIK_ACME_EMAIL" ]]; then
            msg_error "ACME email is required for Let's Encrypt."
            exit 1
        fi

        echo -e "${DIM}Create a token at: https://dash.cloudflare.com/profile/api-tokens${CL}"
        echo -e "${DIM}Required permission: Zone > DNS > Edit${CL}"
        read -rsp "Cloudflare DNS API token (hidden): " TRAEFIK_CF_DNS_API_TOKEN
        echo ""
        if [[ -z "$TRAEFIK_CF_DNS_API_TOKEN" ]]; then
            msg_error "Cloudflare API token is required."
            exit 1
        fi

        read -rp "Optimizer hostname (e.g., optimizer.example.com): " TRAEFIK_OPTIMIZER_HOSTNAME
        if [[ -z "$TRAEFIK_OPTIMIZER_HOSTNAME" ]]; then
            msg_error "Optimizer hostname is required."
            exit 1
        fi

        read -rp "SpeedTest hostname (e.g., speedtest.example.com): " TRAEFIK_SPEEDTEST_HOSTNAME
        if [[ -z "$TRAEFIK_SPEEDTEST_HOSTNAME" ]]; then
            msg_error "SpeedTest hostname is required."
            exit 1
        fi

        # Auto-configure reverse proxy and geo location
        APP_REVERSE_PROXY_HOST="$TRAEFIK_OPTIMIZER_HOSTNAME"
        APP_OPENSPEEDTEST_HOST="$TRAEFIK_SPEEDTEST_HOSTNAME"
        APP_GEOLOCATION="true"
    else
        # Reverse proxy
        echo -e "\n${WH}Reverse Proxy${CL}"
        echo -e "${DIM}If using a reverse proxy (Caddy, nginx, Traefik), enter the public hostname${CL}"
        echo -e "${DIM}Leave empty if accessing directly via IP${CL}"
        read -rp "Reverse proxy hostname (e.g., optimizer.example.com): " APP_REVERSE_PROXY_HOST
        APP_REVERSE_PROXY_HOST=${APP_REVERSE_PROXY_HOST:-}

        # Geo location tagging
        echo -e "\n${WH}Geo Location Tagging${CL}"
        echo -e "${DIM}Tag speed tests and Wi-Fi signal levels with GPS coordinates to map${CL}"
        echo -e "${DIM}coverage and identify dead zones across your property.${CL}"
        read -rp "Set up geo location tagging? [y/N]: " geolocation_response
        if [[ "${geolocation_response,,}" =~ ^(y|yes)$ ]]; then
            echo -e "\n${DIM}Geo location requires HTTPS (browser security requirement), and OpenSpeedTest${CL}"
            echo -e "${DIM}needs HTTP/1.1 for accurate speed results. Set up an HTTP/1.1 reverse proxy${CL}"
            echo -e "${DIM}(Caddy, nginx, etc.) pointing at the speed test server (port ${APP_SPEEDTEST_PORT}).${CL}"
            echo -e "${DIM}See .env.example in /opt/network-optimizer for a sample Caddy config.${CL}"
            echo ""
            read -rp "Speed test HTTPS hostname (e.g., speedtest.example.com): " APP_OPENSPEEDTEST_HOST
            if [[ -n "$APP_OPENSPEEDTEST_HOST" ]]; then
                APP_GEOLOCATION="true"
                # Mixed content check - main app also needs HTTPS
                if [[ -z "$APP_REVERSE_PROXY_HOST" ]]; then
                    echo -e "\n${YW}The main app also needs HTTPS to avoid mixed content blocking.${CL}"
                    echo -e "${DIM}Speed test results won't save unless the main app is behind HTTPS too.${CL}"
                    read -rp "Main app HTTPS hostname (e.g., optimizer.example.com): " APP_REVERSE_PROXY_HOST
                    APP_REVERSE_PROXY_HOST=${APP_REVERSE_PROXY_HOST:-}
                    if [[ -z "$APP_REVERSE_PROXY_HOST" ]]; then
                        msg_warn "No main app hostname set. Speed test results may not save from HTTPS."
                    fi
                fi
            else
                msg_warn "Hostname required for geo location. Skipping geo location setup."
            fi
        fi
    fi

    # SSH access
    echo -e "\n${WH}SSH Access${CL}"
    echo -e "${DIM}Enable SSH root login for direct container access (alternative to pct enter).${CL}"
    read -rp "Enable SSH root access? [y/N]: " ssh_response
    if [[ "${ssh_response,,}" =~ ^(y|yes)$ ]]; then
        APP_SSH_ENABLED="true"
    else
        APP_SSH_ENABLED="false"
    fi

    # Optional password
    echo -e "\n${WH}Admin Password${CL}"
    echo -e "${DIM}Leave empty for auto-generated password (shown in logs on first run).${CL}"
    echo -e "${DIM}Recommended: Set permanent password in Settings > Admin Password after login.${CL}"
    read -rsp "Admin password (hidden, press Enter to skip): " APP_PASSWORD
    echo ""
    APP_PASSWORD=${APP_PASSWORD:-}
}

confirm_settings() {
    header "Confirm Settings"

    echo -e "${BLD}Container Settings:${CL}"
    echo -e "  ID:        ${GN}$CT_ID${CL}"
    echo -e "  Hostname:  ${GN}$CT_HOSTNAME${CL}"
    echo -e "  Debian:    ${GN}$DEBIAN_VERSION${CL}"
    echo -e "  RAM:       ${GN}${CT_RAM}MB${CL}"
    echo -e "  Swap:      ${GN}${CT_SWAP}MB${CL}"
    echo -e "  CPU:       ${GN}${CT_CPU} cores${CL}"
    echo -e "  Disk:      ${GN}${CT_DISK}GB${CL}"
    echo -e "  Storage:   ${GN}$CT_STORAGE${CL}"
    echo -e "  Bridge:    ${GN}$CT_BRIDGE${CL}"
    echo -e "  IP:        ${GN}$CT_IP${CL}"
    if [[ "$CT_IP" != "dhcp" ]]; then
        echo -e "  Gateway:   ${GN}$CT_GW${CL}"
        echo -e "  DNS:       ${GN}$CT_DNS${CL}"
    fi
    if [[ "$APP_SSH_ENABLED" == "true" ]]; then
        echo -e "  SSH:       ${GN}enabled${CL}"
    else
        echo -e "  SSH:       ${DIM}disabled${CL}"
    fi

    echo -e "\n${BLD}Application Settings:${CL}"
    echo -e "  Timezone:       ${GN}$APP_TZ${CL}"
    echo -e "  Web UI Port:    ${GN}8042${CL} ${DIM}(fixed)${CL}"
    echo -e "  Speedtest Port: ${GN}$APP_SPEEDTEST_PORT${CL}"
    if [[ "$APP_IPERF3_ENABLED" == "true" ]]; then
        echo -e "  iperf3 Server:  ${GN}enabled${CL} ${DIM}(port 5201)${CL}"
    else
        echo -e "  iperf3 Server:  ${DIM}disabled${CL}"
    fi
    if [[ "$APP_HOSTNAME_REDIRECT" == "true" ]]; then
        echo -e "  Host Redirect:  ${GN}$CT_HOSTNAME${CL}"
    else
        echo -e "  Host Redirect:  ${DIM}disabled${CL}"
    fi
    if [[ "$APP_TRAEFIK_ENABLED" == "true" ]]; then
        echo -e "  Traefik HTTPS:  ${GN}enabled${CL}"
        echo -e "  ACME Email:     ${GN}$TRAEFIK_ACME_EMAIL${CL}"
        echo -e "  Optimizer:      ${GN}https://$TRAEFIK_OPTIMIZER_HOSTNAME${CL}"
        echo -e "  SpeedTest:      ${GN}https://$TRAEFIK_SPEEDTEST_HOSTNAME${CL}"
    else
        if [[ -n "$APP_REVERSE_PROXY_HOST" ]]; then
            echo -e "  Reverse Proxy:  ${GN}$APP_REVERSE_PROXY_HOST${CL}"
        else
            echo -e "  Reverse Proxy:  ${DIM}none${CL}"
        fi
        if [[ "$APP_GEOLOCATION" == "true" ]]; then
            echo -e "  Geo Location:   ${GN}${APP_OPENSPEEDTEST_HOST}${CL} ${DIM}(HTTPS)${CL}"
        else
            echo -e "  Geo Location:   ${DIM}disabled${CL}"
        fi
    fi
    if [[ -n "$APP_PASSWORD" ]]; then
        echo -e "  Password:       ${GN}(set)${CL}"
    else
        echo -e "  Password:       ${YW}(auto-generate)${CL}"
    fi

    echo ""
    read -rp "Proceed with installation? [Y/n]: " confirm
    confirm=${confirm:-Y}
    if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
        msg_warn "Installation cancelled."
        exit 0
    fi
}

# =============================================================================
# Installation Functions
# =============================================================================
download_template() {
    header "Downloading Container Template"

    msg_info "Finding Debian ${DEBIAN_VERSION} template..."
    CT_TEMPLATE_FILE=$(find_debian_template "$TEMPLATE_STORAGE" "$DEBIAN_VERSION")
    msg_ok "Found template: $CT_TEMPLATE_FILE"

    local template_path
    template_path=$(pvesm path "$TEMPLATE_STORAGE:vztmpl/$CT_TEMPLATE_FILE" 2>/dev/null || echo "")

    if [[ -f "$template_path" ]]; then
        msg_ok "Template already downloaded"
        return 0
    fi

    msg_info "Downloading template..."
    if ! pveam download "$TEMPLATE_STORAGE" "$CT_TEMPLATE_FILE"; then
        msg_error "Failed to download container template."
        echo -e "${DIM}Try manually: pveam download $TEMPLATE_STORAGE $CT_TEMPLATE_FILE${CL}"
        exit 1
    fi

    msg_ok "Template downloaded successfully"
}

create_container() {
    header "Creating LXC Container"

    msg_info "Creating container $CT_ID ($CT_HOSTNAME)..."

    local net_config
    if [[ "$CT_IP" == "dhcp" ]]; then
        net_config="name=eth0,bridge=$CT_BRIDGE,ip=dhcp"
    else
        net_config="name=eth0,bridge=$CT_BRIDGE,ip=$CT_IP,gw=$CT_GW"
    fi

    # Create privileged container with nesting enabled (required for Docker)
    # Note: Privileged is more reliable for Docker; unprivileged requires extra config
    # that varies by Proxmox version and kernel
    pct create "$CT_ID" "$TEMPLATE_STORAGE:vztmpl/$CT_TEMPLATE_FILE" \
        --hostname "$CT_HOSTNAME" \
        --memory "$CT_RAM" \
        --swap "$CT_SWAP" \
        --cores "$CT_CPU" \
        --rootfs "$CT_STORAGE:$CT_DISK" \
        --net0 "$net_config" \
        --ostype debian \
        --unprivileged 0 \
        --features nesting=1 \
        --onboot 1 \
        --start 0

    # Set DNS for static IP
    if [[ "$CT_IP" != "dhcp" ]] && [[ -n "$CT_DNS" ]]; then
        pct set "$CT_ID" --nameserver "$CT_DNS"
    fi

    # Fix Docker-in-LXC compatibility: runc's CVE-2025-52881 security patch uses
    # detached procfs mounts that AppArmor blocks (even in privileged containers).
    # Disabling AppArmor confinement and ensuring writable proc/sys fixes this.
    # See: https://forum.proxmox.com/threads/175437/
    {
        echo "lxc.apparmor.profile: unconfined"
        echo "lxc.mount.auto: proc:rw sys:rw"
    } >> "/etc/pve/lxc/${CT_ID}.conf"

    msg_ok "Container created"
}

start_container() {
    msg_info "Starting container..."
    pct start "$CT_ID"

    # Wait for container to be fully up
    local max_wait=60
    local waited=0
    while ! pct exec "$CT_ID" -- test -f /etc/os-release 2>/dev/null; do
        sleep 1
        ((waited++))
        if [[ $waited -ge $max_wait ]]; then
            msg_error "Container failed to start within ${max_wait}s"
            exit 1
        fi
    done

    # Additional wait for networking
    sleep 3

    msg_ok "Container started"
}

configure_ssh() {
    if [[ "$APP_SSH_ENABLED" != "true" ]]; then
        return
    fi

    msg_info "Configuring SSH root access..."

    # Enable root login via SSH
    pct exec "$CT_ID" -- bash -c '
        # Ensure SSH is installed
        apt-get update -qq && apt-get install -y -qq openssh-server

        # Enable root login with password
        sed -i "s/#PermitRootLogin prohibit-password/PermitRootLogin yes/g" /etc/ssh/sshd_config
        sed -i "s/PermitRootLogin prohibit-password/PermitRootLogin yes/g" /etc/ssh/sshd_config

        # Ensure SSH starts on boot and restart it
        systemctl enable ssh
        systemctl restart ssh
    '

    msg_ok "SSH root access enabled"
    msg_info "Set root password with: pct exec $CT_ID -- passwd"
}

install_dependencies() {
    header "Installing Dependencies"

    msg_info "Updating package lists..."
    pct exec "$CT_ID" -- bash -c "apt-get update -qq"
    msg_ok "Package lists updated"

    msg_info "Installing prerequisites..."
    pct exec "$CT_ID" -- bash -c "DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
        ca-certificates \
        curl \
        gnupg \
        lsb-release \
        sudo \
        wget"
    msg_ok "Prerequisites installed"

    msg_info "Installing Docker (this may take a minute)..."
    pct exec "$CT_ID" -- bash -c '
        set -e

        # Add Docker official GPG key
        install -m 0755 -d /etc/apt/keyrings
        curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc

        # Add Docker repository
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/debian $(. /etc/os-release && echo "$VERSION_CODENAME") stable" > /etc/apt/sources.list.d/docker.list

        # Install Docker
        apt-get update -qq
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

        # Enable and start Docker
        systemctl enable docker
        systemctl start docker
    '
    msg_ok "Docker installed"

    # Verify Docker is running
    if ! pct exec "$CT_ID" -- docker info &>/dev/null; then
        msg_error "Docker failed to start properly"
        msg_info "Checking Docker status..."
        pct exec "$CT_ID" -- systemctl status docker --no-pager || true
        exit 1
    fi
    msg_ok "Docker is running"
}

deploy_application() {
    header "Deploying $APP_NAME"

    local app_dir="/opt/network-optimizer"

    msg_info "Creating application directory..."
    pct exec "$CT_ID" -- mkdir -p "$app_dir"
    msg_ok "Directory created"

    msg_info "Creating docker-compose.yml..."
    # Generate compose file that pulls from GHCR (no build context)
    # This is simpler and faster than building from source
    local compose_content='services:
  network-optimizer:
    image: ghcr.io/ozark-connect/network-optimizer:latest
    container_name: network-optimizer
    restart: unless-stopped
    network_mode: host
    volumes:
      - ./data:/app/data
      - ./ssh-keys:/app/ssh-keys:ro
      - ./logs:/app/logs
    environment:
      - TZ=${TZ:-America/Chicago}
      - BIND_LOCALHOST_ONLY=${BIND_LOCALHOST_ONLY:-false}
      - APP_PASSWORD=${APP_PASSWORD:-}
      - HOST_IP=${HOST_IP:-}
      - HOST_NAME=${HOST_NAME:-}
      - REVERSE_PROXIED_HOST_NAME=${REVERSE_PROXIED_HOST_NAME:-}
      - OPENSPEEDTEST_PORT=${OPENSPEEDTEST_PORT:-3005}
      - OPENSPEEDTEST_HOST=${OPENSPEEDTEST_HOST:-}
      - OPENSPEEDTEST_HTTPS=${OPENSPEEDTEST_HTTPS:-false}
      - OPENSPEEDTEST_HTTPS_PORT=${OPENSPEEDTEST_HTTPS_PORT:-443}
      - Iperf3Server__Enabled=${IPERF3_SERVER_ENABLED:-false}
      - Logging__LogLevel__Default=${LOG_LEVEL:-Information}
      - Logging__LogLevel__NetworkOptimizer=${APP_LOG_LEVEL:-Information}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8042/api/health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s

  network-optimizer-speedtest:
    image: ghcr.io/ozark-connect/speedtest:latest
    container_name: network-optimizer-speedtest
    restart: unless-stopped
    ports:
      - "${OPENSPEEDTEST_PORT:-3005}:3000"
    environment:
      - TZ=${TZ:-America/Chicago}
      - HOST_IP=${HOST_IP:-}
      - HOST_NAME=${HOST_NAME:-}
      - OPENSPEEDTEST_PORT=${OPENSPEEDTEST_PORT:-3005}
      - OPENSPEEDTEST_HOST=${OPENSPEEDTEST_HOST:-}
      - OPENSPEEDTEST_HTTPS=${OPENSPEEDTEST_HTTPS:-false}
      - OPENSPEEDTEST_HTTPS_PORT=${OPENSPEEDTEST_HTTPS_PORT:-443}
      - REVERSE_PROXIED_HOST_NAME=${REVERSE_PROXIED_HOST_NAME:-}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:3000/"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 10s
'
    local encoded_compose
    encoded_compose=$(echo "$compose_content" | base64 -w 0)
    pct exec "$CT_ID" -- bash -c "echo '$encoded_compose' | base64 -d > $app_dir/docker-compose.yml"
    msg_ok "docker-compose.yml created"

    msg_info "Downloading .env.example (reference)..."
    pct exec "$CT_ID" -- curl -fsSL \
        "https://raw.githubusercontent.com/${GITHUB_REPO}/${GITHUB_BRANCH}/docker/.env.example" \
        -o "$app_dir/.env.example"
    msg_ok ".env.example downloaded"

    msg_info "Creating environment configuration..."

    # Get container IP for HOST_IP setting
    local container_ip
    container_ip=$(pct exec "$CT_ID" -- hostname -I 2>/dev/null | awk '{print $1}')

    # Build .env content
    local env_content="# Network Optimizer Configuration
# Generated by Proxmox installation script
# See .env.example for all available options

TZ=${APP_TZ}

# Host identity for speed testing and CORS
HOST_IP=${container_ip}"

    # Only set HOST_NAME if user explicitly enabled hostname redirects
    if [[ "$APP_HOSTNAME_REDIRECT" == "true" ]]; then
        env_content="${env_content}
HOST_NAME=${CT_HOSTNAME}"
    fi

    env_content="${env_content}

# Speed testing
OPENSPEEDTEST_PORT=${APP_SPEEDTEST_PORT}
IPERF3_SERVER_ENABLED=${APP_IPERF3_ENABLED}"

    if [[ -n "$APP_REVERSE_PROXY_HOST" ]]; then
        env_content="${env_content}

# Reverse proxy configuration
REVERSE_PROXIED_HOST_NAME=${APP_REVERSE_PROXY_HOST}"
    fi

    if [[ "$APP_TRAEFIK_ENABLED" == "true" ]]; then
        env_content="${env_content}

# Traefik handles public access - bind app to localhost only
BIND_LOCALHOST_ONLY=true"
    fi

    if [[ "$APP_GEOLOCATION" == "true" ]]; then
        env_content="${env_content}

# Geo location tagging (HTTPS speed test)
OPENSPEEDTEST_HTTPS=true
OPENSPEEDTEST_HOST=${APP_OPENSPEEDTEST_HOST}"
    fi

    if [[ -n "$APP_PASSWORD" ]]; then
        env_content="${env_content}

# Admin password
APP_PASSWORD=${APP_PASSWORD}"
    fi

    # Write .env file using base64 encoding to handle special characters
    local encoded_content
    encoded_content=$(echo "$env_content" | base64 -w 0)
    pct exec "$CT_ID" -- bash -c "echo '$encoded_content' | base64 -d > $app_dir/.env"

    msg_ok "Environment configured"

    # Create data directories
    msg_info "Creating data directories..."
    pct exec "$CT_ID" -- bash -c "mkdir -p $app_dir/data $app_dir/logs $app_dir/ssh-keys"
    msg_ok "Data directories created"

    msg_info "Pulling Docker images (this may take a few minutes)..."
    pct exec "$CT_ID" -- bash -c "cd $app_dir && docker compose pull"
    msg_ok "Docker images pulled"

    msg_info "Starting services..."
    pct exec "$CT_ID" -- bash -c "cd $app_dir && docker compose up -d"
    msg_ok "Services started"
}

deploy_traefik() {
    if [[ "$APP_TRAEFIK_ENABLED" != "true" ]]; then
        return
    fi

    header "Deploying Traefik HTTPS Proxy"

    local proxy_dir="/opt/network-optimizer-proxy"
    local proxy_repo="Ozark-Connect/NetworkOptimizer-Proxy"
    local proxy_branch="main"

    msg_info "Creating proxy directory..."
    pct exec "$CT_ID" -- mkdir -p "$proxy_dir/dynamic" "$proxy_dir/acme"
    msg_ok "Directory created"

    msg_info "Downloading Traefik configuration files..."
    pct exec "$CT_ID" -- curl -fsSL \
        "https://raw.githubusercontent.com/${proxy_repo}/${proxy_branch}/docker-compose.yml" \
        -o "$proxy_dir/docker-compose.yml"
    pct exec "$CT_ID" -- curl -fsSL \
        "https://raw.githubusercontent.com/${proxy_repo}/${proxy_branch}/config.example.yml" \
        -o "$proxy_dir/config.example.yml"
    pct exec "$CT_ID" -- curl -fsSL \
        "https://raw.githubusercontent.com/${proxy_repo}/${proxy_branch}/.env.example" \
        -o "$proxy_dir/.env.example"
    msg_ok "Configuration files downloaded"

    msg_info "Generating dynamic configuration..."
    pct exec "$CT_ID" -- bash -c "
        sed -e 's/optimizer\\.example\\.com/${TRAEFIK_OPTIMIZER_HOSTNAME}/g' \
            -e 's/speedtest\\.example\\.com/${TRAEFIK_SPEEDTEST_HOSTNAME}/g' \
            -e 's|http://localhost:3005|http://localhost:${APP_SPEEDTEST_PORT}|g' \
            '$proxy_dir/config.example.yml' > '$proxy_dir/dynamic/config.yml'
    "
    msg_ok "Dynamic configuration generated"

    msg_info "Creating environment file..."
    local proxy_env_content="# Traefik Proxy - Generated by Proxmox installation script
ACME_EMAIL=${TRAEFIK_ACME_EMAIL}
CF_DNS_API_TOKEN=${TRAEFIK_CF_DNS_API_TOKEN}"
    local encoded_proxy_env
    encoded_proxy_env=$(echo "$proxy_env_content" | base64 -w 0)
    pct exec "$CT_ID" -- bash -c "echo '$encoded_proxy_env' | base64 -d > $proxy_dir/.env"
    msg_ok "Environment file created"

    msg_info "Setting up certificate storage..."
    pct exec "$CT_ID" -- bash -c "touch $proxy_dir/acme/acme.json && chmod 600 $proxy_dir/acme/acme.json"
    msg_ok "Certificate storage ready"

    msg_info "Pulling Traefik image..."
    pct exec "$CT_ID" -- bash -c "cd $proxy_dir && docker compose pull"
    msg_ok "Traefik image pulled"

    msg_info "Starting Traefik..."
    pct exec "$CT_ID" -- bash -c "cd $proxy_dir && docker compose up -d"
    msg_ok "Traefik started"
}

wait_for_healthy() {
    header "Waiting for Application"

    local max_wait=120
    local waited=0

    echo -ne "${BL}[...]${CL} Waiting for health check..."

    while ! pct exec "$CT_ID" -- curl -sf http://localhost:8042/api/health &>/dev/null; do
        sleep 2
        ((waited+=2))
        echo -ne "\r${BL}[...]${CL} Waiting for health check... ${waited}s    "
        if [[ $waited -ge $max_wait ]]; then
            echo ""
            msg_warn "Health check timed out, but services may still be starting."
            msg_info "Check status with: pct exec $CT_ID -- docker logs network-optimizer"
            return 1
        fi
    done

    echo ""
    msg_ok "Application is healthy"
    return 0
}

get_container_ip() {
    pct exec "$CT_ID" -- hostname -I 2>/dev/null | awk '{print $1}'
}

show_completion() {
    header "Installation Complete!"

    local container_ip
    container_ip=$(get_container_ip)

    echo -e "${GN}${BLD}$APP_NAME has been successfully installed!${CL}\n"

    echo -e "${BLD}Access Information:${CL}"
    if [[ "$APP_TRAEFIK_ENABLED" == "true" ]]; then
        echo -e "  Web UI:        ${CY}https://${TRAEFIK_OPTIMIZER_HOSTNAME}${CL}"
        echo -e "  SpeedTest:     ${CY}https://${TRAEFIK_SPEEDTEST_HOSTNAME}${CL} ${DIM}(geo location enabled)${CL}"
    else
        echo -e "  Web UI:        ${CY}http://${container_ip}:8042${CL}"
        echo -e "  OpenSpeedTest: ${CY}http://${container_ip}:${APP_SPEEDTEST_PORT}${CL}"
    fi
    if [[ "$APP_IPERF3_ENABLED" == "true" ]]; then
        echo -e "  iperf3 Server: ${CY}${container_ip}:5201${CL}"
    fi
    if [[ "$APP_TRAEFIK_ENABLED" != "true" ]]; then
        if [[ -n "$APP_REVERSE_PROXY_HOST" ]]; then
            echo -e "  Reverse Proxy: ${CY}https://${APP_REVERSE_PROXY_HOST}${CL}"
        fi
        if [[ "$APP_GEOLOCATION" == "true" ]]; then
            echo -e "  Speed Test:    ${CY}https://${APP_OPENSPEEDTEST_HOST}${CL} ${DIM}(geo location enabled)${CL}"
        fi
    fi

    if [[ -z "$APP_PASSWORD" ]]; then
        echo -e "\n${BLD}Admin Password:${CL}"
        echo -e "  ${YW}Auto-generated on first run. View with:${CL}"
        echo -e "  ${DIM}pct exec $CT_ID -- docker logs network-optimizer 2>&1 | grep -A5 'AUTO-GENERATED'${CL}"
        echo -e "  ${DIM}Set a permanent password in Settings > Admin Password after login.${CL}"
    fi

    echo -e "\n${BLD}Container Management:${CL}"
    echo -e "  Console:  ${DIM}pct enter $CT_ID${CL}"
    echo -e "  Start:    ${DIM}pct start $CT_ID${CL}"
    echo -e "  Stop:     ${DIM}pct stop $CT_ID${CL}"
    echo -e "  Logs:     ${DIM}pct exec $CT_ID -- docker logs -f network-optimizer${CL}"
    if [[ "$APP_SSH_ENABLED" == "true" ]]; then
        echo -e "  SSH:      ${DIM}ssh root@${container_ip}${CL}"
        echo -e "  ${YW}Set root password: pct exec $CT_ID -- passwd${CL}"
    fi

    echo -e "\n${BLD}Application Management:${CL}"
    echo -e "  Directory:  ${DIM}/opt/network-optimizer${CL}"
    echo -e "  Config:     ${DIM}/opt/network-optimizer/.env${CL}"
    echo -e "  Reference:  ${DIM}/opt/network-optimizer/.env.example${CL} ${DIM}(all options)${CL}"
    echo -e "  Update:     ${DIM}pct exec $CT_ID -- bash -c 'cd /opt/network-optimizer && docker compose pull && docker compose up -d'${CL}"

    if [[ "$APP_TRAEFIK_ENABLED" == "true" ]]; then
        echo -e "\n${BLD}Traefik HTTPS Proxy:${CL}"
        echo -e "  ${YW}Certificates may take a minute to issue on first start.${CL}"
        echo -e "  Directory:  ${DIM}/opt/network-optimizer-proxy${CL}"
        echo -e "  Config:     ${DIM}/opt/network-optimizer-proxy/dynamic/config.yml${CL}"
        echo -e "  Logs:       ${DIM}pct exec $CT_ID -- docker logs -f traefik-proxy${CL}"
        echo -e "  Update:     ${DIM}pct exec $CT_ID -- bash -c 'cd /opt/network-optimizer-proxy && docker compose pull && docker compose up -d'${CL}"
    fi

    echo -e "\n${BLD}First Run:${CL}"
    if [[ "$APP_TRAEFIK_ENABLED" == "true" ]]; then
        echo -e "  1. Open ${CY}https://${TRAEFIK_OPTIMIZER_HOSTNAME}${CL} ${DIM}(wait ~1 min for certificates)${CL}"
    else
        echo -e "  1. Open ${CY}http://${container_ip}:8042${CL}"
    fi
    echo -e "  2. Log in with the auto-generated password (or the one you set)"
    echo -e "  3. Go to Settings and connect to your UniFi controller"
    echo -e "  4. Run your first Security Audit!"

    echo -e "\n${BLD}Documentation:${CL}"
    echo -e "  ${DIM}https://github.com/${GITHUB_REPO}/blob/main/docker/DEPLOYMENT.md${CL}"

    echo ""
}

# =============================================================================
# Main Execution
# =============================================================================
main() {
    show_banner

    # Pre-flight checks
    check_root
    check_proxmox

    # Interactive configuration
    configure_container
    configure_application
    confirm_settings

    # Installation
    download_template
    create_container
    start_container
    configure_ssh
    install_dependencies
    deploy_application
    deploy_traefik
    wait_for_healthy || true

    # Done
    show_completion
}

# Run main - this script is designed to be executed, not sourced
main "$@"
