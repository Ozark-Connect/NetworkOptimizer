#!/bin/bash
#
# Network Optimizer Agent - Linux System Metrics Collector
#
# This script collects system metrics from a Linux host and pushes them
# to an InfluxDB instance. It's designed to run as a systemd service.
#
# Features:
# - Collects CPU, memory, disk, and load metrics
# - Optionally collects Docker container statistics
# - Formats as InfluxDB line protocol
# - POSTs to InfluxDB API
# - Lightweight and efficient
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

# Collection intervals (in seconds)
COLLECTION_INTERVAL="${COLLECTION_INTERVAL:-60}"

# Enable/disable metric collection
COLLECT_SYSTEM_STATS="${COLLECT_SYSTEM_STATS:-true}"
COLLECT_DOCKER_STATS="${COLLECT_DOCKER_STATS:-false}"
COLLECT_NETWORK_STATS="${COLLECT_NETWORK_STATS:-true}"
COLLECT_DISK_IO_STATS="${COLLECT_DISK_IO_STATS:-false}"

# Network interfaces to monitor (comma-separated)
NETWORK_INTERFACES="${NETWORK_INTERFACES:-eth0}"

# Log file
LOG_FILE="${LOG_FILE:-/var/log/network-optimizer-agent.log}"

# Dry run mode (don't send to InfluxDB, just log)
DRY_RUN="${DRY_RUN:-false}"

# Debug mode (verbose logging)
DEBUG="${DEBUG:-false}"

###############################################################################
# FUNCTIONS
###############################################################################

log() {
    if [ "$DEBUG" = "true" ] || [ "$DRY_RUN" = "true" ]; then
        echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"
    fi
}

log_error() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] ERROR: $*" | tee -a "$LOG_FILE"
}

log_info() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] INFO: $*" | tee -a "$LOG_FILE"
}

# Get current timestamp in nanoseconds
get_timestamp_ns() {
    echo $(($(date +%s) * 1000000000))
}

