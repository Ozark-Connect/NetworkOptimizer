#!/bin/bash
#
# SQM Ping Monitor - Latency-based Bandwidth Adjustment
#
# This script monitors latency to an upstream target and dynamically adjusts
# traffic control (TC) settings to maintain optimal performance.
#
# Features:
# - Pings upstream target every 5 minutes
# - Detects latency deviation from baseline
# - Applies exponential rate adjustment:
#   * 0.97^n for high latency (reduces bandwidth)
#   * 1.04^n for recovery (increases bandwidth)
# - Skips execution during speedtest windows
# - Blends with 168-hour baseline data
#
# Author: Network Optimizer Agent
# Version: 1.0
#

set -e

###############################################################################
# CONFIGURATION VARIABLES - Customize these for your network
###############################################################################

# WAN interface (the physical interface)
WAN_INTERFACE="${WAN_INTERFACE:-eth2}"

# IFB interface (intermediate functional block for ingress shaping)
IFB_INTERFACE="${IFB_INTERFACE:-ifbeth2}"

# Ping target (upstream provider or stable internet host)
# Options:
# - ISP Host (closest, most sensitive)
# - Upstream Provider (recommended, balance of sensitivity and stability)
# - Stable Internet Host like 1.1.1.1 (less sensitive to local issues)
ISP_PING_HOST="${ISP_PING_HOST:-40.134.217.121}"

# Baseline latency in milliseconds (optimal unloaded ping)
BASELINE_LATENCY="${BASELINE_LATENCY:-17.9}"

# Latency threshold in milliseconds (deviation tolerance)
LATENCY_THRESHOLD="${LATENCY_THRESHOLD:-2.2}"

# Rate adjustment multipliers
LATENCY_DECREASE="${LATENCY_DECREASE:-0.97}"  # 3% decrease per deviation unit
LATENCY_INCREASE="${LATENCY_INCREASE:-1.04}"  # 4% increase for recovery

# Absolute maximum download speed (ceiling)
ABSOLUTE_MAX_DOWNLOAD_SPEED="${ABSOLUTE_MAX_DOWNLOAD_SPEED:-280}"

# Minimum download speed (floor)
MIN_DOWNLOAD_SPEED="${MIN_DOWNLOAD_SPEED:-180}"

# Maximum adjustment cap (percentage of absolute max)
MAX_ADJUSTMENT_CAP="${MAX_ADJUSTMENT_CAP:-0.95}"

# Files
SPEEDTEST_RESULTS_FILE="${SPEEDTEST_RESULTS_FILE:-/data/sqm-speedtest-result.txt}"
LOG_FILE="${LOG_FILE:-/var/log/sqm-ping-monitor.log}"

###############################################################################
# BASELINE LOOKUP TABLE
# Same 168-hour baseline as sqm-manager.sh
###############################################################################

declare -A BASELINE
# Monday (0)
BASELINE[0_0]="262"; BASELINE[0_1]="262"; BASELINE[0_2]="262"; BASELINE[0_3]="262"
BASELINE[0_4]="262"; BASELINE[0_5]="262"; BASELINE[0_6]="255"; BASELINE[0_7]="255"
BASELINE[0_8]="255"; BASELINE[0_9]="255"; BASELINE[0_10]="255"; BASELINE[0_11]="255"
BASELINE[0_12]="255"; BASELINE[0_13]="255"; BASELINE[0_14]="255"; BASELINE[0_15]="255"
BASELINE[0_16]="255"; BASELINE[0_17]="255"; BASELINE[0_18]="225"; BASELINE[0_19]="225"
BASELINE[0_20]="225"; BASELINE[0_21]="225"; BASELINE[0_22]="262"; BASELINE[0_23]="262"

# Tuesday (1)
BASELINE[1_0]="262"; BASELINE[1_1]="262"; BASELINE[1_2]="262"; BASELINE[1_3]="262"
BASELINE[1_4]="262"; BASELINE[1_5]="262"; BASELINE[1_6]="255"; BASELINE[1_7]="255"
BASELINE[1_8]="255"; BASELINE[1_9]="255"; BASELINE[1_10]="255"; BASELINE[1_11]="255"
BASELINE[1_12]="255"; BASELINE[1_13]="255"; BASELINE[1_14]="255"; BASELINE[1_15]="255"
BASELINE[1_16]="255"; BASELINE[1_17]="255"; BASELINE[1_18]="225"; BASELINE[1_19]="225"
BASELINE[1_20]="225"; BASELINE[1_21]="225"; BASELINE[1_22]="262"; BASELINE[1_23]="262"

