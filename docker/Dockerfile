# Multi-stage build for Network Optimizer
# Stage 1: Build stage with full .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Version passed from CI/CD (MinVer can't access .git in Docker context)
ARG VERSION=0.0.0-alpha.0

WORKDIR /src

# Copy solution file, build props, and all project files first for layer caching
COPY ["NetworkOptimizer.sln", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["src/NetworkOptimizer.Core/NetworkOptimizer.Core.csproj", "src/NetworkOptimizer.Core/"]
COPY ["src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj", "src/NetworkOptimizer.Web/"]
COPY ["src/NetworkOptimizer.UniFi/NetworkOptimizer.UniFi.csproj", "src/NetworkOptimizer.UniFi/"]
COPY ["src/NetworkOptimizer.Audit/NetworkOptimizer.Audit.csproj", "src/NetworkOptimizer.Audit/"]
COPY ["src/NetworkOptimizer.Sqm/NetworkOptimizer.Sqm.csproj", "src/NetworkOptimizer.Sqm/"]
COPY ["src/NetworkOptimizer.Monitoring/NetworkOptimizer.Monitoring.csproj", "src/NetworkOptimizer.Monitoring/"]
COPY ["src/NetworkOptimizer.Storage/NetworkOptimizer.Storage.csproj", "src/NetworkOptimizer.Storage/"]
COPY ["src/NetworkOptimizer.Agents/NetworkOptimizer.Agents.csproj", "src/NetworkOptimizer.Agents/"]
COPY ["src/NetworkOptimizer.Reports/NetworkOptimizer.Reports.csproj", "src/NetworkOptimizer.Reports/"]
COPY ["src/NetworkOptimizer.Diagnostics/NetworkOptimizer.Diagnostics.csproj", "src/NetworkOptimizer.Diagnostics/"]

# Restore dependencies
RUN dotnet restore "src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj"

# Copy all source files
COPY src/ src/

# Build and publish Web UI (includes all dependent projects)
# MinVerVersionOverride tells MinVer to use this version instead of reading git tags
RUN dotnet publish "src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    -p:MinVerVersionOverride=${VERSION}

# Stage 2: Build iperf3 from source for latest version
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS iperf-build

# iperf3 version - check https://github.com/esnet/iperf/releases for updates
ARG IPERF3_VERSION=3.20

RUN apt-get update && apt-get install -y \
    build-essential \
    curl \
    libssl-dev \
    && rm -rf /var/lib/apt/lists/*

# Build iperf3 from source
WORKDIR /tmp
RUN curl -LO https://github.com/esnet/iperf/releases/download/${IPERF3_VERSION}/iperf-${IPERF3_VERSION}.tar.gz \
    && tar xzf iperf-${IPERF3_VERSION}.tar.gz \
    && cd iperf-${IPERF3_VERSION} \
    && ./configure --prefix=/usr/local \
    && make \
    && make install

# Stage 3: Runtime stage with ASP.NET runtime only
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install necessary utilities (without iperf3 - we copy it from build)
# Note: openssh-client and sshpass removed - SSH.NET handles SSH natively
RUN apt-get update && apt-get install -y \
    curl \
    iputils-ping \
    libssl3 \
    gosu \
    sqlite3 \
    && rm -rf /var/lib/apt/lists/*

# Copy iperf3 from build stage
COPY --from=iperf-build /usr/local/bin/iperf3 /usr/local/bin/
COPY --from=iperf-build /usr/local/lib/libiperf* /usr/local/lib/
RUN ldconfig

# Copy published application
COPY --from=build /app/publish .

# Create directories for volumes
RUN mkdir -p /app/data /app/ssh-keys /app/logs

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8042 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Expose ports
EXPOSE 8042
EXPOSE 5201

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:8042/api/health || exit 1

# Set ownership for app directories
RUN chown -R app:app /app

# Copy entrypoint script
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Run as root initially; entrypoint will fix volume permissions then drop to app user
ENTRYPOINT ["/entrypoint.sh"]