# Collect system statistics
collect_system_stats() {
    local timestamp=$1
    local metrics=""

    if [ "$COLLECT_SYSTEM_STATS" != "true" ]; then
        return
    fi

    # CPU usage
    # Use mpstat if available, otherwise use top
    if command -v mpstat &> /dev/null; then
        local cpu_usage
        cpu_usage=$(mpstat 1 1 | tail -n 1 | awk '{print 100 - $NF}')
        if [ -n "$cpu_usage" ]; then
            metrics="${metrics}system_cpu_usage,host=${HOSTNAME} value=${cpu_usage} ${timestamp}\n"
        fi
    else
        # Fallback to top
        local cpu_idle cpu_usage
        cpu_idle=$(top -bn2 -d 0.5 | grep "Cpu(s)" | tail -n 1 | awk '{print $8}' | sed 's/%id,//')
        if [ -n "$cpu_idle" ]; then
            cpu_usage=$(echo "100 - $cpu_idle" | bc -l)
            metrics="${metrics}system_cpu_usage,host=${HOSTNAME} value=${cpu_usage} ${timestamp}\n"
        fi
    fi

    # Memory usage
    local mem_total mem_used mem_free mem_available mem_usage_pct
    mem_total=$(free -b | grep Mem | awk '{print $2}')
    mem_used=$(free -b | grep Mem | awk '{print $3}')
    mem_free=$(free -b | grep Mem | awk '{print $4}')
    mem_available=$(free -b | grep Mem | awk '{print $7}')

    if [ -n "$mem_total" ] && [ "$mem_total" -gt 0 ]; then
        mem_usage_pct=$(echo "scale=2; $mem_used * 100 / $mem_total" | bc)
        metrics="${metrics}system_memory_total,host=${HOSTNAME} value=${mem_total} ${timestamp}\n"
        metrics="${metrics}system_memory_used,host=${HOSTNAME} value=${mem_used} ${timestamp}\n"
        metrics="${metrics}system_memory_free,host=${HOSTNAME} value=${mem_free} ${timestamp}\n"
        metrics="${metrics}system_memory_available,host=${HOSTNAME} value=${mem_available} ${timestamp}\n"
        metrics="${metrics}system_memory_usage_pct,host=${HOSTNAME} value=${mem_usage_pct} ${timestamp}\n"
    fi

    # Disk usage for root filesystem
    local disk_total disk_used disk_avail disk_usage_pct
    disk_total=$(df -B1 / | tail -n 1 | awk '{print $2}')
    disk_used=$(df -B1 / | tail -n 1 | awk '{print $3}')
    disk_avail=$(df -B1 / | tail -n 1 | awk '{print $4}')
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

    # Process count
    local process_count
    process_count=$(ps aux | wc -l)
    if [ -n "$process_count" ]; then
        metrics="${metrics}system_process_count,host=${HOSTNAME} value=${process_count} ${timestamp}\n"
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

    # Split interfaces by comma
    IFS=',' read -ra INTERFACES <<< "$NETWORK_INTERFACES"

    for interface in "${INTERFACES[@]}"; do
        interface=$(echo "$interface" | xargs)  # Trim whitespace

        if [ -d "/sys/class/net/$interface" ]; then
            local rx_bytes tx_bytes rx_packets tx_packets rx_errors tx_errors rx_dropped tx_dropped

            rx_bytes=$(cat /sys/class/net/$interface/statistics/rx_bytes 2>/dev/null || echo "0")
            tx_bytes=$(cat /sys/class/net/$interface/statistics/tx_bytes 2>/dev/null || echo "0")
            rx_packets=$(cat /sys/class/net/$interface/statistics/rx_packets 2>/dev/null || echo "0")
            tx_packets=$(cat /sys/class/net/$interface/statistics/tx_packets 2>/dev/null || echo "0")
            rx_errors=$(cat /sys/class/net/$interface/statistics/rx_errors 2>/dev/null || echo "0")
            tx_errors=$(cat /sys/class/net/$interface/statistics/tx_errors 2>/dev/null || echo "0")
            rx_dropped=$(cat /sys/class/net/$interface/statistics/rx_dropped 2>/dev/null || echo "0")
            tx_dropped=$(cat /sys/class/net/$interface/statistics/tx_dropped 2>/dev/null || echo "0")

            metrics="${metrics}network_rx_bytes,host=${HOSTNAME},interface=${interface} value=${rx_bytes} ${timestamp}\n"
            metrics="${metrics}network_tx_bytes,host=${HOSTNAME},interface=${interface} value=${tx_bytes} ${timestamp}\n"
            metrics="${metrics}network_rx_packets,host=${HOSTNAME},interface=${interface} value=${rx_packets} ${timestamp}\n"
            metrics="${metrics}network_tx_packets,host=${HOSTNAME},interface=${interface} value=${tx_packets} ${timestamp}\n"
            metrics="${metrics}network_rx_errors,host=${HOSTNAME},interface=${interface} value=${rx_errors} ${timestamp}\n"
            metrics="${metrics}network_tx_errors,host=${HOSTNAME},interface=${interface} value=${tx_errors} ${timestamp}\n"
            metrics="${metrics}network_rx_dropped,host=${HOSTNAME},interface=${interface} value=${rx_dropped} ${timestamp}\n"
            metrics="${metrics}network_tx_dropped,host=${HOSTNAME},interface=${interface} value=${tx_dropped} ${timestamp}\n"
        fi
    done

    echo -e "$metrics"
}

