#!/bin/bash
# 06-mongodb-ssd-offload.sh: Bind-mount MongoDB from NVMe SSD to eliminate eMMC writes
#
# MongoDB on eMMC causes cyclical packet loss from bulk delete operations
# (15,000 docs every 2-3 hours). The eMMC flash controller's garbage collection
# stalls I/O for 30+ minutes afterward, dropping packets on CPU-attached ports.
#
# This script bind-mounts MongoDB's data directory from the NVMe SSD over the
# eMMC location. On first run, it migrates the existing data. On subsequent
# boots, it sets up the bind mount directly.
#
# Requires: NVMe SSD with a /volume* mount point (UCG-Fiber, UCG-Max, or
# any model with an internal SSD). Not needed on eMMC-only models
# (UCG-Lite, etc.) - those models don't have an SSD to offload to.
#
# Falls back gracefully to eMMC if the SSD is not available.

# ─── Configuration ───
SSD_DB_SUBDIR="unifi-db"             # Subdir on the SSD for MongoDB data
EMMC_DB_DIR="/data/unifi/data/db"    # Stock MongoDB location (eMMC)
MAX_WAIT=60                          # Seconds to wait for SSD mount at boot

LOG_TAG="mongodb-ssd-offload"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
    logger -t "$LOG_TAG" "$1"
}

# ─── Model check ───
# Only UCG-Fiber and UCG-Max are supported. Non-UCG models (UDM-Pro,
# UDM-SE, UDM-Pro Max) use entirely different storage layouts and
# running this script could land a bind mount in the wrong place.
# UCG-Ultra and UCG-Lite have no NVMe SSD to offload to. Refuse to run
# on anything else.
#
# Shortnames appear in multiple forms in the Ubiquiti device registry:
#   UCG-Fiber: UCG-Fiber, UCGF, UCGFIBER
#   UCG-Max:   UCG-Max, UCGMAX
# Match against the full set, case-insensitive. Prefer ubnt-device-info
# as the canonical source, fall back to /proc/ubnthal/system.info.
SHORTNAME=$(ubnt-device-info model_short 2>/dev/null)
if [ -z "$SHORTNAME" ] && [ -r /proc/ubnthal/system.info ]; then
    SHORTNAME=$(grep -i '^shortname=' /proc/ubnthal/system.info | cut -d= -f2-)
fi
case "$(echo "$SHORTNAME" | tr '[:upper:]' '[:lower:]')" in
    ucg-fiber|ucgf|ucgfiber|ucg-max|ucgmax)
        : # supported, proceed
        ;;
    *)
        log "Not running: this script supports UCG-Fiber and UCG-Max only. Detected: ${SHORTNAME:-unknown}."
        exit 0
        ;;
esac

