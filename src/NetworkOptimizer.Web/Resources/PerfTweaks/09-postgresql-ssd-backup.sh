#!/bin/bash
# 09-postgresql-ssd-backup.sh: Daily apps PostgreSQL backups on SSD, with
# a weekly compressed eMMC archive. See docs/postgresql-ssd-backup.md.
# Can be installed before 08: it backs up the running cluster, not PGDATA.

BACKUP_SCRIPT="/data/unifi-pg-ssd/backup.sh"
CRON_FILE="/etc/cron.d/postgresql-ssd-backup"
LOG_TAG="postgresql-ssd-backup"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
    logger -t "$LOG_TAG" "$1"
}

fail() {
    log "ERROR: $1"
    exit 1
}

[ "$#" -eq 0 ] || fail "Installer takes no arguments. Run the installed backup.sh with optional --emmc."
[ "$EUID" -eq 0 ] || fail "Run as root."
umask 077

SHORTNAME=$(ubnt-device-info model_short 2>/dev/null)
if [ -z "$SHORTNAME" ] && [ -r /proc/ubnthal/system.info ]; then
    SHORTNAME=$(grep -i '^shortname=' /proc/ubnthal/system.info | cut -d= -f2-)
fi
case "$(echo "$SHORTNAME" | tr '[:upper:]' '[:lower:]')" in
    ucg-fiber|ucgf|ucgfiber|ucg-max|ucgmax) ;;
    *) log "Not running: only UCG-Fiber and UCG-Max are supported (${SHORTNAME:-unknown})."; exit 0 ;;
esac

