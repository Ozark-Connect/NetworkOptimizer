#!/bin/bash
# 40-pppoe-reconnect.sh: Reconnect the PPPoE WANs at a fixed time of day
#
# Many ISPs end a PPPoE session after 24 hours (in Germany "Zwangstrennung", for
# example 1&1 and o2), so the disconnect, and with it a new IPv4 address and IPv6
# prefix, drifts through the day. Reconnecting at a fixed night-time hour restarts
# that 24 hour count, so the ISP's own disconnect never comes due during the day.
#
# Run without arguments (at boot or on deploy) it writes a systemd timer for the time
# in $TIME_FILE (HH:MM in the gateway's local time zone, 04:00 when missing). The
# timer calls this script with "run": it sends SIGHUP to the pppd of each PPPoE WAN,
# one after the other. pppd ends the session with an LCP Terminate-Request and, as
# UniFi OS starts it with "persist", dials again itself after its holdoff; UniFi's
# ip-up hooks then apply the WAN settings and Smart Queues as on any reconnect. A WAN
# that is down, or whose session is younger than an hour (the ISP just reconnected
# it), is skipped.
#
# Files: /data/pppoe-reconnect/{time,last-run},
#        /etc/systemd/system/pppoe-reconnect.{service,timer} (recreated at boot).

LOG_TAG="pppoe-reconnect"
SCRIPT="/data/on_boot.d/40-pppoe-reconnect.sh"
DIR="/data/pppoe-reconnect"
TIME_FILE="$DIR/time"
LAST_RUN="$DIR/last-run"
UNIT="/etc/systemd/system/pppoe-reconnect.service"
TIMER="/etc/systemd/system/pppoe-reconnect.timer"
PEERS_DIR="/etc/ppp/peers"
RUN_DIR="/var/run"
DEFAULT_TIME="04:00"
MIN_SESSION_AGE=3600      # younger sessions are not reconnected again
RECONNECT_WAIT=120        # seconds to wait for the session to come back
STAGGER=300               # seconds between two WANs, so failover keeps one up

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1" >&2
    logger -t "$LOG_TAG" "$1" 2>/dev/null
}

# The PPPoE units UniFi OS has configured (ppp0, ppp1, ...), one per line
pppoe_units() {
    local f
    for f in "$PEERS_DIR"/ppp[0-9]*; do
        [ -f "$f" ] && basename "$f"
    done
}

# Prints the pid of the running pppd of unit $1, if its session is up
session_pid() {
    local pid
    pid=$(head -n1 "$RUN_DIR/$1.pid" 2>/dev/null) || return 1
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    [ "$(cat "/proc/$pid/comm" 2>/dev/null)" = "pppd" ] || return 1
    echo "$pid"
}

# pppd writes $RUN_DIR/<unit>.pid when the session comes up and removes it when it ends
session_start() {
    stat -c %Y "$RUN_DIR/$1.pid" 2>/dev/null
}

LAST_HUP=""   # when the previous WAN was signalled, for the stagger

# Reconnects unit $1; sets RESULT to "<ok|skipped|failed> <detail>" for the last-run record
reconnect_unit() {
    local unit="$1" pid started now age i
    if ! grep -qx '[[:space:]]*persist[[:space:]]*' "$PEERS_DIR/$unit"; then
        RESULT="skipped pppd is not started with persist and would not dial again"
        return
    fi
    if ! pid=$(session_pid "$unit") || ! started=$(session_start "$unit"); then
        RESULT="skipped session is down"
        return
    fi
    now=$(date +%s)
    age=$((now - started))
    if [ "$age" -lt "$MIN_SESSION_AGE" ]; then
        RESULT="skipped session is only $((age / 60)) min old"
        return
    fi
    if [ -n "$LAST_HUP" ]; then
        # Give failover the previous WAN back before taking this one down
        i=$((LAST_HUP + STAGGER - now))
        if [ "$i" -gt 0 ]; then
            log "Waiting ${i} s before reconnecting $unit."
            sleep "$i"
            if ! pid=$(session_pid "$unit") || [ "$(session_start "$unit")" != "$started" ]; then
                RESULT="skipped session went down or restarted while waiting"
                return
            fi
            now=$(date +%s)
        fi
    fi
    log "$unit: session up for $((age / 3600)) h, sending SIGHUP to pppd $pid."
    if ! kill -HUP "$pid"; then
        RESULT="failed could not signal pppd"
        return
    fi
    LAST_HUP=$now
    for ((i = 1; i <= RECONNECT_WAIT; i++)); do
        sleep 1
        started=$(session_start "$unit") || continue
        if [ "$started" -gt "$now" ] && ip -4 addr show dev "$unit" 2>/dev/null | grep -q 'inet '; then
            RESULT="ok reconnected in ${i} s"
            return
        fi
    done
    RESULT="failed not back after ${RECONNECT_WAIT} s, pppd keeps dialing"
}

