#!/bin/bash

# Network Optimizer - Stop Script

echo "=========================================="
echo "Network Optimizer - Stopping Services"
echo "=========================================="
echo ""

# Check which docker compose command to use
if docker compose version &> /dev/null 2>&1; then
    COMPOSE_CMD="docker compose"
else
    COMPOSE_CMD="docker-compose"
fi

# Stop services
echo "Stopping all services..."
$COMPOSE_CMD down

echo ""
echo "✓ All services stopped"
echo ""
echo "To start again, run: ./start.sh"
echo "To remove all data, run: ./reset.sh"
echo ""
