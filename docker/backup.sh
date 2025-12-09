#!/bin/bash

# Network Optimizer - Backup Script

set -e

BACKUP_DIR=${BACKUP_DIR:-./backups}
DATE=$(date +%Y%m%d-%H%M%S)
BACKUP_NAME="network-optimizer-backup-$DATE"

echo "=========================================="
echo "Network Optimizer - Backup"
echo "=========================================="
echo ""
echo "Backup location: $BACKUP_DIR/$BACKUP_NAME"
echo ""

# Create backup directory
mkdir -p "$BACKUP_DIR"

# Check which docker compose command to use
if docker compose version &> /dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
else
    COMPOSE_CMD="docker-compose"
fi

# Create backup directory
BACKUP_PATH="$BACKUP_DIR/$BACKUP_NAME"
mkdir -p "$BACKUP_PATH"

# Backup local data
echo "Backing up local data..."
if [ -d "data" ]; then
    tar czf "$BACKUP_PATH/data.tar.gz" data/
    echo "✓ Data backed up"
else
    echo "⚠ No data directory found"
fi

# Backup .env
if [ -f ".env" ]; then
    cp .env "$BACKUP_PATH/.env"
    echo "✓ .env backed up"
fi

# Backup InfluxDB
echo ""
echo "Backing up InfluxDB..."
if docker ps | grep -q network-optimizer-influxdb; then
    docker exec network-optimizer-influxdb influxd backup /tmp/backup 2>/dev/null || echo "⚠ InfluxDB backup failed (service may not be running)"
    if docker exec network-optimizer-influxdb test -d /tmp/backup 2>/dev/null; then
        docker cp network-optimizer-influxdb:/tmp/backup "$BACKUP_PATH/influxdb/"
        docker exec network-optimizer-influxdb rm -rf /tmp/backup
        echo "✓ InfluxDB backed up"
    fi
else
    echo "⚠ InfluxDB container not running"
fi

# Backup Grafana
echo ""
echo "Backing up Grafana..."
if docker ps | grep -q network-optimizer-grafana; then
    # Just backup the grafana volume by copying files
    docker run --rm \
        -v network-optimizer_grafana-data:/source:ro \
        -v "$(pwd)/$BACKUP_PATH":/backup \
        busybox tar czf /backup/grafana-data.tar.gz -C /source . 2>/dev/null || echo "⚠ Grafana backup failed"

    if [ -f "$BACKUP_PATH/grafana-data.tar.gz" ]; then
        echo "✓ Grafana backed up"
    fi
else
    echo "⚠ Grafana container not running"
fi

# Create archive
echo ""
echo "Creating final archive..."
cd "$BACKUP_DIR"
tar czf "$BACKUP_NAME.tar.gz" "$BACKUP_NAME/"
rm -rf "$BACKUP_NAME/"

BACKUP_SIZE=$(du -h "$BACKUP_NAME.tar.gz" | cut -f1)

echo ""
echo "=========================================="
echo "✓ Backup complete!"
echo "=========================================="
echo ""
echo "Backup file: $BACKUP_DIR/$BACKUP_NAME.tar.gz"
echo "Size: $BACKUP_SIZE"
echo ""
echo "To restore this backup:"
echo "  ./restore.sh $BACKUP_NAME.tar.gz"
echo ""

# Cleanup old backups (keep last 7)
echo "Cleaning up old backups (keeping last 7)..."
ls -t "$BACKUP_DIR"/network-optimizer-backup-*.tar.gz 2>/dev/null | tail -n +8 | xargs rm -f 2>/dev/null || true
echo "✓ Cleanup complete"
echo ""