run_reconnect() {
    exec 9>/run/pppoe-reconnect.lock
    if ! flock -n 9; then
        log "Another reconnect is running."
        exit 0
    fi
    local units unit record="" failed=0
    units=$(pppoe_units)
    if [ -z "$units" ]; then
        log "No PPPoE WAN configured, nothing to do."
        record="$(date '+%Y-%m-%d %H:%M') - skipped no PPPoE WAN configured"
    fi
    for unit in $units; do
        RESULT=""
        reconnect_unit "$unit"
        log "$unit: $RESULT"
        case "$RESULT" in failed*) failed=1 ;; esac
        record="${record:+$record
}$(date '+%Y-%m-%d %H:%M') $unit $RESULT"
    done
    mkdir -p "$DIR"
    printf '%s\n' "$record" > "$LAST_RUN.tmp" && mv -f "$LAST_RUN.tmp" "$LAST_RUN"
    return "$failed"
}

if [ "$1" = "run" ]; then
    run_reconnect
    exit $?
fi

# --- Boot / deploy: write the timer for the configured time ---

mkdir -p "$DIR" || { log "Creating $DIR failed."; exit 1; }
TIME=$(tr -d '[:space:]' < "$TIME_FILE" 2>/dev/null)
if [ -z "$TIME" ]; then
    TIME="$DEFAULT_TIME"
elif ! [[ "$TIME" =~ ^([01][0-9]|2[0-3]):[0-5][0-9]$ ]]; then
    log "Invalid time '$TIME' in $TIME_FILE, using $DEFAULT_TIME."
    TIME="$DEFAULT_TIME"
fi

# Writes stdin to $1 only when the content differs, so a reboot writes nothing to the
# eMMC. Prints "changed" when it wrote.
install_if_changed() {
    local target="$1" tmp
    tmp=$(mktemp /run/pppoe-reconnect-install.XXXXXX) || return 1
    if ! cat > "$tmp"; then
        rm -f "$tmp"
        return 1
    fi
    if [ -f "$target" ] && cmp -s "$tmp" "$target"; then
        rm -f "$tmp"
        return 0
    fi
    chmod 644 "$tmp" && mv -f "$tmp" "$target" || { rm -f "$tmp"; return 1; }
    echo "changed"
}

UNIT_CHANGED=$(install_if_changed "$UNIT" <<EOF
[Unit]
Description=Scheduled PPPoE reconnect (Network Optimizer)

[Service]
Type=oneshot
ExecStart=$SCRIPT run
TimeoutStartSec=30min
EOF
) || { log "Writing $UNIT failed."; exit 1; }

# Persistent=false: a reconnect missed while the gateway was off is not made up at boot
TIMER_CHANGED=$(install_if_changed "$TIMER" <<EOF
[Unit]
Description=Scheduled PPPoE reconnect (Network Optimizer)

[Timer]
OnCalendar=*-*-* $TIME:00
AccuracySec=1s
Persistent=false
EOF
) || { log "Writing $TIMER failed."; exit 1; }

if [ -n "$UNIT_CHANGED$TIMER_CHANGED" ]; then
    systemctl daemon-reload
    systemctl restart pppoe-reconnect.timer
else
    systemctl start pppoe-reconnect.timer
fi
if ! systemctl is-active --quiet pppoe-reconnect.timer; then
    log "Starting pppoe-reconnect.timer failed."
    exit 1
fi

UNITS=$(pppoe_units | tr '\n' ' ')
log "Reconnecting daily at $TIME ($(cat /etc/timezone 2>/dev/null || date +%Z)); PPPoE WANs: ${UNITS:-none yet}"
exit 0
