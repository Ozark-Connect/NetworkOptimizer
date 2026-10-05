#!/bin/bash
# 08-postgresql-ssd-offload.sh: Bind-mount the UniFi Network PostgreSQL data
# directory from the NVMe SSD to move its writes off the eMMC
#
# Late boot binding and availability-first missing-SSD fallback can expose
# stale eMMC data. See docs/postgresql-ssd-offload.md for recovery.
#
# UniFi Network 11.0.81+ migrates from MongoDB to PostgreSQL. The database
# lives in the UniFi OS "apps" PostgreSQL cluster (port 5434), defined in
# /etc/default/postgresql/14-apps, with this layout on the eMMC:
#
#   /data/postgresql/14/apps/.configured   marker read by pg-cluster-setup
#   /data/postgresql/14/apps/conf/         cluster config (symlinked from /etc/postgresql/14/apps)
#   /data/postgresql/14/apps/data/         PGDATA, incl. pg_wal (the write load)
#
# This script bind-mounts only data/. The cluster dir itself must stay on the
# eMMC: /sbin/pg-cluster-setup runs `rm -rf` on it and re-runs initdb if
# .configured or any conf file is missing, so a bind mount over the whole
# cluster dir that came up without those files would wipe the SSD copy.
#
# This is the PostgreSQL counterpart of 06-mongodb-ssd-offload.sh.
# With no SSD authority marker, failures before application writes on SSD
# recover to eMMC. An unavailable SSD leaves an unmounted eMMC copy running;
# when SSD returns, its marked copy is rebound, hiding fallback eMMC writes.
# Failures before the script stops the stack change no services: Network
# keeps running, the marker is kept, and the next run retries. Failures
# after the stop, with SSD authority, leave the stack stopped for recovery.
# An existing, verified bind exits before any database check, so a rerun
# on an offloaded gateway never stops services.
#
# Which copy is current: unlike 06, this script does NOT compare file mtimes.
# postgresql@14-apps starts on the eMMC copy at every boot, before udm-boot
# runs this script, and PostgreSQL rewrites global/pg_control on startup. So
# the eMMC copy always looks newer, and an mtime check would overwrite the
# SSD data with the eMMC snapshot on every reboot. Instead, a marker file
# ($MARKER) records that the SSD copy is authoritative:
#   - Persisted before starting PostgreSQL on the SSD copy.
#   - Removed only on checked first-migration rollback or explicit revert.
#   - Marker present + SSD copy present: bind the SSD copy, no copy.
#   - Marker absent + SSD copy present: the eMMC copy is current. Move the
#     SSD copy aside (never deleted) and re-migrate.

# ─── Configuration ───
PG_CLUSTER="14-apps"                 # <version>-<cluster>, matches /etc/default/postgresql/<name>
NETWORK_DB="unifi-network"           # Network app database; its presence gates the offload
MAX_WAIT=60                          # Seconds to wait for the SSD mount and for PostgreSQL
STATE_DIR="/data/unifi-pg-ssd"       # eMMC, outside the Ubiquiti-managed /data/postgresql tree
MARKER="$STATE_DIR/$PG_CLUSTER.ssd-active"
PG_UNIT="postgresql@$PG_CLUSTER.service"

LOG_TAG="postgresql-ssd-offload"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
    logger -t "$LOG_TAG" "$1"
}

fail() {
    log "ERROR: $1"
    exit 1
}

clear_marker() {
    rm -f -- "$MARKER" && sync -f "$STATE_DIR"
}

if [ "$#" -ne 0 ]; then
    fail "No arguments are supported (including --dry-run). Nothing changed."
fi
if [ "$EUID" -ne 0 ]; then
    fail "Run as root."
fi
if [ "${BASH_SOURCE[0]}" != "$0" ]; then
    fail "Execute this script with Bash; do not source it. Boot hooks must be executable."
fi

# ─── Model check ───
# Same rule as 06-mongodb-ssd-offload.sh: UCG-Fiber and UCG-Max only. See
# that script for the rationale and the accepted shortname forms.
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

