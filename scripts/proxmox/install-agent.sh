#!/usr/bin/env bash

# Network Optimizer on-site agent - Proxmox LXC Installation Script
# https://github.com/Ozark-Connect/NetworkOptimizer
#
# Creates a small Debian LXC on this Proxmox host and installs the on-site agent
# inside it. The container is the only thing this script builds - the agent itself
# is installed by the standard installer (scripts/agent/install-native.sh), so
# there is one agent install path however you get there.
#
# Generate the enrollment token in the server's web UI under
# Settings > Multi-Site > (site) > Agents > Set up agent.
#
# Usage:
#   bash -c "$(wget -qLO - https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer/main/scripts/proxmox/install-agent.sh)"
#
#   Every prompt below is also an option. Supplying it skips that question, so
#   building one agent per WAN is a flag-driven run each rather than an interview
#   each. --unattended takes the default for anything not supplied and asks nothing.
#
# Options:
#   --ct-id N              Container ID (default: next free)
#   --hostname NAME        Container hostname (default: netopt-agent)
#   --debian-version N     Debian major version for the template (default: 13)
#   --ram MB / --swap MB / --cores N / --disk GB
#   --storage NAME         Storage for the container rootfs
#   --template-storage NAME  Storage holding container templates
#   --bridge NAME          Network bridge (default: vmbr0)
#   --vlan TAG             VLAN tag for the container's interface
#   --ip ADDR              CIDR address, or "dhcp" (default: dhcp)
#   --gateway ADDR         Gateway, required with a static --ip
#   --dns ADDR             Nameserver for a static --ip
#   --server URL           Network Optimizer server this agent reports to
#   --token TOKEN          One-time enrollment token
#   --lan-speed-test       Host the LAN speed test page and iperf3 in this container
#   --speed-test-port N    Serve the speed test page on N instead of 24443
#   --insecure             Accept a self-signed cert on the server's reverse proxy
#   --unattended           Never prompt; take defaults for anything not supplied
#
# Requirements:
#   - Proxmox VE 7.0 or later
#   - Internet access for the container template and the agent binary

set -Eeuo pipefail

# =============================================================================
# Configuration Defaults
# =============================================================================
APP_NAME="Network Optimizer agent"
GITHUB_REPO="Ozark-Connect/NetworkOptimizer"
GITHUB_BRANCH="main"

# The agent is a single self-contained binary with no database and no Docker, so
# it needs a fraction of what the server container does.
DEFAULT_HOSTNAME="netopt-agent"
DEFAULT_DISK_SIZE="4"
DEFAULT_RAM="512"
DEFAULT_SWAP="256"
DEFAULT_CPU="1"
DEFAULT_BRIDGE="vmbr0"
DEFAULT_STORAGE="local-lvm"
DEFAULT_TEMPLATE_STORAGE="local"
DEFAULT_DEBIAN_VERSION="13"
DEFAULT_SPEED_TEST_PORT="24443"

# =============================================================================
# Colors and Formatting
# =============================================================================
readonly RD='\033[0;31m'
readonly GN='\033[0;32m'
readonly YW='\033[0;33m'
readonly BL='\033[0;34m'
readonly CY='\033[0;36m'
readonly BLD='\033[1m'
readonly DIM='\033[2m'
readonly CL='\033[0m'

# =============================================================================
# Helper Functions
# =============================================================================
msg_info()  { echo -e "${BL}[INFO]${CL} $1"; }
msg_ok()    { echo -e "${GN}[ OK ]${CL} $1"; }
msg_warn()  { echo -e "${YW}[WARN]${CL} $1"; }
msg_error() { echo -e "${RD}[FAIL]${CL} $1"; }

header() {
    echo
    echo -e "${BLD}${CY}=== $1 ===${CL}"
    echo
}

# Anything created before a failure is removed, so a half-built container is not
# left behind for the next run to trip over.
CT_CREATED=false
cleanup() {
    local code=$?
    if [[ $code -ne 0 ]] && [[ "$CT_CREATED" == "true" ]] && [[ -n "${CT_ID:-}" ]]; then
        msg_warn "Install failed - removing container $CT_ID"
        pct stop "$CT_ID" &>/dev/null || true
        pct destroy "$CT_ID" &>/dev/null || true
    fi
    exit $code
}
trap cleanup EXIT

