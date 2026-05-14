#!/bin/bash
# 10-journald-volatile.sh: Eliminate eMMC writes from system logging
#
# Two-part fix:
#   1. journald: Storage=volatile (logs to RAM), ForwardToSyslog=no
#   2. syslog-ng: Comment out log routes to local file destinations on eMMC,
#      keeping remote syslog (UDP to console) and tmpfs destinations intact
#
# Logs are still forwarded to the console via syslog-ng remote destination.
# IDS/IPS threat alerts continue flowing via /var/log/ulog/ (tmpfs).
# Zero eMMC writes from logging after this script runs.
#
# Compatible with all UniFi Cloud Gateway models.

LOG_TAG="journald-volatile"
JOURNALD_CONF="/etc/systemd/journald.conf"
SYSLOG_CONF_DIR="/etc/syslog-ng/conf.d"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
}

# ─── Part 1: journald volatile ───

CURRENT_STORAGE=$(grep "^Storage=" "$JOURNALD_CONF" 2>/dev/null | cut -d= -f2)
CURRENT_SYSLOG=$(grep "^ForwardToSyslog=" "$JOURNALD_CONF" 2>/dev/null | cut -d= -f2)

JOURNALD_CHANGED=false

if [ "$CURRENT_STORAGE" != "volatile" ]; then
    sed -i 's/^Storage=.*/Storage=volatile/' "$JOURNALD_CONF"
    log "Changed Storage=$CURRENT_STORAGE -> volatile"
    JOURNALD_CHANGED=true
fi

if [ "$CURRENT_SYSLOG" != "no" ]; then
    sed -i 's/^ForwardToSyslog=.*/ForwardToSyslog=no/' "$JOURNALD_CONF"
    log "Changed ForwardToSyslog=$CURRENT_SYSLOG -> no"
    JOURNALD_CHANGED=true
fi

if [ "$JOURNALD_CHANGED" = true ]; then
    systemctl restart systemd-journald
    log "journald restarted. Logs now in RAM only (/run/log/journal/)."
else
    log "journald already configured."
fi

# ─── Part 1b: syslog-ng persist file → tmpfs ───
#
# syslog-ng writes its state to a persist file on every config/state change.
# Default location is /var/log/.syslog-ng.persist (eMMC). Redirect to /run/
# (tmpfs) to eliminate these continuous eMMC writes. The persist file only
# tracks source read positions — losing it on reboot just means syslog-ng
# re-reads from the current journal position, which is fine for volatile logs.

PERSIST_CONF="/etc/default/syslog-ng-persist"
PERSIST_TMPFS="/run/syslog-ng.persist"

CURRENT_PERSIST=$(grep "^SYSLOGNG_OPTS" "$PERSIST_CONF" 2>/dev/null | grep -o 'persist-file=[^ "]*' | cut -d= -f2)

if [ "$CURRENT_PERSIST" != "$PERSIST_TMPFS" ]; then
    echo "SYSLOGNG_OPTS=\"--persist-file=$PERSIST_TMPFS\"" > "$PERSIST_CONF"
    log "Redirected syslog-ng persist file to tmpfs ($PERSIST_TMPFS)"
    SYSLOG_CHANGED=true
fi

# ─── Part 2: syslog-ng local file destinations ───
#
# Comment out log{} lines that route to local file destinations on eMMC.
# Destination definitions stay intact (avoids syslog-ng persist name collisions),
# but nothing routes to them — zero eMMC writes.
#
# Preserved (NOT commented out):
#   - Remote syslog log{} lines (using d_udapi_server_remote)
#   - Destinations writing to /var/log/ulog/ (tmpfs, not eMMC)
#     This is critical: the IDS/IPS threat alert pipeline flows through
#     /var/log/ulog/threat.log. Commenting it out breaks threat forwarding
#     to the console.

SYSLOG_CHANGED=false

if [ -d "$SYSLOG_CONF_DIR" ]; then
    # Find destinations that write to /var/log (eMMC), excluding /var/log/ulog (tmpfs)
    LOCALFILE_DESTS=$(grep -rh '^destination .* file("/var/log' "$SYSLOG_CONF_DIR"/*.conf 2>/dev/null | \
        grep -v '/var/log/ulog' | \
        sed -n 's/^destination \([^ ]*\) .*/\1/p' | sort -u)

    for conf in "$SYSLOG_CONF_DIR"/*.conf; do
        MODIFIED=false
        for dest in $LOCALFILE_DESTS; do
            if grep -q "destination($dest)" "$conf" 2>/dev/null; then
                # Comment out log{} lines that reference this local destination
                if sed -i "/destination($dest)/s/^log /#log /" "$conf" 2>/dev/null; then
                    MODIFIED=true
                fi
            fi
        done
        if [ "$MODIFIED" = true ]; then
            log "Disabled local log routes in: $(basename "$conf")"
            SYSLOG_CHANGED=true
        fi
    done

    if [ "$SYSLOG_CHANGED" = true ]; then
        systemctl restart syslog-ng
        log "syslog-ng restarted. Local eMMC log routes disabled, remote syslog and tmpfs destinations intact."
    else
        log "syslog-ng already configured."
    fi
else
    log "syslog-ng conf.d not found, skipping."
fi
