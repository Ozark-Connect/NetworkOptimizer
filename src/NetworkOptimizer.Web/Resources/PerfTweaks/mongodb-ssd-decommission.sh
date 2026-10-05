#!/bin/bash
# mongodb-ssd-decommission.sh: Undo 06-mongodb-ssd-offload.sh + 07-mongodb-ssd-backup.sh
# and put the newest MongoDB copy back on the eMMC
#
# Not a boot hook. Run it on demand, as root, by executing it with Bash.
#
# Modes (exactly one is required):
#   --decommission  The Network app runs on PostgreSQL (Network 11.0.81+) and no
#                   longer uses MongoDB. Refuses unless the running app is in
#                   PostgreSQL mode. After a successful copy-back and restart,
#                   deletes the SSD copy, the 07 backups and the old eMMC copy.
#   --remove        Remove the MongoDB SSD tweak on a gateway that may still run
#                   MongoDB. Keeps the SSD copy and the 07 backups (the user may
#                   deploy again). Deletes only the old eMMC copy it replaced.
#
# Steps (every step is checked):
#   1. Lock, and exit early when no 06/07 artifact is left.
#   2. --decommission only: confirm the PostgreSQL backend.
#   3. Stop unifi.service, then unifi-mongodb.service. Abort if mongod stays up.
#      Stopping unifi first also blocks the Network 11 on-demand mongod start.
#   4. Unmount the /data/unifi/data/db bind mount.
#   5. Pick the newest copy by WiredTiger.turtle mtime. If the SSD copy is newer,
#      stage it on the eMMC, check it, then swap it in.
#   6. Move 06, 07 and the 07 cron to /data/on_boot.d.disabled/, remove the
#      07 helper.
#   7. Start unifi.service (and check the backend again for --decommission).
#   8. Clean up (see the modes above).
#
# A failure in steps 3-6 restarts unifi.service and deletes nothing. It first
# restores the bind mount if the SSD copy is the newest and not yet on the eMMC.
#
# The last line of output is machine-readable, for Network Optimizer:
#   RESULT=ok mode=<mode> copied=<ssd|none> bytes=<n> unclean=<0|1> cleanup=<done|incomplete|skipped> noop=<0|1>
#   RESULT=error mode=<mode> step=<n> reason="<text>"
# Exit status is 0 for RESULT=ok and 1 for RESULT=error.

# ─── Configuration ───
EMMC_DB_DIR="/data/unifi/data/db"            # Stock MongoDB location (eMMC)
EMMC_BACKUP_DIR="/data/unifi/data/db-backup" # 07 weekly eMMC archive
SSD_DB_SUBDIR="unifi-db"                     # 06 SSD copy
SSD_BACKUP_SUBDIR="unifi-db-backup"          # 07 daily SSD dump
HELPER_DIR="/data/unifi-db-ssd"              # 07 generated backup.sh
ON_BOOT_DIR="/data/on_boot.d"
DISABLED_DIR="/data/on_boot.d.disabled"
CRON_FILE="/etc/cron.d/mongodb-ssd-backup"
HOOKS="06-mongodb-ssd-offload.sh 07-mongodb-ssd-backup.sh"
STOP_WAIT=30                                 # Seconds to wait for mongod to exit
START_TIMEOUT=600                            # Seconds to wait for unifi.service to start

LOG_TAG="mongodb-ssd-decommission"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
    logger -t "$LOG_TAG" "$1"
}

MODE=""
STEP=0
RESULT_PRINTED=false

# Print the error result line once and exit. The EXIT trap does the recovery.
fail() {
    log "ERROR: $1"
    if [ "$RESULT_PRINTED" = false ]; then
        RESULT_PRINTED=true
        printf 'RESULT=error mode=%s step=%s reason="%s"\n' "${MODE:-none}" "$STEP" "${1//\"/\'}"
    fi
    exit 1
}

case "$*" in
    --decommission) MODE=decommission ;;
    --remove) MODE=remove ;;
    *) fail "Usage: $0 --decommission | --remove" ;;
esac
[ "$EUID" -eq 0 ] || fail "Run as root."
[ "${BASH_SOURCE[0]}" = "$0" ] || fail "Execute this script with Bash; do not source it."

for command in flock mountpoint findmnt pgrep systemctl du df stat cp mv mktemp timeout sync; do
    command -v "$command" >/dev/null || fail "Required command not found: $command."
done

# ─── Model check ───
# Same rule as 06-mongodb-ssd-offload.sh: UCG-Fiber and UCG-Max only.
SHORTNAME=$(ubnt-device-info model_short 2>/dev/null)
if [ -z "$SHORTNAME" ] && [ -r /proc/ubnthal/system.info ]; then
    SHORTNAME=$(grep -i '^shortname=' /proc/ubnthal/system.info | cut -d= -f2-)