command -v flock >/dev/null || fail "Required command not found: flock."
exec 9>/run/postgresql-ssd-offload.lock || fail "Cannot open the run lock."
flock -n 9 || fail "Another database operation holds the lock. No services changed; retry manually after it finishes. This is not a boot interlock."

SSD_AUTHORITATIVE=false
if [ -e "$MARKER" ] || [ -L "$MARKER" ]; then
    SSD_AUTHORITATIVE=true
fi
STOPPING=false
ALLOW_EMMC_RECOVERY=true
if [ "$SSD_AUTHORITATIVE" = true ]; then
    ALLOW_EMMC_RECOVERY=false
fi
RESTART_UNITS="unifi.service"
TMP_DIR=""
MARKER_TMP=""
MOUNT_ATTEMPTED=false

wait_postmaster_exit() {
    local waited=0
    while postmaster_running; do
        [ "$waited" -lt 30 ] || return 1
        sleep 1
        waited=$((waited + 1))
    done
    case "$(systemctl is-active "$PG_UNIT")" in
        inactive|failed) return 0 ;;
        *) return 1 ;;
    esac
}

stop_stack() {
    local u
    for u in $RESTART_UNITS; do
        systemctl stop "$u" || return 1
    done
    systemctl stop "$PG_UNIT" && wait_postmaster_exit
}

bind_matches() {
    local source target
    mountpoint -q "$PG_DATA" || return 1
    source=$(stat -c '%d:%i' "$SSD_PG_DIR") || return 1
    target=$(stat -c '%d:%i' "$PG_DATA") || return 1
    [ "$source" = "$target" ]
}

restart_apps() {
    local u
    for u in $RESTART_UNITS; do
        systemctl start "$u" || return 1
        systemctl is-active --quiet "$u" || return 1
    done
}

recover() {
    local status=$?
    trap - EXIT
    trap '' INT TERM
    if [ "$status" -ne 0 ]; then
        if [ "$STOPPING" = true ]; then
            log "Recovering after a failed or interrupted offload."
            if ! stop_stack; then
                log "ERROR: Cannot confirm the stack stopped. No unmount or restart attempted; manual recovery required."
                ALLOW_EMMC_RECOVERY=false
            elif [ "$ALLOW_EMMC_RECOVERY" = false ]; then
                log "ERROR: SSD authority retained; stack left stopped. Do not start stale eMMC data. Manual recovery required."
            elif [ "$MOUNT_ATTEMPTED" = true ] && mountpoint -q "$PG_DATA"; then
                if ! bind_matches || ! umount "$PG_DATA"; then
                    log "ERROR: Cannot safely unmount PGDATA. Marker retained; stack left stopped. Manual recovery required."
                    ALLOW_EMMC_RECOVERY=false
                fi
            fi
            if [ "$ALLOW_EMMC_RECOVERY" = true ]; then
                if clear_marker && systemctl start "$PG_UNIT" && wait_pg && check_pg && restart_apps; then
                    log "First-migration rollback complete. PostgreSQL and applications are back on eMMC."
                else
                    log "ERROR: eMMC recovery failed. Stopping the stack; manual recovery required."
                    stop_stack || log "ERROR: Could not confirm the recovery stack stopped."
                fi
            fi
        elif [ "$SSD_AUTHORITATIVE" = true ]; then
            log "Services left unchanged. SSD authority retained; the next run retries the bind."
        fi
    fi
    if [ -n "$TMP_DIR" ]; then
        rm -rf -- "$TMP_DIR" || log "ERROR: Could not remove temporary copy $TMP_DIR."
    fi
    if [ -n "$MARKER_TMP" ]; then
        rm -f -- "$MARKER_TMP" || log "ERROR: Could not remove temporary marker $MARKER_TMP."
    fi
    exit "$status"
}
trap recover EXIT
trap 'fail "Interrupted."' INT TERM

for command in sync mktemp mountpoint findmnt pg_isready runuser psql timeout; do
    command -v "$command" >/dev/null || fail "Required command not found: $command."