detect_ssd_mount() {
    if mountpoint -q /volume1; then
        SSD_MOUNT=/volume1
        return 0
    fi
    local d
    for d in /volume/*/; do
        [ -d "$d" ] || continue
        if mountpoint -q "${d%/}"; then
            SSD_MOUNT="${d%/}"
            return 0
        fi
    done
    SSD_MOUNT=$(findmnt -no TARGET /dev/md3 | head -1)
    [ -n "$SSD_MOUNT" ]
}

detect_ssd_mount || fail "No SSD mount found."
exec 8>/run/postgresql-ssd-backup-install.lock || fail "Cannot open installer lock."
flock -n 8 || fail "Another backup installer holds the lock."
mkdir -p /data/unifi-pg-ssd || fail "Cannot create backup helper directory."
HELPER_TMP=""
CRON_TMP=""
cleanup() {
    local status=$?
    trap - EXIT
    if [ -n "$HELPER_TMP" ]; then
        rm -f -- "$HELPER_TMP" || { log "ERROR: Cannot remove temporary helper."; status=1; }
    fi
    if [ -n "$CRON_TMP" ]; then
        rm -f -- "$CRON_TMP" || { log "ERROR: Cannot remove temporary cron."; status=1; }
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'fail "Installer interrupted."' INT TERM

HELPER_TMP=$(mktemp "$BACKUP_SCRIPT.tmp.XXXXXX") || fail "Cannot stage backup helper."
cat > "$HELPER_TMP" <<'SCRIPT_EOF' || fail "Cannot write backup helper."
#!/bin/bash
SSD_BACKUP_SUBDIR="unifi-pg-backup"
EMMC_BACKUP="/data/unifi-pg-backup"
LOG_TAG="postgresql-ssd-backup"
set -o pipefail
export PATH="${PATH:-/usr/bin:/bin}:/usr/sbin:/sbin"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
    logger -t "$LOG_TAG" "$1"
}

fail() {
    log "ERROR: $1"
    exit 1
}

if [ "$#" -gt 1 ] || { [ "$#" -eq 1 ] && [ "$1" != --emmc ] && [ "$1" != --check ]; }; then
    fail "Only an optional --emmc or --check argument is supported."
fi
[ "$EUID" -eq 0 ] || fail "Run as root."
umask 077
exec 9>/run/postgresql-ssd-offload.lock || fail "Cannot open database operation lock."
flock -n 9 || fail "Another PostgreSQL backup, offload or revert holds the lock."
for command in pg_dump pg_dumpall pg_restore psql runuser tar gzip sha256sum sync timeout; do
    command -v "$command" >/dev/null || fail "Required command not found: $command."
done

PG_ENV=/etc/default/postgresql/14-apps
[ -r "$PG_ENV" ] || fail "Missing apps cluster definition."
# shellcheck disable=SC1090
. "$PG_ENV" || fail "Cannot read apps cluster definition."
if [ "${PG_VERSION:-}" != 14 ] || [ "${CLUSTER_NAME:-}" != apps ] || \
   [ "${DIR:-}" != /data/postgresql/14/apps ] || [ "${CLUSTER_PORT:-}" != 5434 ]; then
    fail "Only PostgreSQL 14/apps on port 5434 is supported."
fi
pg_query() {
    timeout --kill-after=2s 15s runuser -u postgres -- psql -X -w -v ON_ERROR_STOP=1 -h /var/run/postgresql -p 5434 -d postgres -Atc "$1"
}
DATA_DIR=$(pg_query "SHOW data_directory") || fail "Cannot query apps cluster."
[ "$DATA_DIR" = "$DIR/data" ] || fail "Unexpected running data directory: $DATA_DIR."
VERSION=$(pg_query "SHOW server_version_num") || fail "Cannot query PostgreSQL version."
[[ "$VERSION" =~ ^14[0-9]{4}$ ]] || fail "Running cluster is not PostgreSQL 14."
NETWORK=$(pg_query "SELECT 1 FROM pg_database WHERE datname = 'unifi-network'") || fail "Cannot query Network database."
[ "$NETWORK" = 1 ] || fail "Apps cluster has no unifi-network database."

detect_ssd_mount() {
    if mountpoint -q /volume1; then
        SSD_MOUNT=/volume1
        return 0
    fi
    local d
    for d in /volume/*/; do
        [ -d "$d" ] || continue
        if mountpoint -q "${d%/}"; then
            SSD_MOUNT="${d%/}"
            return 0
        fi
    done
    SSD_MOUNT=$(findmnt -no TARGET /dev/md3 | head -1)
    [ -n "$SSD_MOUNT" ]
}
detect_ssd_mount || fail "No SSD mount found."
check_authority() {
    local marker=/data/unifi-pg-ssd/14-apps.ssd-active
    local source="$SSD_MOUNT/postgresql-14-apps" marker_time marker_source source_id target_id
    mountpoint -q "$SSD_MOUNT" || fail "SSD mount disappeared."
    [ ! -L "$DIR/data" ] || fail "PGDATA must not be a symlink."
    if [ -e "$marker" ] || [ -L "$marker" ]; then
        [ -f "$marker" ] && [ ! -L "$marker" ] || fail "Invalid SSD authority marker."
        read -r marker_time marker_source < "$marker" || fail "Cannot read SSD authority marker."
        [ -n "$marker_time" ] && [ "$marker_source" = "$source" ] || fail "Marker identifies an unexpected SSD source."
        [ -d "$source" ] && [ ! -L "$source" ] || fail "Invalid authoritative SSD PGDATA."
        mountpoint -q "$DIR/data" || fail "SSD is authoritative but PGDATA is not bound; refusing a stale eMMC backup."
        source_id=$(stat -c '%d:%i' "$source") || fail "Cannot identify SSD PGDATA."
        target_id=$(stat -c '%d:%i' "$DIR/data") || fail "Cannot identify running PGDATA."
        [ "$source_id" = "$target_id" ] || fail "PGDATA bind does not match SSD authority."
    else
        ! mountpoint -q "$DIR/data" || fail "Mounted PGDATA has no authority marker."
    fi
}
check_authority
SSD_BACKUP="$SSD_MOUNT/$SSD_BACKUP_SUBDIR"
[ ! -L "$SSD_BACKUP" ] && [ ! -L "$EMMC_BACKUP" ] || fail "Backup directories must not be symlinks."