fi
case "$(echo "$SHORTNAME" | tr '[:upper:]' '[:lower:]')" in
    ucg-fiber|ucgf|ucgfiber|ucg-max|ucgmax) ;;
    *) fail "This script supports UCG-Fiber and UCG-Max only. Detected: ${SHORTNAME:-unknown}." ;;
esac

# ─── Step 1: lock and artifact check ───
STEP=1
exec 9>/run/mongodb-ssd-decommission.lock || fail "Cannot open the run lock."
flock -n 9 || fail "Another decommission or remove is running."
log "Start ($MODE). $(date -u '+%Y-%m-%dT%H:%M:%SZ')"

# Same detection as 06-mongodb-ssd-offload.sh.
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

SSD_MOUNT=""
SSD_DB_DIR=""
SSD_BACKUP=""
if detect_ssd_mount; then
    SSD_DB_DIR="$SSD_MOUNT/$SSD_DB_SUBDIR"
    SSD_BACKUP="$SSD_MOUNT/$SSD_BACKUP_SUBDIR"
fi

BOUND=false
mountpoint -q "$EMMC_DB_DIR" 2>/dev/null && BOUND=true

ARTIFACTS=""
for h in $HOOKS; do
    [ -e "$ON_BOOT_DIR/$h" ] && ARTIFACTS="$ARTIFACTS $h"
done
[ -e "$CRON_FILE" ] && ARTIFACTS="$ARTIFACTS cron"
[ -e "$HELPER_DIR" ] && ARTIFACTS="$ARTIFACTS helper"
[ "$BOUND" = true ] && ARTIFACTS="$ARTIFACTS bind"
[ -n "$SSD_DB_DIR" ] && [ -d "$SSD_DB_DIR" ] && ARTIFACTS="$ARTIFACTS ssd-copy"
if [ "$MODE" = decommission ]; then
    [ -n "$SSD_BACKUP" ] && [ -d "$SSD_BACKUP" ] && ARTIFACTS="$ARTIFACTS ssd-backup"
    [ -d "$EMMC_BACKUP_DIR" ] && ARTIFACTS="$ARTIFACTS emmc-backup"
fi
if [ -z "$ARTIFACTS" ]; then
    log "No MongoDB SSD artifacts found. Nothing to do."
    RESULT_PRINTED=true
    echo "RESULT=ok mode=$MODE copied=none bytes=0 unclean=0 cleanup=skipped noop=1"
    exit 0
fi
log "Artifacts:$ARTIFACTS"

if [ "$BOUND" = true ]; then
    [ -n "$SSD_DB_DIR" ] && [ -d "$SSD_DB_DIR" ] || fail "$EMMC_DB_DIR is bind-mounted, but no SSD copy was found."
    [ "$(stat -c '%d:%i' "$EMMC_DB_DIR")" = "$(stat -c '%d:%i' "$SSD_DB_DIR")" ] \
        || fail "$EMMC_DB_DIR is mounted from an unexpected source."
fi

# ─── Step 2: backend check (--decommission only) ───
# The running Network app must be in PostgreSQL mode with a populated
# database, so a stale caller decision cannot decommission a MongoDB gateway.
db_mode() {
    local pid
    pid=$(pgrep -o -x unifi) || return 1
    tr '\0' '\n' < "/proc/$pid/cmdline" | sed -n 's/^-Dunifi\.active\.db\.mode=//p' | head -1
}

check_postgresql_backend() {
    local mode tables
    mode=$(db_mode) || return 1
    [ "$mode" = postgresql ] || return 1
    tables=$(timeout 10 runuser -u postgres -- psql -X -w -h /var/run/postgresql -p 5434 -d unifi-network \
        -Atc "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema')" 2>/dev/null) \
        || return 1
    [[ "$tables" =~ ^[0-9]+$ ]] && [ "$tables" -gt 0 ]
}

STEP=2
if [ "$MODE" = decommission ]; then
    command -v runuser >/dev/null && command -v psql >/dev/null || fail "Required command not found: runuser or psql."
    systemctl is-active --quiet unifi.service || fail "unifi.service is not active; cannot confirm the database backend."
    check_postgresql_backend || fail "The Network app is not running on PostgreSQL. Use --remove on a MongoDB gateway."
    log "Confirmed: Network app runs on PostgreSQL."
fi