done

# ─── Cluster layout ───
# Read the cluster definition the firmware ships, instead of hardcoding
# paths, so a firmware change to the layout fails closed below.
PG_ENV="/etc/default/postgresql/$PG_CLUSTER"
if [ ! -r "$PG_ENV" ]; then
    [ "$SSD_AUTHORITATIVE" = false ] || fail "$PG_ENV missing while SSD is authoritative."
    log "Not running: $PG_ENV not found. This firmware has no $PG_CLUSTER PostgreSQL cluster."
    exit 0
fi
# shellcheck disable=SC1090
. "$PG_ENV" || fail "Cannot read $PG_ENV."

if [ "${PG_VERSION:-}" != 14 ] || [ "${CLUSTER_NAME:-}" != apps ] || \
   [ "${DIR:-}" != /data/postgresql/14/apps ] || [ "${CLUSTER_PORT:-}" != 5434 ]; then
    fail "Unexpected $PG_ENV layout. Only PostgreSQL 14/apps at port 5434 is supported."
fi
PG_CLUSTER_DIR="$DIR"
PG_DATA="$DIR/data"
PG_SOCKET_DIR="/var/run/postgresql"

# ─── Detect the SSD mount ───
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

read_marker_source() {
    [ -f "$MARKER" ] && [ ! -L "$MARKER" ] || return 1
    read -r MARKER_TIME MARKER_SOURCE < "$MARKER" || return 1
    [ -n "$MARKER_TIME" ] && [ -n "$MARKER_SOURCE" ]
}

# ─── Already offloaded ───
# Verify the bind and the marker, then exit before the database gate.
# Network is running on the mounted copy, so a failure here only logs:
# SSD_AUTHORITATIVE stays as the marker set it and no service is stopped.
if mountpoint -q "$PG_DATA"; then
    ALLOW_EMMC_RECOVERY=false
    detect_ssd_mount || fail "PGDATA is mounted, but no SSD mount was found."
    SSD_PG_DIR="$SSD_MOUNT/postgresql-$PG_CLUSTER"
    read_marker_source || fail "PGDATA is mounted, but the authority marker is missing or invalid."
    [ "$MARKER_SOURCE" = "$SSD_PG_DIR" ] || fail "Marker identifies '$MARKER_SOURCE', expected $SSD_PG_DIR."
    bind_matches || fail "PGDATA is mounted from an unexpected source."
    log "Verified SSD bind mount and authority marker. Nothing to do."
    exit 0
fi

if [ ! -e "$PG_CLUSTER_DIR/.configured" ] || [ ! -f "$PG_DATA/PG_VERSION" ] || [ -L "$PG_DATA" ]; then
    [ "$SSD_AUTHORITATIVE" = false ] || fail "Authoritative cluster is not initialized at $PG_CLUSTER_DIR."
    log "Not running: cluster $PG_CLUSTER is not initialized at $PG_CLUSTER_DIR. Nothing to offload."
    exit 0
fi
[ "$(cat "$PG_DATA/PG_VERSION")" = "$PG_VERSION" ] || fail "PGDATA version does not match $PG_VERSION."

# The config must point PostgreSQL at the PGDATA we are about to bind-mount.
CONF_DATA_DIR=$(sed -n "s/^[[:space:]]*data_directory[[:space:]]*=[[:space:]]*'\([^']*\)'.*/\1/p" \
    "$PG_CLUSTER_DIR/conf/postgresql.conf" 2>/dev/null | tail -1)
if [ "$CONF_DATA_DIR" != "$PG_DATA" ]; then
    fail "postgresql.conf data_directory is '${CONF_DATA_DIR:-unset}', expected '$PG_DATA'."
fi

pg_ready() {
    timeout --kill-after=2s 2s pg_isready -t 1 -q -h "$PG_SOCKET_DIR" -p "$CLUSTER_PORT" >/dev/null 2>&1
}

