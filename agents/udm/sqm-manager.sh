#!/bin/bash
#
# SQM Manager - Speedtest-based Bandwidth Management
#
# This script performs periodic bandwidth testing and adjusts traffic control (TC)
# settings on the UniFi gateway to optimize Smart Queue Management (SQM).
#
# Features:
# - Runs Ookla Speedtest to measure actual bandwidth
# - Consults 168-hour baseline lookup table (day/hour specific)
# - Applies blending algorithm:
#   * 60/40 (baseline/measured) when within 10% of baseline
#   * 80/20 (baseline/measured) when below 10% of baseline
# - Updates HTB tc classes on ifb device
# - Enforces min/max bandwidth limits
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

# Maximum download speed (ceiling) in Mbps
MAX_DOWNLOAD_SPEED="${MAX_DOWNLOAD_SPEED:-285}"

# Minimum download speed (floor) in Mbps
MIN_DOWNLOAD_SPEED="${MIN_DOWNLOAD_SPEED:-190}"

# Speed multiplier (overhead percentage)
# 1.05 = 5% overhead to prevent saturation
DOWNLOAD_SPEED_MULTIPLIER="${DOWNLOAD_SPEED_MULTIPLIER:-1.05}"

# Maximum adjustment cap (percentage of max speed)
# 0.95 = allow adjustments up to 95% of MAX_DOWNLOAD_SPEED
MAX_ADJUSTMENT_CAP="${MAX_ADJUSTMENT_CAP:-0.95}"

# Baseline threshold percentage
# If speedtest is within this percentage of baseline, use 60/40 blend
# Otherwise use 80/20 blend (favor baseline more heavily)
BASELINE_THRESHOLD_PCT="${BASELINE_THRESHOLD_PCT:-0.90}"

# Files
SPEEDTEST_RESULTS_FILE="${SPEEDTEST_RESULTS_FILE:-/data/sqm-speedtest-result.txt}"
LOG_FILE="${LOG_FILE:-/var/log/sqm-manager.log}"

###############################################################################
# BASELINE LOOKUP TABLE
# 168-hour baseline: indexed by day_hour (0=Mon, 6=Sun)
# Customize these values based on your ISP's performance patterns
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
    # Only update classes with rate=64bit (standard UniFi SQM classes)
    tc class show dev "$device" | grep "parent 1:1" | while read -r line; do
        # Extract the classid (e.g., "1:2", "1:4", etc.)
        classid=$(echo "$line" | grep -o "class htb [0-9:]*" | awk '{print $3}')
        # Extract the prio value if present
        prio=$(echo "$line" | grep -o "prio [0-9]*" | awk '{print $2}')
        # Extract the rate value to check if it's a special class
        rate=$(echo "$line" | grep -o "rate [0-9]*[a-zA-Z]*" | awk '{print $2}')

        # Skip classes with rate != 64bit (these are special UniFi-configured classes)
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

# Run speedtest and return download speed in Mbps
run_speedtest() {
    log "Running speedtest on interface $WAN_INTERFACE..."

    if ! which speedtest > /dev/null; then
        log "ERROR: speedtest command not found"
        exit 1
    fi

    # Run speedtest and parse JSON output
    local speedtest_output
    speedtest_output=$(speedtest --accept-license --format=json --interface="$WAN_INTERFACE" 2>&1)

    # Extract download speed (bytes/sec)
    local download_bytes_per_sec
    download_bytes_per_sec=$(echo "$speedtest_output" | jq -r .download.bandwidth)

    # Convert bytes/sec to Mbps
    local download_mbps
    download_mbps=$(echo "scale=0; $download_bytes_per_sec * 8 / 1000000" | bc)

    log "Speedtest measured: ${download_mbps} Mbps"
    echo "$download_mbps"
}

# Get baseline speed for current day/hour
get_baseline_speed() {
    # Get current day of week (0=Mon, 6=Sun) and hour
    local current_day
    current_day=$(date +%u)  # 1=Mon, 7=Sun
    current_day=$((current_day - 1))  # Convert to 0=Mon, 6=Sun

    local current_hour
    current_hour=$(date +%H | sed 's/^0//')  # Remove leading zero

    local lookup_key="${current_day}_${current_hour}"
    local baseline_speed="${BASELINE[$lookup_key]}"

    if [ -n "$baseline_speed" ]; then
        log "Baseline for day $current_day hour $current_hour: ${baseline_speed} Mbps"
        echo "$baseline_speed"
    else
        log "No baseline data for day $current_day hour $current_hour"
        echo ""
    fi
}

