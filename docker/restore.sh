#!/bin/bash

# Network Optimizer - Restore Script

set -e

if [ -z "$1" ]; then
    echo "Usage: ./restore.sh <backup-file.tar.gz>"
    echo ""
    echo "Available backups:"
    ls -lh backups/network-optimizer-backup-*.tar.gz 2>/dev/null || echo "  No backups found"
    exit 1
fi

BACKUP_FILE="$1"

if [ ! -f "$BACKUP_FILE" ]; then
    echo "Error: Backup file not found: $BACKUP_FILE"
    exit 1
fi

echo "=========================================="
echo "Network Optimizer - Restore"
echo "=========================================="
echo ""
echo "Restoring from: $BACKUP_FILE"
echo ""
echo "WARNING: This will replace all current data!"
echo ""
read -p "Continue? (y/N): " -n 1 -r
echo

if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Restore cancelled."
    exit 0
fi

# Check which docker compose command to use
if docker compose version &> /dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
else
    COMPOSE_CMD="docker-compose"
fi

# Stop services
echo ""
echo "Stopping services..."
$COMPOSE_CMD down

# Extract backup
RESTORE_DIR=$(mktemp -d)
echo "Extracting backup..."
tar xzf "$BACKUP_FILE" -C "$RESTORE_DIR"

# Find the backup directory
BACKUP_NAME=$(basename "$BACKUP_FILE" .tar.gz)
BACKUP_PATH="$RESTORE_DIR/$BACKUP_NAME"

if [ ! -d "$BACKUP_PATH" ]; then
    # Try to find it
    BACKUP_PATH=$(find "$RESTORE_DIR" -type d -name "network-optimizer-backup-*" | head -n 1)
fi

if [ ! -d "$BACKUP_PATH" ]; then
    echo "Error: Could not find backup directory in archive"
    rm -rf "$RESTORE_DIR"
    exit 1
fi

# Restore local data
echo ""
echo "Restoring local data..."
if [ -f "$BACKUP_PATH/data.tar.gz" ]; then
    rm -rf data/
    tar xzf "$BACKUP_PATH/data.tar.gz"
    echo "✓ Data restored"
fi

# Restore .env
if [ -f "$BACKUP_PATH/.env" ]; then
    cp "$BACKUP_PATH/.env" .env
    echo "✓ .env restored"
fi

# Start InfluxDB to restore its data
echo ""
echo "Starting InfluxDB for restore..."
$COMPOSE_CMD up -d influxdb
sleep 10

# Restore InfluxDB
if [ -d "$BACKUP_PATH/influxdb" ]; then
    echo "Restoring InfluxDB..."
    docker cp "$BACKUP_PATH/influxdb" network-optimizer-influxdb:/tmp/restore
    docker exec network-optimizer-influxdb influxd restore /tmp/restore
    docker exec network-optimizer-influxdb rm -rf /tmp/restore
    echo "✓ InfluxDB restored"
fi

# Restore Grafana
if [ -f "$BACKUP_PATH/grafana-data.tar.gz" ]; then
    echo "Restoring Grafana..."
    docker run --rm \
        -v network-optimizer_grafana-data:/target \
        -v "$BACKUP_PATH":/backup \
        busybox tar xzf /backup/grafana-data.tar.gz -C /target
    echo "✓ Grafana restored"
fi

# Cleanup
rm -rf "$RESTORE_DIR"

# Start all services
echo ""
echo "Starting all services..."
$COMPOSE_CMD up -d

echo ""
echo "=========================================="
echo "✓ Restore complete!"
echo "=========================================="
echo ""
echo "Services are starting..."
echo "Check status with: docker-compose ps"
echo ""
