#!/bin/bash
# 07-mongodb-ssd-backup.sh: MongoDB backup to SSD, with weekly compressed eMMC failover archive
#
# Companion to 06-mongodb-ssd-offload.sh. Installs a cron that runs:
#  - Daily at 1:30am: mongodump to SSD only (fast, zero eMMC impact)
#  - Weekly (Sunday 1:35am): mongodump to SSD + a compressed archive to eMMC
#
# The eMMC archive is off-SSD insurance: the daily backups live on the SSD, so
# if the SSD itself dies this is a recent copy to hand-restore from with
#   mongorestore --gzip --archive=/data/unifi/data/db-backup/unifi-db.archive.gz
# It is a mongodump archive, NOT a live database: it is not auto-loaded and is
# not the boot fallback. Zero-touch boot recovery is handled by 06, which on
# SSD loss leaves MongoDB on the stock eMMC data directory for mongod to open
# directly. Stored gzip-compressed (~12x smaller than a raw dump) to keep eMMC
# overlay usage minimal.
#
# Can also be run manually:
#   backup.sh           # SSD-only mongodump
#   backup.sh --emmc    # SSD mongodump + compressed eMMC archive
#
# Requires: 06-mongodb-ssd-offload.sh deployed first.

# ─── Configuration ───
SSD_BACKUP_SUBDIR="unifi-db-backup"   # Subdir on the SSD for backups
EMMC_BACKUP="/data/unifi/data/db-backup"
BACKUP_SCRIPT="/data/unifi-db-ssd/backup.sh"

LOG_TAG="mongodb-ssd-backup"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
}

# ─── Model check ───
# Only UCG-Fiber and UCG-Max are supported. See 06-mongodb-ssd-offload.sh
# for the rationale and for the set of accepted shortname forms.
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
# /volume1; 5.1.7+ EA mounts it at /volume/<uuid>/.
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

if ! detect_ssd_mount; then
    log "ERROR: No SSD mount (/volume1 or /volume/<uuid>) found. Aborting."
    exit 1
fi
SSD_BACKUP="$SSD_MOUNT/$SSD_BACKUP_SUBDIR"
log "SSD mount: $SSD_MOUNT"

# ─── Install the backup script to /data (persists across reboots) ───
mkdir -p "$(dirname "$BACKUP_SCRIPT")"
cat > "$BACKUP_SCRIPT" << 'SCRIPT_EOF'
#!/bin/bash
SSD_BACKUP_SUBDIR="unifi-db-backup"
EMMC_BACKUP="/data/unifi/data/db-backup"
LOG_TAG="mongodb-ssd-backup"

log() {
    echo "$(date '+%Y-%m-%d %H:%M:%S') [$LOG_TAG] $1"
}

# Detect the SSD mount point. Firmware 5.0.x and older mount the NVMe at
# /volume1; 5.1.7+ EA mounts it at /volume/<uuid>/.
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

if ! detect_ssd_mount; then
    log "ERROR: No SSD mount (/volume1 or /volume/<uuid>) found. Aborting."
    exit 1
fi
SSD_BACKUP="$SSD_MOUNT/$SSD_BACKUP_SUBDIR"

# Step 1: mongodump to SSD
mkdir -p "$SSD_BACKUP"
log "Starting mongodump to SSD..."
if ! mongodump --port 27117 --out "$SSD_BACKUP" --quiet 2>&1; then
    log "ERROR: mongodump failed"
    exit 1
fi
SSD_SIZE=$(du -sh "$SSD_BACKUP" | cut -f1)
log "mongodump complete: $SSD_SIZE on SSD"

# Step 2: optional eMMC failover archive (weekly, off-SSD insurance)
# A compressed mongodump archive (~12x smaller than a raw dump dir), restorable
# with: mongorestore --gzip --archive=<file>. This is NOT a live DB and is not
# auto-loaded; zero-touch boot recovery is handled by 06 via the stock eMMC
# data directory, not this file.
if [ "$1" = "--emmc" ]; then
    EMMC_ARCHIVE="$EMMC_BACKUP/unifi-db.archive.gz"
    mkdir -p "$EMMC_BACKUP"
    # Reclaim space from legacy uncompressed dump dirs left by older versions
    find "$EMMC_BACKUP" -mindepth 1 -maxdepth 1 ! -name "unifi-db.archive.gz" -exec rm -rf {} +
    log "Writing compressed eMMC failover archive..."
    if ! mongodump --port 27117 --gzip --archive="$EMMC_ARCHIVE.tmp" --quiet 2>&1; then
        log "ERROR: eMMC archive dump failed"
        rm -f "$EMMC_ARCHIVE.tmp"
        exit 1
    fi
    mv -f "$EMMC_ARCHIVE.tmp" "$EMMC_ARCHIVE"
    log "eMMC failover archive complete: $(du -sh "$EMMC_ARCHIVE" | cut -f1)"
fi
SCRIPT_EOF
chmod +x "$BACKUP_SCRIPT"
log "Backup script installed at $BACKUP_SCRIPT"

# ─── Install cron (overlay, re-created each boot) ───
CRON_FILE="/etc/cron.d/mongodb-ssd-backup"
cat > "$CRON_FILE" << EOF
# MongoDB SSD backup - installed by 07-mongodb-ssd-backup.sh
# Logs to /tmp (tmpfs) to avoid eMMC writes
30 1 * * * root $BACKUP_SCRIPT >> /tmp/mongodb-backup.log 2>&1
35 1 * * 0 root $BACKUP_SCRIPT --emmc >> /tmp/mongodb-backup.log 2>&1
EOF
log "Cron installed at $CRON_FILE"

# ─── Run initial backup if none exists yet ───
if [ ! -d "$SSD_BACKUP" ] || [ -z "$(ls -A "$SSD_BACKUP" 2>/dev/null)" ]; then
    log "No existing backup found. Running initial backup (SSD + eMMC failover)..."
    "$BACKUP_SCRIPT" --emmc >> /tmp/mongodb-backup.log 2>&1
    if [ $? -eq 0 ]; then
        log "Initial backup complete."
    else
        log "WARNING: Initial backup failed. Check /tmp/mongodb-backup.log"
    fi
else
    log "Existing backup found, skipping initial backup."
fi
