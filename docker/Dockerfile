# Multi-stage build for Network Optimizer
# Stage 1: Build stage with full .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution file, build props, and all project files first for layer caching
COPY ["NetworkOptimizer.sln", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["NetworkOptimizer.Core/NetworkOptimizer.Core.csproj", "NetworkOptimizer.Core/"]
COPY ["src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj", "src/NetworkOptimizer.Web/"]
COPY ["src/NetworkOptimizer.UniFi/NetworkOptimizer.UniFi.csproj", "src/NetworkOptimizer.UniFi/"]
COPY ["src/NetworkOptimizer.Audit/NetworkOptimizer.Audit.csproj", "src/NetworkOptimizer.Audit/"]
COPY ["src/NetworkOptimizer.Sqm/NetworkOptimizer.Sqm.csproj", "src/NetworkOptimizer.Sqm/"]
COPY ["src/NetworkOptimizer.Monitoring/NetworkOptimizer.Monitoring.csproj", "src/NetworkOptimizer.Monitoring/"]
COPY ["src/NetworkOptimizer.Storage/NetworkOptimizer.Storage.csproj", "src/NetworkOptimizer.Storage/"]
COPY ["src/NetworkOptimizer.Agents/NetworkOptimizer.Agents.csproj", "src/NetworkOptimizer.Agents/"]
COPY ["src/NetworkOptimizer.Reports/NetworkOptimizer.Reports.csproj", "src/NetworkOptimizer.Reports/"]

# Restore dependencies
RUN dotnet restore "src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj"

# Copy all source files
COPY NetworkOptimizer.Core/ NetworkOptimizer.Core/
COPY src/ src/

# Build and publish Web UI (includes all dependent projects)
RUN dotnet publish "src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

# Stage 2: Runtime stage with ASP.NET runtime only
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install necessary utilities
RUN apt-get update && apt-get install -y \
    curl \
    iputils-ping \
    openssh-client \
    sshpass \
    iperf3 \
    && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Create directories for volumes
RUN mkdir -p /app/data /app/ssh-keys /app/logs

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Expose ports
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

# Run as non-root user
RUN useradd -r -u 1000 -g root appuser && \
    chown -R appuser:root /app
USER appuser

ENTRYPOINT ["dotnet", "NetworkOptimizer.Web.dll"]