# Detect the SSD mount point. Firmware 5.0.x and older mount the NVMe at
# /volume1; 5.1.7+ EA mounts it at /volume/<uuid>/. On success, sets
# SSD_MOUNT and returns 0. Returns 1 if no SSD mount is found.
detect_ssd_mount() {
    if mountpoint -q /volume1 2>/dev/null; then
        SSD_MOUNT=/volume1
        return 0
    fi
    local d mp
    for d in /volume/*/; do
        [ -d "$d" ] || continue
        mp="${d%/}"
        if mountpoint -q "$mp" 2>/dev/null; then
            SSD_MOUNT="$mp"
            return 0
        fi
    done
    local t
    t=$(findmnt -no TARGET /dev/md3 2>/dev/null | head -1)
    if [ -n "$t" ]; then
        SSD_MOUNT="$t"
        return 0
    fi
    return 1
}

# Check if already bind-mounted
# mountpoint returns 0 only if the path is a mount point itself (not just a subdirectory
# of a mounted filesystem). Stock MongoDB is just a subdir of /data, not its own mount.
if mountpoint -q "$EMMC_DB_DIR" 2>/dev/null; then
    log "Already bind-mounted to SSD. Nothing to do."
    exit 0
fi

# Wait for an SSD mount to appear (may take a moment after boot)
waited=0
while ! detect_ssd_mount; do
    if [ "$waited" -ge "$MAX_WAIT" ]; then
        log "WARNING: No SSD mount (/volume1 or /volume/<uuid>) found after ${MAX_WAIT}s. Falling back to eMMC."
        exit 0
    fi
    sleep 2
    waited=$((waited + 2))
done

SSD_DB_DIR="$SSD_MOUNT/$SSD_DB_SUBDIR"
if [ "$waited" -gt 0 ]; then
    log "Waited ${waited}s for SSD to mount at $SSD_MOUNT."
else
    log "SSD mount: $SSD_MOUNT"
fi

# Stop unifi and guarantee mongod is fully exited. Sets RESTART_UNIFI=true
# if we stopped anything. Returns 0 if mongod is down, 1 if it refused.
#
# Both the first-run cp and the bind mount need mongod truly down: the cp
# would otherwise capture a torn WiredTiger snapshot, and the bind mount
# would clobber a live data directory.
#
# On modern firmware, mongod runs under its own systemd unit
# (unifi-mongodb.service) which includes a bash wrapper, the mongod
# process, and a watchdog - all in the same cgroup. That unit declares:
#   - ExecStop=/usr/bin/mongod --shutdown    (clean WiredTiger shutdown)
#   - KillMode=control-group                  (SIGTERM the whole cgroup)
#   - Restart=always                          (watchdog respawns on exit)
# unifi.service declares Requires=unifi-mongodb.service, so stopping
# unifi-mongodb also cascades to stop unifi. This is the correct and
# safe way to stop the whole stack: `systemctl stop unifi` alone does
# NOT stop mongod because they're separate units, and `pkill mongod`
# would be respawned by systemd within RestartSec.
#
# Older firmware without a separate mongodb unit falls back to stopping
# unifi and escalating to SIGTERM if mongod doesn't exit.
RESTART_UNIFI=false
stop_mongod_and_unifi() {
    # Call `systemctl stop` unconditionally rather than gating on
    # is-active. During boot, the unit is often in the "activating"
    # state (ExecStartPre=check-repair launches a mongod for repair
    # purposes, so pgrep sees mongod but is-active returns failure).
    # systemctl stop correctly handles activating/active/inactive - on
    # inactive it's a no-op, on activating it waits for the start to
    # complete then applies the stop.
    if systemctl list-unit-files unifi-mongodb.service >/dev/null 2>&1; then
        log "Stopping unifi-mongodb.service (clean mongod shutdown + cascades to unifi)..."
        systemctl stop unifi-mongodb.service
        RESTART_UNIFI=true
    else
        log "Stopping unifi.service (no unifi-mongodb unit present)..."
        systemctl stop unifi
        RESTART_UNIFI=true
        for i in $(seq 1 30); do
            pgrep -x mongod >/dev/null 2>&1 || break
            sleep 1
        done
    fi

    # Final safety: if mongod is still running for any reason (orphaned
    # from a prior run, launched outside the unit cgroup, or just slow
    # to exit), escalate to SIGTERM. systemctl stop above already
    # disabled Restart=always for this cycle, so there's no respawn
    # race.
    if pgrep -x mongod >/dev/null 2>&1; then
        log "mongod still running. Sending SIGTERM..."
        pkill -TERM -x mongod
        for i in $(seq 1 15); do
            pgrep -x mongod >/dev/null 2>&1 || break
            sleep 1
        done
    fi

    if pgrep -x mongod >/dev/null 2>&1; then
        return 1
    fi
    return 0
}

# Check if SSD copy exists and is current
if [ -f "$SSD_DB_DIR/WiredTiger" ]; then
    # Compare timestamps: if eMMC data is newer than SSD, the SSD copy is stale
    # (e.g., after a removal that copied SSD back to eMMC, then ran on eMMC for a while).
    # Re-migrate to pick up the newer eMMC data.
    EMMC_TS=$(stat -c %Y "$EMMC_DB_DIR/WiredTiger" 2>/dev/null || echo 0)
    SSD_TS=$(stat -c %Y "$SSD_DB_DIR/WiredTiger" 2>/dev/null || echo 0)
    if [ "$EMMC_TS" -gt "$SSD_TS" ]; then
        log "SSD copy is stale (eMMC is newer). Will re-migrate."
        NEEDS_MIGRATION=true
    else
        log "SSD copy found and current. Setting up bind mount."
        NEEDS_MIGRATION=false
    fi
else
    log "No SSD copy found. Will perform initial migration."
    NEEDS_MIGRATION=true
fi

# Stop mongod before touching anything. Do this once and reuse for both
# migration and bind mount, so we never run them against a live database.
if ! stop_mongod_and_unifi; then
    log "ERROR: mongod still running after SIGTERM. Aborting to avoid corruption."
    if [ "$RESTART_UNIFI" = true ]; then
        log "Restarting unifi to restore service on eMMC..."
        systemctl start unifi
    fi
    exit 1
fi

# First-run migration: copy eMMC → SSD now that mongod is guaranteed down
if [ "$NEEDS_MIGRATION" = true ]; then
    mkdir -p "$SSD_DB_DIR"
    log "Copying $EMMC_DB_DIR to $SSD_DB_DIR..."
    cp -a "$EMMC_DB_DIR"/* "$SSD_DB_DIR"/
    log "Migration complete. $(du -sh "$SSD_DB_DIR" | cut -f1) copied."
fi

# Apply bind mount
mount --bind "$SSD_DB_DIR" "$EMMC_DB_DIR"

if mountpoint -q "$EMMC_DB_DIR" 2>/dev/null; then
    log "Bind mount active: $EMMC_DB_DIR -> $SSD_DB_DIR (SSD)"
else
    log "ERROR: Bind mount failed. Controller will use eMMC."
    if [ "$RESTART_UNIFI" = true ]; then
        systemctl start unifi
    fi
    exit 1
fi

# Start unifi if we stopped it (or if it needs starting after migration)
if [ "$RESTART_UNIFI" = true ] || ! systemctl is-active --quiet unifi 2>/dev/null; then
    log "Starting unifi..."
    systemctl start unifi
    log "UniFi controller started on SSD-backed MongoDB."
fi