# Wednesday (2)
BASELINE[2_0]="262"; BASELINE[2_1]="262"; BASELINE[2_2]="262"; BASELINE[2_3]="262"
BASELINE[2_4]="262"; BASELINE[2_5]="262"; BASELINE[2_6]="255"; BASELINE[2_7]="255"
BASELINE[2_8]="255"; BASELINE[2_9]="255"; BASELINE[2_10]="255"; BASELINE[2_11]="255"
BASELINE[2_12]="255"; BASELINE[2_13]="255"; BASELINE[2_14]="255"; BASELINE[2_15]="255"
BASELINE[2_16]="255"; BASELINE[2_17]="255"; BASELINE[2_18]="225"; BASELINE[2_19]="225"
BASELINE[2_20]="225"; BASELINE[2_21]="225"; BASELINE[2_22]="262"; BASELINE[2_23]="262"

# Thursday (3)
BASELINE[3_0]="262"; BASELINE[3_1]="262"; BASELINE[3_2]="262"; BASELINE[3_3]="262"
BASELINE[3_4]="262"; BASELINE[3_5]="262"; BASELINE[3_6]="255"; BASELINE[3_7]="255"
BASELINE[3_8]="255"; BASELINE[3_9]="255"; BASELINE[3_10]="255"; BASELINE[3_11]="255"
BASELINE[3_12]="255"; BASELINE[3_13]="255"; BASELINE[3_14]="255"; BASELINE[3_15]="255"
BASELINE[3_16]="255"; BASELINE[3_17]="255"; BASELINE[3_18]="225"; BASELINE[3_19]="225"
BASELINE[3_20]="225"; BASELINE[3_21]="225"; BASELINE[3_22]="262"; BASELINE[3_23]="262"

# Friday (4)
BASELINE[4_0]="262"; BASELINE[4_1]="262"; BASELINE[4_2]="262"; BASELINE[4_3]="262"
BASELINE[4_4]="262"; BASELINE[4_5]="262"; BASELINE[4_6]="255"; BASELINE[4_7]="255"
BASELINE[4_8]="255"; BASELINE[4_9]="255"; BASELINE[4_10]="255"; BASELINE[4_11]="255"
BASELINE[4_12]="255"; BASELINE[4_13]="255"; BASELINE[4_14]="255"; BASELINE[4_15]="255"
BASELINE[4_16]="255"; BASELINE[4_17]="255"; BASELINE[4_18]="225"; BASELINE[4_19]="225"
BASELINE[4_20]="225"; BASELINE[4_21]="225"; BASELINE[4_22]="262"; BASELINE[4_23]="262"

# Saturday (5)
BASELINE[5_0]="262"; BASELINE[5_1]="262"; BASELINE[5_2]="262"; BASELINE[5_3]="262"
BASELINE[5_4]="262"; BASELINE[5_5]="262"; BASELINE[5_6]="255"; BASELINE[5_7]="255"
BASELINE[5_8]="255"; BASELINE[5_9]="255"; BASELINE[5_10]="255"; BASELINE[5_11]="255"
BASELINE[5_12]="255"; BASELINE[5_13]="255"; BASELINE[5_14]="255"; BASELINE[5_15]="255"
BASELINE[5_16]="255"; BASELINE[5_17]="255"; BASELINE[5_18]="225"; BASELINE[5_19]="225"
BASELINE[5_20]="225"; BASELINE[5_21]="225"; BASELINE[5_22]="262"; BASELINE[5_23]="262"

# Sunday (6)
BASELINE[6_0]="262"; BASELINE[6_1]="262"; BASELINE[6_2]="262"; BASELINE[6_3]="262"
BASELINE[6_4]="262"; BASELINE[6_5]="262"; BASELINE[6_6]="255"; BASELINE[6_7]="255"
BASELINE[6_8]="255"; BASELINE[6_9]="255"; BASELINE[6_10]="255"; BASELINE[6_11]="255"
BASELINE[6_12]="255"; BASELINE[6_13]="255"; BASELINE[6_14]="255"; BASELINE[6_15]="255"
BASELINE[6_16]="255"; BASELINE[6_17]="255"; BASELINE[6_18]="230"; BASELINE[6_19]="230"
BASELINE[6_20]="230"; BASELINE[6_21]="238"; BASELINE[6_22]="256"; BASELINE[6_23]="262"

###############################################################################
# FUNCTIONS
###############################################################################

log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" | tee -a "$LOG_FILE"
}