# ─── Recovery ───
# Before step 6: put the bind back if it was removed and no copy was swapped
# in, then restart unifi.service. Nothing is deleted on any failure path.
UNMOUNTED=false
SWAPPED=false
SSD_NEWER=false
STOPPED=false
STAGE=""
recover() {
    local status=$?
    trap - EXIT
    trap '' INT TERM
    if [ -n "$STAGE" ] && [ -d "$STAGE" ]; then
        rm -rf -- "$STAGE" || log "ERROR: Could not remove staging dir $STAGE."
    fi
    if [ "$status" -ne 0 ] && [ "$STOPPED" = true ] && [ "$STEP" -lt 7 ]; then
        # Restore the bind only while the SSD copy is the newest one that is
        # not yet on the eMMC: before the step 5 decision, or after it chose
        # the SSD copy but did not swap it in. When the eMMC copy was current
        # or already swapped in, start on the eMMC copy instead.
        if [ "$UNMOUNTED" = true ] && [ "$SWAPPED" = false ] && \
           { [ "$STEP" -lt 5 ] || [ "$SSD_NEWER" = true ]; }; then
            if mount --bind "$SSD_DB_DIR" "$EMMC_DB_DIR"; then
                log "Recovery: bind mount restored."
            else
                log "ERROR: Recovery could not restore the bind mount. Not starting unifi.service; manual recovery required."
                exit "$status"
            fi
        fi
        if timeout "$START_TIMEOUT" systemctl start unifi.service; then
            log "Recovery: unifi.service started."
        else
            log "ERROR: Recovery could not start unifi.service."
        fi
    fi
    exit "$status"
}
trap recover EXIT
trap 'fail "Interrupted."' INT TERM

# ─── Step 3: stop the stack ───
STEP=3
log "Stopping unifi.service and unifi-mongodb.service..."
STOPPED=true
systemctl stop unifi.service || fail "Cannot stop unifi.service."
systemctl stop unifi-mongodb.service 2>/dev/null
waited=0
while pgrep -x mongod >/dev/null 2>&1; do
    [ "$waited" -lt "$STOP_WAIT" ] || fail "mongod still running after ${STOP_WAIT}s. Not touching MongoDB data."
    sleep 1
    waited=$((waited + 1))
done
case "$(systemctl is-active unifi-mongodb.service 2>/dev/null)" in
    inactive|failed|unknown|"") ;;
    *) fail "unifi-mongodb.service did not stop." ;;
esac

# ─── Step 4: unmount the bind ───
STEP=4
if [ "$BOUND" = true ]; then
    umount "$EMMC_DB_DIR" || fail "umount $EMMC_DB_DIR failed (busy?)."
    UNMOUNTED=true
    log "Bind mount removed: $EMMC_DB_DIR now shows the eMMC copy."
fi