pg_query() {
    timeout --kill-after=2s 15s runuser -u postgres -- psql -X -w -v ON_ERROR_STOP=1 -h "$PG_SOCKET_DIR" -p "$CLUSTER_PORT" -d postgres -Atc "$1"
}

wait_pg() {
    local waited=0
    local deadline=$((SECONDS + MAX_WAIT))
    # The socket can accept connections before the forking unit becomes active.
    until pg_ready && systemctl is-active --quiet "$PG_UNIT"; do
        [ "$waited" -lt "$MAX_WAIT" ] && [ "$SECONDS" -lt "$deadline" ] || return 1
        sleep 2
        waited=$((waited + 2))
    done
}

check_pg() {
    local data
    systemctl is-active --quiet "$PG_UNIT" || return 1
    data=$(pg_query "SHOW data_directory") || return 1
    [ "$data" = "$PG_DATA" ]
}

# A postmaster for this PGDATA is running if postmaster.pid exists and its
# first line is a live PID.
postmaster_running() {
    local pid
    [ -f "$PG_DATA/postmaster.pid" ] || return 1
    pid=$(head -1 "$PG_DATA/postmaster.pid") || return 0
    [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 0
    kill -0 "$pid" 2>/dev/null
}

# ─── Gate: is the Network app on PostgreSQL, and is the cluster ours alone? ───
# The database list needs a running cluster. During manual recovery it may
# still be starting, so wait for it.
wait_pg || fail "$PG_UNIT not active and accepting connections after ${MAX_WAIT}s. PostgreSQL stays on eMMC for now."
check_pg || fail "Running cluster does not match $PG_DATA."

if ! DATABASES=$(pg_query "SELECT datname FROM pg_database WHERE datname NOT IN ('postgres','template0','template1') ORDER BY 1"); then
    fail "Could not list databases in cluster $PG_CLUSTER."
fi
if ! echo "$DATABASES" | grep -qx "$NETWORK_DB"; then
    [ "$SSD_AUTHORITATIVE" = false ] || fail "Authoritative cluster no longer contains '$NETWORK_DB'."
    log "Not running: no '$NETWORK_DB' database in cluster $PG_CLUSTER. The Network app is not on PostgreSQL yet."
    exit 0
fi
# The apps cluster is named for sharing. Stopping it interrupts every app
# that uses it, and this script only knows how to restart the Network app's
# stack. Refuse if any other database is present.
OTHER_DBS=$(echo "$DATABASES" | grep -vx "$NETWORK_DB" | tr '\n' ' ')
if [ -n "${OTHER_DBS// /}" ]; then
    fail "Cluster $PG_CLUSTER also holds other databases (${OTHER_DBS% }). Shared clusters are not supported."
fi

# ─── Wait for the SSD mount ───
waited=0
while ! detect_ssd_mount; do
    if [ "$waited" -ge "$MAX_WAIT" ]; then
        if [ "$SSD_AUTHORITATIVE" = true ]; then
            log "WARNING: SSD unavailable after ${MAX_WAIT}s. Leaving Network on potentially stale eMMC data; newer SSD settings/history are unavailable. SSD authority retained: when SSD returns, rebinding will hide writes made on eMMC during fallback. This is availability fallback, not lossless recovery."
        else
            log "WARNING: No SSD mount found after ${MAX_WAIT}s. Initial migration skipped; PostgreSQL remains on eMMC."
        fi
        exit 0
    fi
    sleep 2
    waited=$((waited + 2))
done

SSD_PG_DIR="$SSD_MOUNT/postgresql-$PG_CLUSTER"
log "SSD mount: $SSD_MOUNT"

[ ! -L "$SSD_PG_DIR" ] || fail "SSD PGDATA must not be a symlink."
if [ -e "$SSD_PG_DIR" ] && [ ! -d "$SSD_PG_DIR" ]; then
    fail "$SSD_PG_DIR exists but is not a directory."
fi
if [ "$SSD_AUTHORITATIVE" = true ]; then
    read_marker_source || fail "Missing or invalid SSD authority marker."
    [ "$MARKER_SOURCE" = "$SSD_PG_DIR" ] || fail "Marker does not identify $SSD_PG_DIR."
fi
SSD_DEVICE=$(stat -c %d "$SSD_MOUNT") || fail "Cannot identify SSD filesystem."
EMMC_DEVICE=$(stat -c %d "$PG_DATA") || fail "Cannot identify eMMC filesystem."
[ "$SSD_DEVICE" != "$EMMC_DEVICE" ] || fail "SSD and eMMC are on the same filesystem."

# ─── Which copy is current ───
# See the header: decided by the marker, not by mtimes.
if [ -f "$SSD_PG_DIR/global/pg_control" ]; then
    if [ "$SSD_AUTHORITATIVE" = true ]; then
        log "SSD copy is authoritative. Setting up bind mount."
        NEEDS_MIGRATION=false
    else
        log "SSD copy found without a marker. Will preserve it and migrate the current eMMC copy."
        NEEDS_MIGRATION=true
    fi
else
    [ "$SSD_AUTHORITATIVE" = false ] || fail "Authoritative SSD copy has no global/pg_control."
    log "No SSD copy found. Will perform initial migration."
    NEEDS_MIGRATION=true
fi

COPY_SOURCE="$PG_DATA"
if [ "$NEEDS_MIGRATION" = false ]; then
    COPY_SOURCE="$SSD_PG_DIR"
fi
[ "$(cat "$COPY_SOURCE/PG_VERSION")" = "$PG_VERSION" ] || fail "Selected copy is not PostgreSQL $PG_VERSION."
LINKS=$(find "$COPY_SOURCE" -type l -print -quit) || fail "Cannot inspect selected PGDATA."
[ -z "$LINKS" ] || fail "PGDATA symlinks (including external WAL/tablespaces) are unsupported: $LINKS."
if [ "$NEEDS_MIGRATION" = true ]; then
    SIZE=$(du -sk "$PG_DATA") || fail "Cannot measure PGDATA."
    read -r REQUIRED_KB _ <<< "$SIZE"
    SPACE=$(df -Pk "$SSD_MOUNT") || fail "Cannot measure SSD free space."
    AVAILABLE_KB=$(echo "$SPACE" | awk 'NR==2 {print $4}')
    [[ "$REQUIRED_KB" =~ ^[0-9]+$ && "$AVAILABLE_KB" =~ ^[0-9]+$ ]] || fail "Invalid disk-space measurements."
    [ "$AVAILABLE_KB" -ge "$REQUIRED_KB" ] || fail "Insufficient SSD space for a new PGDATA copy."
fi
mkdir -p "$STATE_DIR" || fail "Cannot create $STATE_DIR."

# ─── Stop the stack ───
# Record the running units that depend on the cluster, so the same set
# comes back up afterward. Stopping $PG_UNIT propagates the stop to every
# unit that Requires= it, so the list must be taken first.
#
# unifi.service is always restarted, because it is always stopped below. At
# boot it is often still "activating" (Type=notify), which is-active --quiet
# reports as not running. For the same reason, "activating" counts as
# running for the other dependents.
DEPENDENCY_LIST=$(systemctl list-dependencies --reverse --plain --no-legend "$PG_UNIT") || fail "Cannot list cluster dependents."
DEPENDENTS=$(echo "$DEPENDENCY_LIST" | sed 's/^[[:space:]]*//' | grep '\.service$' \
    | grep -v '^postgresql' | grep -vx 'unifi.service' | sort -u)
RESTART_UNITS="unifi.service"
for u in $DEPENDENTS; do
    case "$(systemctl is-active "$u" 2>/dev/null)" in
        active|activating|reloading) RESTART_UNITS="$RESTART_UNITS $u" ;;
        inactive|failed) ;;
        *) fail "Cannot establish the state of dependent $u." ;;
    esac