# Update all TC classes on a device with new rate
update_all_tc_classes() {
    local device=$1
    local new_rate=$2

    log "Updating TC classes on $device to ${new_rate}Mbit..."

    # Update the root class 1:1 with rate and ceil
    tc class change dev "$device" parent 1: classid 1:1 htb \
        rate "${new_rate}Mbit" ceil "${new_rate}Mbit" \
        burst 1500b cburst 1500b

    # Get all child classes and update their ceil values
    tc class show dev "$device" | grep "parent 1:1" | while read -r line; do
        classid=$(echo "$line" | grep -o "class htb [0-9:]*" | awk '{print $3}')
        prio=$(echo "$line" | grep -o "prio [0-9]*" | awk '{print $2}')
        rate=$(echo "$line" | grep -o "rate [0-9]*[a-zA-Z]*" | awk '{print $2}')

        # Skip classes with rate != 64bit
        if [ "$rate" != "64bit" ]; then
            continue
        fi

        if [ -n "$classid" ]; then
            if [ -n "$prio" ]; then
                tc class change dev "$device" parent 1:1 classid "$classid" htb \
                    rate 64bit ceil "${new_rate}Mbit" \
                    burst 1500b cburst 1500b prio "$prio"
            else
                tc class change dev "$device" parent 1:1 classid "$classid" htb \
                    rate 64bit ceil "${new_rate}Mbit" \
                    burst 1500b cburst 1500b
            fi
        fi
    done
}

# Get baseline speed for current day/hour
get_baseline_speed() {
    local current_day
    current_day=$(date +%u)
    current_day=$((current_day - 1))

    local current_hour
    current_hour=$(date +%H | sed 's/^0//')

    local lookup_key="${current_day}_${current_hour}"
    local baseline_speed="${BASELINE[$lookup_key]}"

    log "Baseline lookup: day=$current_day hour=$current_hour key=$lookup_key"

    if [ -n "$baseline_speed" ]; then
        log "Baseline speed: ${baseline_speed} Mbps"
        echo "$baseline_speed"
    else
        log "No baseline data for this time"
        echo ""
    fi
}

###############################################################################
# MAIN EXECUTION
###############################################################################