# ─── Step 5: put the newest copy on the eMMC ───
# WiredTiger.turtle is rewritten on every checkpoint and on clean shutdown,
# so its mtime marks the newest state (the same check 06 uses).
STEP=5
turtle_ts() { stat -c %Y "$1/WiredTiger.turtle" 2>/dev/null || echo 0; }
COPIED=none
BYTES=0
UNCLEAN=0
if [ -n "$SSD_DB_DIR" ] && [ -f "$SSD_DB_DIR/WiredTiger.turtle" ]; then
    SSD_TS=$(turtle_ts "$SSD_DB_DIR")
    EMMC_TS=$(turtle_ts "$EMMC_DB_DIR")
    log "WiredTiger.turtle: SSD $(date -u -d "@$SSD_TS" '+%Y-%m-%dT%H:%M:%SZ'), eMMC $(date -u -d "@$EMMC_TS" '+%Y-%m-%dT%H:%M:%SZ')."
    if [ "$SSD_TS" -gt "$EMMC_TS" ]; then
        SSD_NEWER=true
        # A non-empty mongod.lock means mongod did not shut down cleanly.
        # The newest copy is still kept: WiredTiger recovers from its journal,
        # and the older copy would lose every later write.
        if [ -s "$SSD_DB_DIR/mongod.lock" ]; then
            UNCLEAN=1
            log "WARNING: the SSD copy was not shut down cleanly. mongod will run journal recovery on next start."
        fi
        BYTES=$(du -sb "$SSD_DB_DIR" | cut -f1) || fail "Cannot measure the SSD copy."
        AVAIL_KB=$(df -Pk "$(dirname "$EMMC_DB_DIR")" | awk 'NR==2 {print $4}') || fail "Cannot measure eMMC free space."
        [[ "$BYTES" =~ ^[0-9]+$ && "$AVAIL_KB" =~ ^[0-9]+$ ]] || fail "Invalid size measurements."
        NEED_KB=$(( BYTES / 1024 * 11 / 10 ))
        [ "$AVAIL_KB" -ge "$NEED_KB" ] || fail "Not enough eMMC space: need ${NEED_KB} KB, have ${AVAIL_KB} KB."

        STAGE=$(mktemp -d "$EMMC_DB_DIR.decom.XXXXXX") || fail "Cannot create a staging dir."
        log "Copying $SSD_DB_DIR to the eMMC ($(( BYTES / 1048576 )) MiB)..."
        cp -a "$SSD_DB_DIR"/. "$STAGE"/ || fail "Copy to eMMC failed."
        sync -f "$STAGE" || fail "Cannot flush the staged copy."
        [ -f "$STAGE/WiredTiger.turtle" ] || fail "Staged copy has no WiredTiger.turtle."
        STAGED=$(du -sb "$STAGE" | cut -f1) || fail "Cannot measure the staged copy."
        DIFF=$(( STAGED > BYTES ? STAGED - BYTES : BYTES - STAGED ))
        [ "$DIFF" -le $(( BYTES / 100 )) ] || fail "Staged copy size ($STAGED) differs from the SSD copy ($BYTES) by more than 1%."
        chown "$(stat -c '%U:%G' "$SSD_DB_DIR")" "$STAGE" || fail "Cannot set staged dir owner."
        chmod "$(stat -c '%a' "$SSD_DB_DIR")" "$STAGE" || fail "Cannot set staged dir mode."

        OLD_DIR="$EMMC_DB_DIR.pre-decom-$(date +%Y%m%d%H%M%S)"
        if [ -e "$EMMC_DB_DIR" ]; then
            mv -T "$EMMC_DB_DIR" "$OLD_DIR" || fail "Cannot move the old eMMC copy aside."
        fi
        if ! mv -T "$STAGE" "$EMMC_DB_DIR"; then
            mv -T "$OLD_DIR" "$EMMC_DB_DIR" || log "ERROR: Could not move the old eMMC copy back."
            fail "Cannot move the staged copy into place."
        fi
        STAGE=""
        SWAPPED=true
        sync -f "$EMMC_DB_DIR" || fail "Cannot flush the new eMMC copy."
        COPIED=ssd
        log "SSD copy is now on the eMMC. Old eMMC copy kept at $OLD_DIR until cleanup."
    else
        log "eMMC copy is current. No copy needed."
    fi
else
    log "No SSD copy with WiredTiger.turtle. eMMC copy left as it is."
fi

# ─── Step 6: retire the hooks, cron and helper ───
STEP=6
ARCHIVE="$DISABLED_DIR/mongodb-$(date +%Y%m%d%H%M%S)"
for h in $HOOKS; do
    if [ -e "$ON_BOOT_DIR/$h" ]; then
        mkdir -p "$ARCHIVE" || fail "Cannot create $ARCHIVE."
        mv "$ON_BOOT_DIR/$h" "$ARCHIVE/" || fail "Cannot move $h out of $ON_BOOT_DIR."
    fi
done
if [ -e "$CRON_FILE" ]; then
    mkdir -p "$ARCHIVE" || fail "Cannot create $ARCHIVE."
    mv "$CRON_FILE" "$ARCHIVE/" || fail "Cannot move $CRON_FILE."
fi
rm -rf -- "$HELPER_DIR" || fail "Cannot remove $HELPER_DIR."
[ -d "$ARCHIVE" ] && log "Hooks and cron archived in $ARCHIVE."

# ─── Step 7: start the Network app ───
STEP=7
log "Starting unifi.service..."
timeout "$START_TIMEOUT" systemctl start unifi.service || fail "unifi.service did not start within ${START_TIMEOUT}s."
systemctl is-active --quiet unifi.service || fail "unifi.service is not active after start."
if [ "$MODE" = decommission ]; then
    check_postgresql_backend || fail "unifi.service started, but the PostgreSQL backend check failed. Nothing deleted."
fi
log "unifi.service is active."

# ─── Step 8: cleanup ───
STEP=8
CLEANUP=done
remove_path() {
    [ -n "$1" ] && [ -e "$1" ] || return 0
    if rm -rf -- "$1"; then
        log "Removed $1."
    else
        log "WARNING: could not remove $1."
        CLEANUP=incomplete
    fi
}
[ -n "${OLD_DIR:-}" ] && remove_path "$OLD_DIR"
if [ "$MODE" = decommission ]; then
    remove_path "$SSD_DB_DIR"
    remove_path "$SSD_BACKUP"
    remove_path "$EMMC_BACKUP_DIR"
fi

log "Done ($MODE). $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
RESULT_PRINTED=true
echo "RESULT=ok mode=$MODE copied=$COPIED bytes=$BYTES unclean=$UNCLEAN cleanup=$CLEANUP noop=0"
exit 0
