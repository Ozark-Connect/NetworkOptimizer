#!/bin/bash
#
# Metrics Collector - System and Network Metrics to InfluxDB
#
# This script collects various metrics from the UniFi gateway and pushes
# them to an InfluxDB instance using the line protocol.
#
# Features:
# - Collects TC (traffic control) class statistics
# - Collects system metrics (CPU, memory, disk, load)
# - Collects network interface statistics
# - Formats as InfluxDB line protocol
# - POSTs to central hub API
#
# Author: Network Optimizer Agent
# Version: 1.0
#

set -e

###############################################################################
# CONFIGURATION VARIABLES - Customize these for your setup
###############################################################################

# InfluxDB connection settings
INFLUXDB_URL="${INFLUXDB_URL:-http://your-influxdb-host:8086}"
INFLUXDB_TOKEN="${INFLUXDB_TOKEN:-your-token-here}"
INFLUXDB_ORG="${INFLUXDB_ORG:-your-org}"
INFLUXDB_BUCKET="${INFLUXDB_BUCKET:-network-metrics}"

# Hostname (used as a tag)
HOSTNAME="${HOSTNAME:-$(hostname)}"

# WAN interface
WAN_INTERFACE="${WAN_INTERFACE:-eth2}"

# IFB interface
IFB_INTERFACE="${IFB_INTERFACE:-ifbeth2}"

# Log file
LOG_FILE="${LOG_FILE:-/var/log/metrics-collector.log}"

# Enable/disable metric collection
COLLECT_TC_STATS="${COLLECT_TC_STATS:-true}"
COLLECT_SYSTEM_STATS="${COLLECT_SYSTEM_STATS:-true}"
COLLECT_NETWORK_STATS="${COLLECT_NETWORK_STATS:-true}"

# Dry run mode (don't send to InfluxDB, just log)
DRY_RUN="${DRY_RUN:-false}"

###############################################################################
# FUNCTIONS
###############################################################################

log() {
    if [ "$DRY_RUN" = "true" ]; then
        echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"
    fi
}

log_error() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] ERROR: $*" >> "$LOG_FILE"
}

# Get current timestamp in nanoseconds
get_timestamp_ns() {
    echo $(($(date +%s) * 1000000000))
}

# Collect TC class statistics
collect_tc_stats() {
    local timestamp=$1
    local metrics=""

    if [ "$COLLECT_TC_STATS" != "true" ]; then
        return
    fi

    # Get TC class stats for IFB interface
    local tc_output
    tc_output=$(tc -s class show dev "$IFB_INTERFACE" 2>/dev/null || echo "")

    if [ -z "$tc_output" ]; then
        log_error "Failed to collect TC stats for $IFB_INTERFACE"
        return
    fi

    # Parse TC output for root class (1:1)
    local rate_mbit
    rate_mbit=$(echo "$tc_output" | grep "class htb 1:1" | grep -o 'rate [0-9.]*[MGK]bit' | head -n1 | awk '{print $2}' | sed 's/Mbit//')

    if [ -n "$rate_mbit" ]; then
        metrics="${metrics}tc_rate,host=${HOSTNAME},interface=${IFB_INTERFACE},class=root value=${rate_mbit} ${timestamp}\n"
    fi

    # Parse bytes sent and packets sent
    local bytes_sent packets_sent
    bytes_sent=$(echo "$tc_output" | grep -A 1 "class htb 1:1" | grep "Sent" | awk '{print $2}')
    packets_sent=$(echo "$tc_output" | grep -A 1 "class htb 1:1" | grep "Sent" | awk '{print $4}')

    if [ -n "$bytes_sent" ]; then
        metrics="${metrics}tc_bytes_sent,host=${HOSTNAME},interface=${IFB_INTERFACE},class=root value=${bytes_sent} ${timestamp}\n"
    fi

    if [ -n "$packets_sent" ]; then
        metrics="${metrics}tc_packets_sent,host=${HOSTNAME},interface=${IFB_INTERFACE},class=root value=${packets_sent} ${timestamp}\n"
    fi

    echo -e "$metrics"
}

