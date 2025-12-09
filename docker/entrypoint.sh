#!/bin/bash
set -e

echo "Starting Network Optimizer..."
echo "Web UI will be available on port 8080"
echo "Metrics API will be available on port 8081"

# Start both applications using supervisord or in parallel
dotnet /app/web/NetworkOptimizer.Web.dll &
WEB_PID=$!

dotnet /app/api/NetworkOptimizer.Api.dll &
API_PID=$!

# Wait for both processes
wait $WEB_PID $API_PID