check_root() {
    if [[ $EUID -ne 0 ]]; then
        msg_error "This script must be run as root on Proxmox VE."
        exit 1
    fi
}

check_proxmox() {
    if ! command -v pveversion &>/dev/null; then
        msg_error "This script must be run on Proxmox VE."
        echo -e "${DIM}To install the agent on a machine you already have, use scripts/agent/install-native.sh instead.${CL}"
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

validate_hostname() {
    if ! [[ "$1" =~ ^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?$ ]]; then
        msg_error "Invalid hostname: $1"
        return 1
    fi
    return 0
}

get_storage_list()          { pvesm status -content rootdir 2>/dev/null | awk 'NR>1 {print $1}' | tr '\n' ' '; }
get_template_storage_list() { pvesm status -content vztmpl 2>/dev/null | awk 'NR>1 {print $1}' | tr '\n' ' '; }
get_bridge_list()           { ip -o link show type bridge 2>/dev/null | awk -F': ' '{print $2}' | tr '\n' ' '; }

validate_storage() {
    pvesm status -content "$2" 2>/dev/null | awk 'NR>1 {print $1}' | grep -qw "$1"
}

find_debian_template() {
    local storage=$1 version=${2:-13}
    pveam update &>/dev/null || true
    local template
    template=$(pveam available --section system 2>/dev/null \
        | awk '{print $2}' | grep "^debian-${version}-standard" | sort -V | tail -n1)
    if [[ -z "$template" ]]; then
        template=$(pveam list "$storage" 2>/dev/null \
            | awk '{print $1}' | grep "debian-${version}-standard" | sed 's|.*/||' | sort -V | tail -n1)
    fi
    if [[ -z "$template" ]]; then
        msg_error "No Debian ${version} template found."
        exit 1
    fi
    echo "$template"
}

# =============================================================================
# Options
# =============================================================================
UNATTENDED=false
CT_ID=""; CT_HOSTNAME=""; DEBIAN_VERSION=""
CT_RAM=""; CT_SWAP=""; CT_CPU=""; CT_DISK=""
CT_STORAGE=""; TEMPLATE_STORAGE=""; CT_BRIDGE=""; CT_VLAN_TAG=""
CT_IP=""; CT_GW=""; CT_DNS=""
AGENT_SERVER=""; AGENT_TOKEN=""
AGENT_LAN_SPEED_TEST=""; AGENT_SPEED_TEST_PORT=""; AGENT_INSECURE=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --ct-id)            CT_ID="$2"; shift 2 ;;
        --hostname)         CT_HOSTNAME="$2"; shift 2 ;;
        --debian-version)   DEBIAN_VERSION="$2"; shift 2 ;;
        --ram)              CT_RAM="$2"; shift 2 ;;
        --swap)             CT_SWAP="$2"; shift 2 ;;
        --cores)            CT_CPU="$2"; shift 2 ;;
        --disk)             CT_DISK="$2"; shift 2 ;;
        --storage)          CT_STORAGE="$2"; shift 2 ;;
        --template-storage) TEMPLATE_STORAGE="$2"; shift 2 ;;
        --bridge)           CT_BRIDGE="$2"; shift 2 ;;
        --vlan)             CT_VLAN_TAG="$2"; shift 2 ;;
        --ip)               CT_IP="$2"; shift 2 ;;
        --gateway)          CT_GW="$2"; shift 2 ;;
        --dns)              CT_DNS="$2"; shift 2 ;;
        --server)           AGENT_SERVER="$2"; shift 2 ;;
        --token)            AGENT_TOKEN="$2"; shift 2 ;;
        --lan-speed-test)   AGENT_LAN_SPEED_TEST=true; shift ;;
        --speed-test-port)  AGENT_SPEED_TEST_PORT="$2"; AGENT_LAN_SPEED_TEST=true; shift 2 ;;
        --insecure)         AGENT_INSECURE=true; shift ;;
        --unattended)       UNATTENDED=true; shift ;;
        -h|--help)          sed -n '3,42p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) msg_error "Unknown option: $1"; exit 1 ;;
    esac
done