# Collect system statistics
collect_system_stats() {
    local timestamp=$1
    local metrics=""

    if [ "$COLLECT_SYSTEM_STATS" != "true" ]; then
        return
    fi

    # CPU usage (idle percentage, inverted to get usage)
    local cpu_idle cpu_usage
    cpu_idle=$(top -bn1 | grep "CPU:" | awk '{print $8}' | sed 's/%//' | sed 's/id,//')
    if [ -n "$cpu_idle" ]; then
        cpu_usage=$(echo "100 - $cpu_idle" | bc)
        metrics="${metrics}system_cpu_usage,host=${HOSTNAME} value=${cpu_usage} ${timestamp}\n"
    fi

    # Memory usage
    local mem_total mem_used mem_free mem_usage_pct
    mem_total=$(free | grep Mem | awk '{print $2}')
    mem_used=$(free | grep Mem | awk '{print $3}')
    mem_free=$(free | grep Mem | awk '{print $4}')

    if [ -n "$mem_total" ] && [ "$mem_total" -gt 0 ]; then
        mem_usage_pct=$(echo "scale=2; $mem_used * 100 / $mem_total" | bc)
        metrics="${metrics}system_memory_total,host=${HOSTNAME} value=${mem_total} ${timestamp}\n"
        metrics="${metrics}system_memory_used,host=${HOSTNAME} value=${mem_used} ${timestamp}\n"
        metrics="${metrics}system_memory_free,host=${HOSTNAME} value=${mem_free} ${timestamp}\n"
        metrics="${metrics}system_memory_usage_pct,host=${HOSTNAME} value=${mem_usage_pct} ${timestamp}\n"
    fi

    # Disk usage
    local disk_total disk_used disk_avail disk_usage_pct
    disk_total=$(df / | tail -n 1 | awk '{print $2}')
    disk_used=$(df / | tail -n 1 | awk '{print $3}')
    disk_avail=$(df / | tail -n 1 | awk '{print $4}')
    disk_usage_pct=$(df / | tail -n 1 | awk '{print $5}' | sed 's/%//')

    if [ -n "$disk_total" ]; then
        metrics="${metrics}system_disk_total,host=${HOSTNAME},mount=/ value=${disk_total} ${timestamp}\n"
        metrics="${metrics}system_disk_used,host=${HOSTNAME},mount=/ value=${disk_used} ${timestamp}\n"
        metrics="${metrics}system_disk_avail,host=${HOSTNAME},mount=/ value=${disk_avail} ${timestamp}\n"
        metrics="${metrics}system_disk_usage_pct,host=${HOSTNAME},mount=/ value=${disk_usage_pct} ${timestamp}\n"
    fi

    # Load average
    local load_1m load_5m load_15m
    load_1m=$(uptime | awk -F'load average:' '{print $2}' | awk -F',' '{print $1}' | xargs)
    load_5m=$(uptime | awk -F'load average:' '{print $2}' | awk -F',' '{print $2}' | xargs)
    load_15m=$(uptime | awk -F'load average:' '{print $2}' | awk -F',' '{print $3}' | xargs)

    if [ -n "$load_1m" ]; then
        metrics="${metrics}system_load_1m,host=${HOSTNAME} value=${load_1m} ${timestamp}\n"
        metrics="${metrics}system_load_5m,host=${HOSTNAME} value=${load_5m} ${timestamp}\n"
        metrics="${metrics}system_load_15m,host=${HOSTNAME} value=${load_15m} ${timestamp}\n"
    fi

    # Uptime in seconds
    local uptime_sec
    uptime_sec=$(cat /proc/uptime | awk '{print $1}' | cut -d. -f1)
    if [ -n "$uptime_sec" ]; then
        metrics="${metrics}system_uptime,host=${HOSTNAME} value=${uptime_sec} ${timestamp}\n"
    fi

    echo -e "$metrics"
}