# Collect Docker container statistics
collect_docker_stats() {
    local timestamp=$1
    local metrics=""

    if [ "$COLLECT_DOCKER_STATS" != "true" ]; then
        return
    fi

    if ! command -v docker &> /dev/null; then
        log "Docker not found, skipping Docker stats"
        return
    fi

    # Get list of running containers
    local containers
    containers=$(docker ps --format '{{.Names}}' 2>/dev/null || echo "")

    if [ -z "$containers" ]; then
        log "No running containers found"
        return
    fi

    # Collect stats for each container
    while IFS= read -r container; do
        if [ -n "$container" ]; then
            # Get container stats (one-shot)
            local stats
            stats=$(docker stats --no-stream --format '{{.CPUPerc}},{{.MemUsage}},{{.NetIO}},{{.BlockIO}}' "$container" 2>/dev/null || echo "")

            if [ -n "$stats" ]; then
                local cpu_pct mem_usage mem_limit net_rx net_tx block_read block_write

                # Parse CPU percentage
                cpu_pct=$(echo "$stats" | cut -d',' -f1 | sed 's/%//')

                # Parse memory usage (format: "used / limit")
                mem_usage=$(echo "$stats" | cut -d',' -f2 | awk '{print $1}' | numfmt --from=auto 2>/dev/null || echo "0")
                mem_limit=$(echo "$stats" | cut -d',' -f2 | awk '{print $3}' | numfmt --from=auto 2>/dev/null || echo "0")

                # Parse network I/O (format: "rx / tx")
                net_rx=$(echo "$stats" | cut -d',' -f3 | awk '{print $1}' | numfmt --from=auto 2>/dev/null || echo "0")
                net_tx=$(echo "$stats" | cut -d',' -f3 | awk '{print $3}' | numfmt --from=auto 2>/dev/null || echo "0")

                # Parse block I/O (format: "read / write")
                block_read=$(echo "$stats" | cut -d',' -f4 | awk '{print $1}' | numfmt --from=auto 2>/dev/null || echo "0")
                block_write=$(echo "$stats" | cut -d',' -f4 | awk '{print $3}' | numfmt --from=auto 2>/dev/null || echo "0")

                # Add metrics
                if [ -n "$cpu_pct" ]; then
                    metrics="${metrics}docker_cpu_usage,host=${HOSTNAME},container=${container} value=${cpu_pct} ${timestamp}\n"
                fi
                if [ -n "$mem_usage" ]; then
                    metrics="${metrics}docker_memory_used,host=${HOSTNAME},container=${container} value=${mem_usage} ${timestamp}\n"
                fi
                if [ -n "$mem_limit" ]; then
                    metrics="${metrics}docker_memory_limit,host=${HOSTNAME},container=${container} value=${mem_limit} ${timestamp}\n"
                fi
                if [ -n "$net_rx" ]; then
                    metrics="${metrics}docker_network_rx,host=${HOSTNAME},container=${container} value=${net_rx} ${timestamp}\n"
                fi
                if [ -n "$net_tx" ]; then
                    metrics="${metrics}docker_network_tx,host=${HOSTNAME},container=${container} value=${net_tx} ${timestamp}\n"
                fi
                if [ -n "$block_read" ]; then
                    metrics="${metrics}docker_block_read,host=${HOSTNAME},container=${container} value=${block_read} ${timestamp}\n"
                fi
                if [ -n "$block_write" ]; then
                    metrics="${metrics}docker_block_write,host=${HOSTNAME},container=${container} value=${block_write} ${timestamp}\n"
                fi
            fi
        fi
    done <<< "$containers"

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
    log_info "Network Optimizer Agent starting..."
    log_info "Collection interval: ${COLLECTION_INTERVAL}s"

    # Main collection loop
    while true; do
        log "========================================="
        log "Metrics Collection Cycle Starting"
        log "========================================="

        # Get current timestamp
        timestamp=$(get_timestamp_ns)

        # Collect all metrics
        all_metrics=""

        system_metrics=$(collect_system_stats "$timestamp")
        if [ -n "$system_metrics" ]; then
            all_metrics="${all_metrics}${system_metrics}"
        fi

        network_metrics=$(collect_network_stats "$timestamp")
        if [ -n "$network_metrics" ]; then
            all_metrics="${all_metrics}${network_metrics}"
        fi

        docker_metrics=$(collect_docker_stats "$timestamp")
        if [ -n "$docker_metrics" ]; then
            all_metrics="${all_metrics}${docker_metrics}"
        fi

        # Send to InfluxDB
        send_to_influxdb "$all_metrics"

        log "========================================="
        log "Metrics Collection Cycle Complete"
        log "========================================="

        # Sleep until next collection interval
        sleep "$COLLECTION_INTERVAL"
    done
}

# Run main function
main
