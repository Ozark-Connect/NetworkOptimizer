#!/bin/bash

# Network Optimizer - Quick Start Script
# This script helps you get started quickly with secure defaults

set -e

echo "=========================================="
echo "Network Optimizer - Quick Start"
echo "=========================================="
echo ""

# Check if .env exists
if [ -f .env ]; then
    echo "✓ .env file already exists"
    read -p "Do you want to regenerate it? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        echo "Using existing .env file"
        SKIP_ENV=true
    fi
fi

if [ "$SKIP_ENV" != "true" ]; then
    echo "Creating .env file with secure random passwords..."

    # Copy template
    cp .env.example .env

    # Generate secure passwords and tokens
    INFLUXDB_PASSWORD=$(openssl rand -base64 24)
    INFLUXDB_TOKEN=$(openssl rand -base64 32)
    GRAFANA_PASSWORD=$(openssl rand -base64 24)
    AGENT_AUTH_TOKEN=$(openssl rand -hex 32)

    # Update .env file
    if [[ "$OSTYPE" == "darwin"* ]]; then
        # macOS
        sed -i '' "s/changeme_influxdb_password/$INFLUXDB_PASSWORD/" .env
        sed -i '' "s/changeme_influxdb_token/$INFLUXDB_TOKEN/" .env
        sed -i '' "s/changeme_grafana_password/$GRAFANA_PASSWORD/" .env
        sed -i '' "s/# AGENT_AUTH_TOKEN=/AGENT_AUTH_TOKEN=$AGENT_AUTH_TOKEN/" .env
    else
        # Linux
        sed -i "s/changeme_influxdb_password/$INFLUXDB_PASSWORD/" .env
        sed -i "s/changeme_influxdb_token/$INFLUXDB_TOKEN/" .env
        sed -i "s/changeme_grafana_password/$GRAFANA_PASSWORD/" .env
        sed -i "s/# AGENT_AUTH_TOKEN=/AGENT_AUTH_TOKEN=$AGENT_AUTH_TOKEN/" .env
    fi

    echo "✓ Generated secure passwords and tokens"
    echo ""
    echo "IMPORTANT: Save these credentials!"
    echo "=========================================="
    echo "Grafana Admin Password: $GRAFANA_PASSWORD"
    echo "InfluxDB Admin Password: $INFLUXDB_PASSWORD"
    echo "InfluxDB Token: $INFLUXDB_TOKEN"
    echo "Agent Auth Token: $AGENT_AUTH_TOKEN"
    echo "=========================================="
    echo ""
    echo "These are also saved in the .env file"
    read -p "Press Enter to continue..."
    echo ""
fi

# Check for Docker
echo "Checking prerequisites..."
if ! command -v docker &> /dev/null; then
    echo "✗ Docker not found. Please install Docker first."
    echo "  Visit: https://docs.docker.com/get-docker/"
    exit 1
fi
echo "✓ Docker installed"

if ! command -v docker-compose &> /dev/null && ! docker compose version &> /dev/null 2>&1; then
    echo "✗ Docker Compose not found. Please install Docker Compose first."
    echo "  Visit: https://docs.docker.com/compose/install/"
    exit 1
fi
echo "✓ Docker Compose installed"

# Create directories
echo ""
echo "Creating directories..."
mkdir -p data logs ssh-keys
chmod 700 ssh-keys
echo "✓ Directories created"

# Pull images
echo ""
echo "Pulling Docker images (this may take a few minutes)..."
if docker compose version &> /dev/null 2>&1; then
    docker compose pull
else
    docker-compose pull
fi
echo "✓ Images pulled"

# Start services
echo ""
echo "Starting services..."
if docker compose version &> /dev/null 2>&1; then
    docker compose up -d
else
    docker-compose up -d
fi

# Wait for services to be healthy
echo ""
echo "Waiting for services to start..."
sleep 10

# Check health
echo ""
echo "Checking service health..."
if docker compose version &> /dev/null 2>&1; then
    docker compose ps
else
    docker-compose ps
fi

echo ""
echo "=========================================="
echo "Network Optimizer is starting!"
echo "=========================================="
echo ""
echo "Services will be available at:"
echo "  Web UI:  http://localhost:8080"
echo "  Grafana: http://localhost:3000"
echo "  API:     http://localhost:8081"
echo ""
echo "Grafana Credentials:"
echo "  Username: admin"
echo "  Password: (check .env file or see above)"
echo ""
echo "To view logs:"
echo "  docker-compose logs -f"
echo ""
echo "To stop services:"
echo "  docker-compose down"
echo ""
echo "For more information, see README.md and DEPLOYMENT.md"
echo "=========================================="