# Collect network interface statistics
collect_network_stats() {
    local timestamp=$1
    local metrics=""

    if [ "$COLLECT_NETWORK_STATS" != "true" ]; then
        return
    fi

    # WAN interface stats
    if [ -d "/sys/class/net/$WAN_INTERFACE" ]; then
        local rx_bytes tx_bytes rx_packets tx_packets rx_errors tx_errors

        rx_bytes=$(cat /sys/class/net/$WAN_INTERFACE/statistics/rx_bytes 2>/dev/null || echo "0")
        tx_bytes=$(cat /sys/class/net/$WAN_INTERFACE/statistics/tx_bytes 2>/dev/null || echo "0")
        rx_packets=$(cat /sys/class/net/$WAN_INTERFACE/statistics/rx_packets 2>/dev/null || echo "0")
        tx_packets=$(cat /sys/class/net/$WAN_INTERFACE/statistics/tx_packets 2>/dev/null || echo "0")
        rx_errors=$(cat /sys/class/net/$WAN_INTERFACE/statistics/rx_errors 2>/dev/null || echo "0")
        tx_errors=$(cat /sys/class/net/$WAN_INTERFACE/statistics/tx_errors 2>/dev/null || echo "0")

        metrics="${metrics}network_rx_bytes,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${rx_bytes} ${timestamp}\n"
        metrics="${metrics}network_tx_bytes,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${tx_bytes} ${timestamp}\n"
        metrics="${metrics}network_rx_packets,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${rx_packets} ${timestamp}\n"
        metrics="${metrics}network_tx_packets,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${tx_packets} ${timestamp}\n"
        metrics="${metrics}network_rx_errors,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${rx_errors} ${timestamp}\n"
        metrics="${metrics}network_tx_errors,host=${HOSTNAME},interface=${WAN_INTERFACE} value=${tx_errors} ${timestamp}\n"
    fi

    echo -e "$metrics"
}

# Send metrics to InfluxDB
send_to_influxdb() {
    local metrics=$1

    if [ -z "$metrics" ]; then
        log "No metrics to send"
        return
    fi

    if [ "$DRY_RUN" = "true" ]; then
        log "=== DRY RUN MODE ==="
        log "Would send the following metrics to InfluxDB:"
        echo -e "$metrics"
        log "===================="
        return
    fi

    # Send via curl
    local response
    response=$(curl -s -w "\n%{http_code}" -X POST \
        "${INFLUXDB_URL}/api/v2/write?org=${INFLUXDB_ORG}&bucket=${INFLUXDB_BUCKET}&precision=ns" \
        -H "Authorization: Token ${INFLUXDB_TOKEN}" \
        -H "Content-Type: text/plain; charset=utf-8" \
        --data-binary "$metrics" 2>&1)

    local http_code
    http_code=$(echo "$response" | tail -n 1)

    if [ "$http_code" = "204" ]; then
        log "Metrics sent successfully to InfluxDB"
    else
        log_error "Failed to send metrics to InfluxDB. HTTP code: $http_code"
        log_error "Response: $response"
    fi
}

###############################################################################
# MAIN EXECUTION
###############################################################################

main() {
    log "========================================="
    log "Metrics Collector Starting"
    log "========================================="

    # Get current timestamp
    timestamp=$(get_timestamp_ns)

    # Collect all metrics
    all_metrics=""

    tc_metrics=$(collect_tc_stats "$timestamp")
    if [ -n "$tc_metrics" ]; then
        all_metrics="${all_metrics}${tc_metrics}"
    fi

    system_metrics=$(collect_system_stats "$timestamp")
    if [ -n "$system_metrics" ]; then
        all_metrics="${all_metrics}${system_metrics}"
    fi

    network_metrics=$(collect_network_stats "$timestamp")
    if [ -n "$network_metrics" ]; then
        all_metrics="${all_metrics}${network_metrics}"
    fi

    # Send to InfluxDB
    send_to_influxdb "$all_metrics"

    log "========================================="
    log "Metrics Collector Complete"
    log "========================================="
}

# Run main function
main