valid_archive() {
    local archive=$1 file digest line checksums="" stored timestamp
    for file in unifi-network.dump apps-globals.sql; do
        line=$(tar -xOf "$archive" "./$file" | sha256sum) || return 1
        read -r digest _ <<< "$line"
        [ "$digest" != e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 ] || return 1
        checksums+="$digest  $file"$'\n'
    done
    stored=$(tar -xOf "$archive" ./SHA256SUMS) || return 1
    [ "$stored" = "${checksums%$'\n'}" ] || return 1
    timestamp=$(tar -xOf "$archive" ./completed-at) || return 1
    [[ "$timestamp" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || return 1
    # pg_restore can finish reading the TOC before tar finishes writing.
    tar -xOf "$archive" ./unifi-network.dump | {
        pg_restore --cluster 14/apps --list >/dev/null
        status=$?
        cat >/dev/null
        exit "$status"
    }
}
if [ "${1:-}" = --check ]; then
    if valid_archive "$SSD_BACKUP/unifi-pg.tar" && valid_archive "$EMMC_BACKUP/unifi-pg.tar.gz"; then
        log "Current cluster and both backup archives validated. Check their recovery points and verify an off-device restore."
        exit 0
    fi
    log "WARNING: Backup pair is missing or invalid; a new backup is required."
    exit 2
fi
mkdir -p "$SSD_BACKUP" || fail "Cannot create SSD backup directory."
chmod 0700 "$SSD_BACKUP" || fail "Cannot protect SSD backups."
STAGE=""
SSD_TMP=""
EMMC_TMP=""
cleanup() {
    local status=$?
    trap - EXIT
    if [ -n "$STAGE" ]; then
        rm -rf -- "$STAGE" || { log "ERROR: Cannot remove staging directory $STAGE."; status=1; }
    fi
    for file in "$SSD_TMP" "$EMMC_TMP"; do
        [ -n "$file" ] || continue
        rm -f -- "$file" || { log "ERROR: Cannot remove temporary archive $file."; status=1; }
    done
    exit "$status"
}
trap cleanup EXIT
trap 'fail "Backup interrupted."' INT TERM

STAGE=$(mktemp -d "$SSD_BACKUP/.backup.XXXXXX") || fail "Cannot stage backup."
log "Dumping unifi-network and apps globals to SSD."
# Put --cluster first so Debian's wrapper selects the PostgreSQL 14 client.
timeout --kill-after=10s 1800s runuser -u postgres -- pg_dump --cluster 14/apps -w -h /var/run/postgresql -p 5434 --lock-wait-timeout=30s -Fc unifi-network \
    > "$STAGE/unifi-network.dump" || fail "pg_dump failed; previous backups retained."
timeout --kill-after=10s 300s runuser -u postgres -- pg_dumpall --cluster 14/apps -w -h /var/run/postgresql -p 5434 --globals-only \
    > "$STAGE/apps-globals.sql" || fail "Globals dump failed; previous backups retained."
[ -s "$STAGE/unifi-network.dump" ] && [ -s "$STAGE/apps-globals.sql" ] || fail "Empty backup output."
pg_restore --cluster 14/apps --list "$STAGE/unifi-network.dump" >/dev/null || fail "Invalid custom-format dump."
(cd "$STAGE" && sha256sum unifi-network.dump apps-globals.sql > SHA256SUMS) || fail "Cannot checksum backup."
date -u '+%Y-%m-%dT%H:%M:%SZ' > "$STAGE/completed-at" || fail "Cannot record backup time."
check_authority

# One archive publishes the dump, globals, checksums and timestamp together.
SSD_ARCHIVE="$SSD_BACKUP/unifi-pg.tar"
SSD_TMP=$(mktemp "$SSD_ARCHIVE.tmp.XXXXXX") || fail "Cannot stage SSD archive."
tar cf "$SSD_TMP" -C "$STAGE" . || fail "SSD archive failed; previous backup retained."
sync -f "$SSD_TMP" || fail "Cannot persist SSD archive."
mv -T "$SSD_TMP" "$SSD_ARCHIVE" || fail "Cannot publish SSD archive."
SSD_TMP=""
sync -f "$SSD_BACKUP" || fail "Cannot persist SSD archive rename."
log "SSD backup published: $SSD_ARCHIVE."

if [ "${1:-}" = --emmc ]; then
    mkdir -p "$EMMC_BACKUP" || fail "Cannot create eMMC backup directory."
    chmod 0700 "$EMMC_BACKUP" || fail "Cannot protect eMMC backups."
    EMMC_ARCHIVE="$EMMC_BACKUP/unifi-pg.tar.gz"
    EMMC_TMP=$(mktemp "$EMMC_ARCHIVE.tmp.XXXXXX") || fail "Cannot stage eMMC archive."
    gzip -c "$SSD_ARCHIVE" > "$EMMC_TMP" || fail "eMMC compression failed; previous eMMC backup retained."
    sync -f "$EMMC_TMP" || fail "Cannot persist eMMC archive."
    mv -T "$EMMC_TMP" "$EMMC_ARCHIVE" || fail "Cannot publish eMMC archive."
    EMMC_TMP=""
    sync -f "$EMMC_BACKUP" || fail "Cannot persist eMMC archive rename."
    log "Compressed eMMC backup published: $EMMC_ARCHIVE."
fi
SCRIPT_EOF
# A failed preflight (status 1: target, authority or lock) still installs the
# helper and cron. Each helper run repeats the authority check and refuses a
# stale target, so backups resume by themselves once 08 restores the bind.
# Skipping the cron instead would leave a gateway with no backups after an
# OS upgrade resets /etc/cron.d, until someone re-runs this installer.
NEEDS_BACKUP=false
PREFLIGHT_FAILED=false
bash "$HELPER_TMP" --check
CHECK_STATUS=$?
case "$CHECK_STATUS" in
    0) ;;
    2) NEEDS_BACKUP=true ;;
    *) PREFLIGHT_FAILED=true ;;
