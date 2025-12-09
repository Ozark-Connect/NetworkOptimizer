#!/bin/bash

# Network Optimizer - Reset Script
# WARNING: This deletes ALL data!

echo "=========================================="
echo "Network Optimizer - RESET"
echo "=========================================="
echo ""
echo "WARNING: This will DELETE ALL DATA!"
echo "  - Docker volumes (InfluxDB, Grafana)"
echo "  - Local data directory"
echo "  - Logs"
echo ""
read -p "Are you sure? Type 'yes' to continue: " -r
echo

if [ "$REPLY" != "yes" ]; then
    echo "Reset cancelled."
    exit 0
fi

echo ""
read -p "Last chance! Type 'DELETE' to confirm: " -r
echo

if [ "$REPLY" != "DELETE" ]; then
    echo "Reset cancelled."
    exit 0
fi

# Check which docker compose command to use
if docker compose version &> /dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
else
    COMPOSE_CMD="docker-compose"
fi

echo ""
echo "Stopping services..."
$COMPOSE_CMD down -v

echo "Removing data directories..."
rm -rf data/
rm -rf logs/

echo "Removing SSH keys..."
rm -rf ssh-keys/

echo ""
echo "✓ Reset complete"
echo ""
echo "All data has been deleted."
echo "To start fresh, run: ./start.sh"
echo ""