# Blend measured and baseline speeds
blend_speeds() {
    local measured=$1
    local baseline=$2

    # Calculate threshold (90% of baseline by default)
    local threshold
    threshold=$(echo "scale=0; $baseline * $BASELINE_THRESHOLD_PCT / 1" | bc)

    local blended_speed
    if [ "$measured" -ge "$threshold" ]; then
        # Within threshold: weight toward baseline (60/40)
        blended_speed=$(echo "scale=0; ($baseline * 0.6 + $measured * 0.4) / 1" | bc)
        log "Speedtest within threshold: blending (60/40 baseline/measured) → ${blended_speed} Mbps"
    else
        # Below threshold: heavily favor baseline (80/20)
        blended_speed=$(echo "scale=0; ($baseline * 0.8 + $measured * 0.2) / 1" | bc)
        log "Speedtest below threshold: weighting toward baseline (80/20) → ${blended_speed} Mbps"
    fi

    echo "$blended_speed"
}

###############################################################################
# MAIN EXECUTION
###############################################################################

main() {
    log "========================================="
    log "SQM Manager Starting"
    log "========================================="
    log "WAN Interface: $WAN_INTERFACE"
    log "IFB Interface: $IFB_INTERFACE"
    log "Max Speed: $MAX_DOWNLOAD_SPEED Mbps"
    log "Min Speed: $MIN_DOWNLOAD_SPEED Mbps"
    log ""

    # Show current TC configuration
    log "Current TC class configuration:"
    tc class show dev "$IFB_INTERFACE" | grep "class htb" | tee -a "$LOG_FILE"
    log ""

    # Set SQM to max speed before testing (to get accurate measurement)
    update_all_tc_classes "$IFB_INTERFACE" "$MAX_DOWNLOAD_SPEED"

    # Run speedtest
    download_speed_mbps=$(run_speedtest)

    # Apply floor
    if [ "$download_speed_mbps" -lt "$MIN_DOWNLOAD_SPEED" ]; then
        log "Applying floor: $download_speed_mbps < $MIN_DOWNLOAD_SPEED"
        download_speed_mbps=$MIN_DOWNLOAD_SPEED
    fi
    log "Download speed after floor: ${download_speed_mbps} Mbps"

    # Get baseline speed
    baseline_speed=$(get_baseline_speed)

    # Blend speeds if baseline exists
    if [ -n "$baseline_speed" ]; then
        blended_speed=$(blend_speeds "$download_speed_mbps" "$baseline_speed")

        # Apply overhead multiplier
        download_speed_mbps=$(echo "scale=0; $blended_speed * $DOWNLOAD_SPEED_MULTIPLIER / 1" | bc)
        log "Speed after overhead multiplier (${DOWNLOAD_SPEED_MULTIPLIER}): ${download_speed_mbps} Mbps"
    else
        # No baseline: just apply overhead to measured speed
        download_speed_mbps=$(echo "scale=0; $download_speed_mbps * $DOWNLOAD_SPEED_MULTIPLIER / 1" | bc)
        log "Speed after overhead multiplier (${DOWNLOAD_SPEED_MULTIPLIER}): ${download_speed_mbps} Mbps"
    fi

    # Cap at MAX_DOWNLOAD_SPEED
    if [ "$download_speed_mbps" -gt "$MAX_DOWNLOAD_SPEED" ]; then
        log "Capping at max speed: $download_speed_mbps > $MAX_DOWNLOAD_SPEED"
        download_speed_mbps=$MAX_DOWNLOAD_SPEED
    fi

    # Apply maximum adjustment cap
    max_adjusted_rate=$(echo "$MAX_DOWNLOAD_SPEED * $MAX_ADJUSTMENT_CAP / 1" | bc)
    if (( $(echo "$download_speed_mbps > $max_adjusted_rate" | bc -l) )); then
        log "Applying adjustment cap: $download_speed_mbps > $max_adjusted_rate"
        download_speed_mbps=$max_adjusted_rate
    fi

    # Round to integer
    download_speed_mbps=$(echo "scale=0; $download_speed_mbps / 1" | bc)

    log "Final adjusted speed: ${download_speed_mbps} Mbps"
    log ""

    # Save result to file for ping monitor to use
    echo "Measured download speed: $download_speed_mbps Mbps" > "$SPEEDTEST_RESULTS_FILE"

    # Update TC classes
    update_all_tc_classes "$IFB_INTERFACE" "$download_speed_mbps"

    # Show updated TC configuration
    log "Updated TC class configuration:"
    tc class show dev "$IFB_INTERFACE" | grep "class htb" | tee -a "$LOG_FILE"

    log "========================================="
    log "SQM Manager Complete"
    log "Adjusted speedtest-based rate to ${download_speed_mbps} Mbps on $WAN_INTERFACE"
    log "========================================="
}

# Run main function
main