# Ask only for what was not supplied. In unattended mode nothing is asked and the
# default stands, which is what makes this scriptable for several WANs at once.
ask() {
    local prompt=$1 default=$2 current=$3 answer
    if [[ -n "$current" ]]; then echo "$current"; return; fi
    if [[ "$UNATTENDED" == "true" ]]; then echo "$default"; return; fi
    read -rp "$(echo -e "${BLD}${prompt}${CL} [${default}]: ")" answer </dev/tty
    echo "${answer:-$default}"
}

# =============================================================================
# Configuration
# =============================================================================
show_banner() {
    clear 2>/dev/null || true
    echo -e "${CY}${BLD}"
    echo "  Network Optimizer - on-site agent"
    echo "  Proxmox LXC installer"
    echo -e "${CL}"
    echo -e "${DIM}  Creates a container and installs the agent inside it.${CL}"
    echo
}

configure_container() {
    header "Container Configuration"

    local default_id
    default_id=$(get_next_ct_id)
    while true; do
        CT_ID=$(ask "Container ID" "$default_id" "$CT_ID")
        validate_ct_id "$CT_ID" && break
        [[ "$UNATTENDED" == "true" ]] && exit 1
        CT_ID=""
    done

    while true; do
        CT_HOSTNAME=$(ask "Hostname" "$DEFAULT_HOSTNAME" "$CT_HOSTNAME")
        validate_hostname "$CT_HOSTNAME" && break
        [[ "$UNATTENDED" == "true" ]] && exit 1
        CT_HOSTNAME=""
    done

    DEBIAN_VERSION=$(ask "Debian version" "$DEFAULT_DEBIAN_VERSION" "$DEBIAN_VERSION")
    CT_RAM=$(ask  "RAM in MB"  "$DEFAULT_RAM"  "$CT_RAM")
    CT_SWAP=$(ask "Swap in MB" "$DEFAULT_SWAP" "$CT_SWAP")
    CT_CPU=$(ask  "CPU cores"  "$DEFAULT_CPU"  "$CT_CPU")
    CT_DISK=$(ask "Disk size in GB" "$DEFAULT_DISK_SIZE" "$CT_DISK")

    if [[ -z "$CT_STORAGE" ]] && [[ "$UNATTENDED" != "true" ]]; then
        echo -e "${DIM}Available: $(get_storage_list)${CL}"
    fi
    CT_STORAGE=$(ask "Storage for container" "$DEFAULT_STORAGE" "$CT_STORAGE")
    if ! validate_storage "$CT_STORAGE" rootdir; then
        msg_error "Storage '$CT_STORAGE' cannot hold containers."
        exit 1
    fi

    if [[ -z "$TEMPLATE_STORAGE" ]] && [[ "$UNATTENDED" != "true" ]]; then
        echo -e "${DIM}Available: $(get_template_storage_list)${CL}"
    fi
    TEMPLATE_STORAGE=$(ask "Storage for templates" "$DEFAULT_TEMPLATE_STORAGE" "$TEMPLATE_STORAGE")
    if ! validate_storage "$TEMPLATE_STORAGE" vztmpl; then
        msg_error "Storage '$TEMPLATE_STORAGE' cannot hold templates."
        exit 1
    fi

    if [[ -z "$CT_BRIDGE" ]] && [[ "$UNATTENDED" != "true" ]]; then
        echo -e "${DIM}Available: $(get_bridge_list)${CL}"
    fi
    CT_BRIDGE=$(ask "Network bridge" "$DEFAULT_BRIDGE" "$CT_BRIDGE")

    # A WAN-context agent is often on its own VLAN, so this is asked rather than
    # buried in a flag.
    CT_VLAN_TAG=$(ask "VLAN tag (blank for none)" "" "$CT_VLAN_TAG")

    CT_IP=$(ask "IP address (CIDR, or dhcp)" "dhcp" "$CT_IP")
    if [[ "$CT_IP" != "dhcp" ]]; then
        CT_GW=$(ask "Gateway" "" "$CT_GW")
        if [[ -z "$CT_GW" ]]; then
            msg_error "A static IP needs a gateway."
            exit 1
        fi
        CT_DNS=$(ask "DNS server" "$CT_GW" "$CT_DNS")
    fi
}