main() {
    log "========================================="
    log "SQM Ping Monitor Starting"
    log "========================================="
    log "Ping Host: $ISP_PING_HOST"
    log "Baseline Latency: ${BASELINE_LATENCY} ms"
    log "Latency Threshold: ${LATENCY_THRESHOLD} ms"
    log ""

    # Check if speedtest results file exists
    if [ ! -f "$SPEEDTEST_RESULTS_FILE" ]; then
        log "ERROR: Speedtest result file not found: $SPEEDTEST_RESULTS_FILE"
        log "Run sqm-manager.sh first to establish baseline"
        exit 1
    fi

    # Get speedtest-based speed (already includes 5% overhead)
    SPEEDTEST_SPEED=$(cat "$SPEEDTEST_RESULTS_FILE" | awk '{print $4}')
    log "Speedtest baseline: $SPEEDTEST_SPEED Mbps"

    # Get baseline speed for current day/hour
    baseline_speed=$(get_baseline_speed)

    # Determine MAX_DOWNLOAD_SPEED
    MAX_DOWNLOAD_SPEED=$ABSOLUTE_MAX_DOWNLOAD_SPEED
    if [ -n "$baseline_speed" ]; then
        # Apply 5% overhead to baseline
        baseline_with_overhead=$(echo "scale=0; $baseline_speed * 1.05 / 1" | bc)

        # Cap at 285
        if [ "$baseline_with_overhead" -gt 285 ]; then
            baseline_with_overhead=285
        fi

        # Blend speedtest and baseline (60/40 toward baseline)
        MAX_DOWNLOAD_SPEED=$(echo "scale=0; ($baseline_with_overhead * 0.6 + $SPEEDTEST_SPEED * 0.4) / 1" | bc)
        log "Speedtest: $SPEEDTEST_SPEED Mbps, Baseline: $baseline_speed Mbps (+5% = $baseline_with_overhead Mbps)"
        log "Blended (60/40): $MAX_DOWNLOAD_SPEED Mbps"
    else
        MAX_DOWNLOAD_SPEED=$SPEEDTEST_SPEED
        log "Using speedtest result: $MAX_DOWNLOAD_SPEED Mbps"
    fi

    # Measure current latency
    log "Pinging $ISP_PING_HOST (20 packets, 0.25s interval)..."
    latency=$(ping -I "$WAN_INTERFACE" -c 20 -i 0.25 -q "$ISP_PING_HOST" 2>&1 | tail -n 1 | awk -F '/' '{print $5}')

    if [ -z "$latency" ] || [ "$latency" = "0" ]; then
        log "ERROR: Failed to measure latency"
        exit 1
    fi

    log "Current latency: ${latency} ms"

    # Calculate deviation count
    deviation_count=$(echo "($latency - $BASELINE_LATENCY) / $LATENCY_THRESHOLD" | bc)

    # Determine rate adjustment
    new_rate=$MAX_DOWNLOAD_SPEED

    if (( $(echo "$latency >= $BASELINE_LATENCY + $LATENCY_THRESHOLD" | bc -l) )); then
        # High latency detected
        log "High latency detected: $latency ms (threshold: $BASELINE_LATENCY + $LATENCY_THRESHOLD ms)"

        # Apply exponential decrease: rate * 0.97^n
        decrease_multiplier=$(echo "$LATENCY_DECREASE^$deviation_count" | bc -l)
        new_rate=$(echo "$MAX_DOWNLOAD_SPEED * $decrease_multiplier" | bc)

        log "Applying decrease multiplier: ${decrease_multiplier} (deviation count: $deviation_count)"
        log "New rate: ${new_rate} Mbps"

        # Enforce minimum
        if (( $(echo "$new_rate < $MIN_DOWNLOAD_SPEED" | bc) )); then
            new_rate=$MIN_DOWNLOAD_SPEED
            log "Enforcing minimum rate: $MIN_DOWNLOAD_SPEED Mbps"
        fi

    elif (( $(echo "$latency < $BASELINE_LATENCY - 0.4" | bc -l) )); then
        # Latency is reduced (better than baseline)
        log "Latency is reduced: $latency ms"

        lower_bound=$(echo "$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92" | bc)
        mid_bound=$(echo "$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.94" | bc)

        if (( $(echo "$MAX_DOWNLOAD_SPEED < $lower_bound" | bc -l) )); then
            log "Current baseline is low, applying x2 increase"
            new_rate=$(echo "$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE * $LATENCY_INCREASE" | bc -l)
        elif (( $(echo "$MAX_DOWNLOAD_SPEED < $mid_bound" | bc -l) )); then
            log "Current baseline is somewhat low, normalizing to $mid_bound Mbps"
            new_rate=$mid_bound
        else
            log "Keeping rate at MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED)"
            new_rate=$MAX_DOWNLOAD_SPEED
        fi

    else
        # Latency is normal
        log "Latency is normal: $latency ms"

        lower_bound=$(echo "$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.9" | bc)
        mid_bound=$(echo "$ABSOLUTE_MAX_DOWNLOAD_SPEED * 0.92" | bc)

        latency_diff=$(echo "$latency - $BASELINE_LATENCY" | bc -l)
        latency_normal=$(echo "$latency_diff <= 0.3" | bc -l)

        if (( $(echo "$MAX_DOWNLOAD_SPEED < $lower_bound" | bc -l) )) && (( latency_normal == 1 )); then
            log "Baseline is low and latency is within 0.3ms of normal, applying increase"
            new_rate=$(echo "$MAX_DOWNLOAD_SPEED * $LATENCY_INCREASE" | bc)
        elif (( $(echo "$MAX_DOWNLOAD_SPEED < $mid_bound" | bc -l) )) && (( latency_normal == 1 )); then
            log "Baseline is somewhat low and latency is within 0.3ms of normal, normalizing to $mid_bound Mbps"
            new_rate=$mid_bound
        else
            log "Keeping rate at MAX_DOWNLOAD_SPEED ($MAX_DOWNLOAD_SPEED), latency_diff=${latency_diff}ms"
            new_rate=$MAX_DOWNLOAD_SPEED
        fi
    fi

    # Apply maximum adjustment cap
    max_adjusted_rate=$(echo "$ABSOLUTE_MAX_DOWNLOAD_SPEED * $MAX_ADJUSTMENT_CAP" | bc)
    if (( $(echo "$new_rate > $max_adjusted_rate" | bc) )); then
        log "Applying adjustment cap: $new_rate > $max_adjusted_rate"
        new_rate=$max_adjusted_rate
    fi

    # Round to 1 decimal place
    new_rate=$(echo "scale=1; $new_rate / 1" | bc)

    # Cap at 285 Mbps
    if (( $(echo "$new_rate > 285" | bc) )); then
        new_rate=285
    fi

    log "Final adjusted rate: ${new_rate} Mbps"
    log ""

    # Update TC classes
    update_all_tc_classes "$IFB_INTERFACE" "$new_rate"

    # Show updated TC configuration
    log "Updated TC class configuration:"
    tc class show dev "$IFB_INTERFACE" | grep "class htb" | tee -a "$LOG_FILE"

    log "========================================="
    log "SQM Ping Monitor Complete"
    log "Adjusted rate to ${new_rate} Mbps based on latency"
    log "========================================="
}

# Run main function
main