done

# Stop the Network app first so it closes its connections, then stop the
# cluster. The postgresql@ unit stops with `pg_ctlcluster -m fast`, which
# is a clean shutdown (shutdown checkpoint, no crash recovery on start).
log "Stopping unifi.service and $PG_UNIT (restart list: ${RESTART_UNITS:-none})..."
STOPPING=true
stop_stack || fail "Cannot confirm the stack stopped. No data movement attempted."

# ─── Migration ───
# Copy to a temp dir and rename on success, so a partial copy can never be
# mistaken for a valid SSD copy. An existing SSD copy is moved aside rather
# than copied over (copying over it would leave old relation and WAL files
# behind) or deleted (it may hold the only copy of recent changes if the
# marker logic guessed wrong).
if [ "$NEEDS_MIGRATION" = true ]; then
    TMP_DIR=$(mktemp -d "$SSD_PG_DIR.tmp.XXXXXX") || fail "Cannot create temporary SSD copy."
    log "Copying $PG_DATA to $SSD_PG_DIR..."
    cp -a "$PG_DATA"/. "$TMP_DIR"/ || fail "Copy to SSD failed."
    sync -f "$TMP_DIR" || fail "Cannot persist SSD copy."
    if [ -d "$SSD_PG_DIR" ]; then
        STALE_DIR="$SSD_PG_DIR.stale-$(date +%Y%m%d%H%M%S)-$$"
        [ ! -e "$STALE_DIR" ] && [ ! -L "$STALE_DIR" ] || fail "Archive destination already exists: $STALE_DIR."
        mv -T "$SSD_PG_DIR" "$STALE_DIR" || fail "Cannot archive previous SSD copy."
        log "Moved previous SSD copy aside to $STALE_DIR."
    fi
    mv -T "$TMP_DIR" "$SSD_PG_DIR" || fail "Cannot promote temporary SSD copy."
    TMP_DIR=""
    log "Migration complete. $(du -sh "$SSD_PG_DIR" | cut -f1) copied."