configure_agent() {
    header "Agent Configuration"
    echo -e "${DIM}The token comes from the server's web UI: Settings > Multi-Site > (site) > Agents.${CL}"
    echo

    AGENT_SERVER=$(ask "Server URL (https://...)" "" "$AGENT_SERVER")
    if [[ -z "$AGENT_SERVER" ]]; then
        msg_error "The agent needs the server URL to report to."
        exit 1
    fi

    AGENT_TOKEN=$(ask "Enrollment token" "" "$AGENT_TOKEN")
    if [[ -z "$AGENT_TOKEN" ]]; then
        msg_error "The agent needs a one-time enrollment token."
        exit 1
    fi

    if [[ -z "$AGENT_LAN_SPEED_TEST" ]]; then
        local answer
        answer=$(ask "Host the LAN speed test in this container? (y/n)" "n" "")
        [[ "$answer" =~ ^[Yy] ]] && AGENT_LAN_SPEED_TEST=true || AGENT_LAN_SPEED_TEST=false
    fi
    if [[ "$AGENT_LAN_SPEED_TEST" == "true" ]]; then
        AGENT_SPEED_TEST_PORT=$(ask "Speed test port" "$DEFAULT_SPEED_TEST_PORT" "$AGENT_SPEED_TEST_PORT")
    fi
}

confirm_settings() {
    [[ "$UNATTENDED" == "true" ]] && return 0

    header "Review"
    echo -e "  Container:   ${CY}${CT_ID}${CL} (${CT_HOSTNAME}), Debian ${DEBIAN_VERSION}"
    echo -e "  Resources:   ${CT_CPU} core(s), ${CT_RAM} MB RAM, ${CT_DISK} GB disk"
    echo -e "  Storage:     ${CT_STORAGE} (templates: ${TEMPLATE_STORAGE})"
    echo -e "  Network:     ${CT_BRIDGE}${CT_VLAN_TAG:+ VLAN ${CT_VLAN_TAG}}, ${CT_IP}"
    echo -e "  Server:      ${CY}${AGENT_SERVER}${CL}"
    echo -e "  Speed test:  $([[ "$AGENT_LAN_SPEED_TEST" == "true" ]] && echo "yes (port ${AGENT_SPEED_TEST_PORT})" || echo "no")"
    echo
    local answer
    read -rp "$(echo -e "${BLD}Create it? (y/n)${CL} [y]: ")" answer </dev/tty
    if [[ -n "$answer" ]] && ! [[ "$answer" =~ ^[Yy] ]]; then
        msg_info "Cancelled."
        trap - EXIT
        exit 0
    fi
}

# =============================================================================
# Build
# =============================================================================
download_template() {
    header "Container Template"
    msg_info "Finding Debian ${DEBIAN_VERSION} template..."
    CT_TEMPLATE_FILE=$(find_debian_template "$TEMPLATE_STORAGE" "$DEBIAN_VERSION")
    msg_ok "Template: $CT_TEMPLATE_FILE"

    local template_path
    template_path=$(pvesm path "$TEMPLATE_STORAGE:vztmpl/$CT_TEMPLATE_FILE" 2>/dev/null || echo "")
    if [[ -f "$template_path" ]]; then
        msg_ok "Already downloaded"
        return 0
    fi

    msg_info "Downloading..."
    if ! pveam download "$TEMPLATE_STORAGE" "$CT_TEMPLATE_FILE"; then
        msg_error "Failed to download the container template."
        exit 1
    fi
    msg_ok "Downloaded"
}

create_container() {
    header "Creating Container"
    msg_info "Creating $CT_ID ($CT_HOSTNAME)..."

    local net_config="name=eth0,bridge=$CT_BRIDGE"
    if [[ "$CT_IP" == "dhcp" ]]; then
        net_config="${net_config},ip=dhcp"
    else
        net_config="${net_config},ip=${CT_IP},gw=${CT_GW}"
    fi
    [[ -n "$CT_VLAN_TAG" ]] && net_config="${net_config},tag=${CT_VLAN_TAG}"

    # Unprivileged, no nesting: the agent is a plain systemd service with no Docker
    # under it, so it needs none of the concessions the server container makes.
    pct create "$CT_ID" "$TEMPLATE_STORAGE:vztmpl/$CT_TEMPLATE_FILE" \
        --hostname "$CT_HOSTNAME" \
        --memory "$CT_RAM" \
        --swap "$CT_SWAP" \
        --cores "$CT_CPU" \
        --rootfs "$CT_STORAGE:$CT_DISK" \
        --net0 "$net_config" \
        --ostype debian \
        --unprivileged 1 \
        --onboot 1 \
        --start 0
    CT_CREATED=true

    if [[ "$CT_IP" != "dhcp" ]] && [[ -n "$CT_DNS" ]]; then
        pct set "$CT_ID" --nameserver "$CT_DNS"
    fi

    msg_ok "Container created"
}