esac
chmod 0700 "$HELPER_TMP" || fail "Cannot make helper executable."
sync -f "$HELPER_TMP" || fail "Cannot persist backup helper."
mv -T "$HELPER_TMP" "$BACKUP_SCRIPT" || fail "Cannot install backup helper."
HELPER_TMP=""
sync -f /data/unifi-pg-ssd || fail "Cannot persist helper rename."

CRON_TMP=$(mktemp "$CRON_FILE.tmp.XXXXXX") || fail "Cannot stage backup cron."
cat > "$CRON_TMP" <<EOF || fail "Cannot write backup cron."
# PostgreSQL backup - installed by 09-postgresql-ssd-backup.sh
30 1 * * 1-6 root $BACKUP_SCRIPT >> /tmp/postgresql-backup.log 2>&1
30 1 * * 0 root $BACKUP_SCRIPT --emmc >> /tmp/postgresql-backup.log 2>&1
EOF
chmod 0644 "$CRON_TMP" || fail "Cannot set cron permissions."
mv -T "$CRON_TMP" "$CRON_FILE" || fail "Cannot install backup cron."
CRON_TMP=""
log "Helper and cron installed."

if [ "$PREFLIGHT_FAILED" = true ]; then
    fail "Backup preflight failed (see the log above). Cron installed; scheduled runs retry the check and skip until it passes."
fi
if [ "$NEEDS_BACKUP" = true ]; then
    log "No complete SSD/eMMC backup pair found. Running initial backup."
    "$BACKUP_SCRIPT" --emmc || fail "Initial backup failed; inspect the log before any offload trial."
else
    log "Existing validated backups found; initial backup skipped. Check completed-at and verify an off-device restore."
fi