fi

# PostgreSQL refuses to start unless PGDATA is owned by the server user
# with mode 0700 or 0750. A bind mount shows the source dir's own owner and
# mode, so set them on the SSD dir before binding.
chown postgres:postgres "$SSD_PG_DIR" || fail "Cannot set SSD PGDATA owner."
chmod 0700 "$SSD_PG_DIR" || fail "Cannot set SSD PGDATA mode."
sync -f "$SSD_PG_DIR" || fail "Cannot persist SSD directory."

# ─── Bind mount ───
MOUNT_ATTEMPTED=true
mount --bind "$SSD_PG_DIR" "$PG_DATA" || fail "Bind mount failed."
bind_matches || fail "Bind mount source verification failed."
log "Bind mount active: $PG_DATA -> $SSD_PG_DIR (SSD)"

# Publish authority before PostgreSQL or applications can write on SSD.
if [ "$SSD_AUTHORITATIVE" = false ]; then
    MARKER_TMP=$(mktemp "$STATE_DIR/$PG_CLUSTER.ssd-active.tmp.XXXXXX") || fail "Cannot create temporary authority marker."
    printf '%s %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$SSD_PG_DIR" > "$MARKER_TMP" || fail "Cannot write authority marker."
    sync -f "$MARKER_TMP" || fail "Cannot persist temporary authority marker."
    mv -T "$MARKER_TMP" "$MARKER" || fail "Cannot publish authority marker."
    MARKER_TMP=""
    sync -f "$STATE_DIR" || fail "Cannot persist authority marker rename."
fi

# ─── Start on the SSD ───
systemctl start "$PG_UNIT" || fail "PostgreSQL start on SSD failed."
if ! wait_pg || ! check_pg; then
    fail "PostgreSQL on SSD failed readiness or identity checks."
fi
ALLOW_EMMC_RECOVERY=false
# PostgreSQL is verified on the SSD. From here, a failed application start
# must not stop it again: recovery would only add downtime.
STOPPING=false
restart_apps || fail "Application restart failed. PostgreSQL stays on the SSD; start ${RESTART_UNITS} manually."
log "PostgreSQL cluster $PG_CLUSTER started on SSD. Restarted: ${RESTART_UNITS:-none}."