start_container() {
    msg_info "Starting container..."
    pct start "$CT_ID"

    local max_wait=60 waited=0
    while ! pct exec "$CT_ID" -- test -f /etc/os-release 2>/dev/null; do
        sleep 1
        ((waited++))
        if [[ $waited -ge $max_wait ]]; then
            msg_error "Container failed to start within ${max_wait}s"
            exit 1
        fi
    done
    sleep 3
    msg_ok "Container started"
}

install_agent() {
    header "Installing the Agent"

    msg_info "Installing prerequisites..."
    pct exec "$CT_ID" -- bash -c "apt-get update -qq && apt-get install -y -qq curl ca-certificates iputils-ping traceroute" >/dev/null
    msg_ok "Prerequisites installed"

    # The standard installer does the actual work, so a container agent and a
    # bare-metal agent are the same install with the same layout and the same
    # upgrade path.
    local args="--server '${AGENT_SERVER}' --token '${AGENT_TOKEN}'"
    [[ "$AGENT_LAN_SPEED_TEST" == "true" ]] && args="$args --lan-speed-test"
    [[ -n "$AGENT_SPEED_TEST_PORT" ]] && args="$args --speed-test-port '${AGENT_SPEED_TEST_PORT}'"
    [[ "$AGENT_INSECURE" == "true" ]] && args="$args --insecure"

    msg_info "Running the agent installer inside the container..."
    if ! pct exec "$CT_ID" -- bash -c \
        "curl -fsSL https://raw.githubusercontent.com/${GITHUB_REPO}/${GITHUB_BRANCH}/scripts/agent/install-native.sh | bash -s -- ${args}"; then
        msg_error "The agent installer failed inside the container."
        echo -e "${DIM}The container is left in place so you can look: pct enter ${CT_ID}${CL}"
        CT_CREATED=false
        exit 1
    fi
    msg_ok "Agent installed"
}

get_container_ip() {
    pct exec "$CT_ID" -- hostname -I 2>/dev/null | awk '{print $1}'
}

show_completion() {
    header "Done"

    local ip mac
    ip=$(get_container_ip)
    mac=$(pct config "$CT_ID" | awk -F'hwaddr=' '/^net0:/ {split($2,a,","); print a[1]}')

    echo -e "${GN}${BLD}The agent is installed and enrolled.${CL}\n"
    echo -e "${BLD}Container:${CL}"
    echo -e "  ID / hostname: ${CY}${CT_ID}${CL} (${CT_HOSTNAME})"
    echo -e "  Address:       ${CY}${ip:-pending}${CL}"
    echo -e "  MAC:           ${CY}${mac:-unknown}${CL}"
    if [[ "$AGENT_LAN_SPEED_TEST" == "true" ]]; then
        echo -e "  Speed test:    ${CY}https://${ip}:${AGENT_SPEED_TEST_PORT}${CL}"
    fi
    echo
    echo -e "${BLD}Check on it:${CL}"
    echo -e "  ${DIM}pct exec ${CT_ID} -- systemctl status netopt-agent${CL}"
    echo -e "  ${DIM}pct exec ${CT_ID} -- journalctl -u netopt-agent -f${CL}"
    echo
    echo -e "${BLD}Monitoring a second WAN with this agent?${CL}"
    echo -e "  In UniFi Network, add a Policy-Based Route sending this container out that WAN:"
    echo -e "  ${DIM}Settings > Policy Table > Policy-Based Route - the WAN as the interface,${CL}"
    echo -e "  ${DIM}this container's Client Device (MAC ${mac:-above}) as the source, Any as the destination.${CL}"
    echo -e "  Then give the WAN a context in Monitoring > Setup and assign this agent to it."
    echo
}

main() {
    check_root
    check_proxmox
    show_banner
    configure_container
    configure_agent
    confirm_settings
    download_template
    create_container
    start_container
    install_agent
    show_completion
    trap - EXIT
}

main "$@"
