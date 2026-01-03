#!/bin/bash
set -e

# Fix ownership of mounted volumes (they may be created as root by Docker)
# This runs as root before dropping to the app user
chown -R app:app /app/data /app/logs 2>/dev/null || true

# Drop to app user and run the application
exec gosu app dotnet NetworkOptimizer.Web.dll "$@"
