# NetworkOptimizer.Storage

Production-ready storage layer for NetworkOptimizer with InfluxDB time-series metrics and SQLite local storage.

## Features

### InfluxDB Storage (InfluxDbStorage.cs)
- **Batch Writing**: Efficient batch writing with configurable buffer size and flush intervals
- **ConcurrentQueue Buffer**: Thread-safe metric buffering for high-throughput scenarios
- **Automatic Flushing**: Timer-based and size-based flush triggers
- **Health Monitoring**: Built-in health check method for monitoring connectivity
- **Proper Disposal**: Ensures all buffered data is flushed before disposal
- **Multiple Metric Types**: Support for device metrics, interface metrics, and SQM metrics

### SQLite Repository (SqliteRepository.cs)
- **Audit History**: Store and query network device audit results
- **SQM Baselines**: Manage 168-hour (7-day) baseline tables for Smart Queue Management
- **Agent Configuration**: Store and manage monitoring agent settings
- **License Management**: Track and validate license information
- **EF Core Integration**: Modern Entity Framework Core with async patterns

## Usage

### Setup with Dependency Injection

```csharp
using NetworkOptimizer.Storage;

// Configure services
var services = new ServiceCollection();

// InfluxDB configuration
var influxConfig = new StorageConfiguration
{
    Url = "http://localhost:8086",
    Token = "your-influx-token",
    Organization = "NetworkOptimizer",
    Bucket = "network_metrics",
    BatchFlushIntervalSeconds = 5,
    MaxBufferSize = 1000
};

// SQLite configuration
var sqliteConfig = new SqliteConfiguration
{
    DatabasePath = "networkoptimizer.db"
};

// Add storage services
services.AddNetworkOptimizerStorage(influxConfig, sqliteConfig);

// Build service provider
var serviceProvider = services.BuildServiceProvider();

// Ensure database is created
await serviceProvider.EnsureDatabaseCreatedAsync();
```

### Using InfluxDB Storage

```csharp
using NetworkOptimizer.Storage.Interfaces;

// Inject IMetricsStorage
var metricsStorage = serviceProvider.GetRequiredService<IMetricsStorage>();

// Write device metrics
var metrics = new Dictionary<string, object>
{
    { "cpu_usage", 45.2 },
    { "memory_usage", 62.1 },
    { "uptime_seconds", 86400 }
};

await metricsStorage.WriteMetricsAsync(
    deviceId: "device-001",
    measurementType: "device_metrics",
    metrics: metrics,
    tags: new Dictionary<string, string> { { "location", "datacenter-1" } }
);

// Write interface metrics
var interfaceMetrics = new Dictionary<string, object>
{
    { "bits_in", 1000000 },
    { "bits_out", 500000 },
    { "speed_bps", 1000000000 },
    { "is_up", true }
};

await metricsStorage.WriteInterfaceMetricsAsync(
    deviceId: "device-001",
    interfaceId: "eth0",
    metrics: interfaceMetrics
);

// Write SQM metrics
var sqmMetrics = new Dictionary<string, object>
{
    { "latency_ms", 12.5 },
    { "jitter_ms", 2.1 },
    { "packet_loss_percent", 0.01 }
};

await metricsStorage.WriteSqmMetricsAsync(
    deviceId: "device-001",
    metrics: sqmMetrics,
    tags: new Dictionary<string, string> { { "interface", "wan" } }
);

// Check health
var isHealthy = await metricsStorage.HealthCheckAsync();
```

### Using SQLite Repository

```csharp
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

// Inject ILocalRepository
var repository = serviceProvider.GetRequiredService<ILocalRepository>();

// Save audit result
var auditResult = new AuditResult
{
    DeviceId = "device-001",
    DeviceName = "Main Router",
    AuditDate = DateTime.UtcNow,
    TotalChecks = 25,
    PassedChecks = 22,
    FailedChecks = 2,
    WarningChecks = 1,
    ComplianceScore = 88.0,
    FirmwareVersion = "6.5.23",
    Model = "UDM-Pro"
};

var auditId = await repository.SaveAuditResultAsync(auditResult);

// Get audit history
var history = await repository.GetAuditHistoryAsync(deviceId: "device-001", limit: 100);

// Save SQM baseline
var baseline = new SqmBaseline
{
    DeviceId = "device-001",
    InterfaceId = "wan",
    InterfaceName = "WAN",
    BaselineStart = DateTime.UtcNow.AddDays(-7),
    BaselineEnd = DateTime.UtcNow,
    BaselineHours = 168,
    AvgLatency = 15.2,
    PeakLatency = 45.8,
    P95Latency = 28.5,
    P99Latency = 38.2,
    RecommendedDownloadMbps = 950,
    RecommendedUploadMbps = 40
};

var baselineId = await repository.SaveSqmBaselineAsync(baseline);

// Get SQM baseline
var storedBaseline = await repository.GetSqmBaselineAsync("device-001", "wan");

// Save agent configuration
var agentConfig = new AgentConfiguration
{
    AgentId = "agent-001",
    AgentName = "Main Monitoring Agent",
    DeviceUrl = "https://192.168.1.1",
    PollingIntervalSeconds = 60,
    MetricsEnabled = true,
    SqmEnabled = true,
    BatchSize = 1000,
    FlushIntervalSeconds = 5
};

await repository.SaveAgentConfigAsync(agentConfig);
```

## Database Schema

### AuditResults Table
Stores historical audit results for network devices with compliance scoring.

### SqmBaselines Table
Stores 168-hour baseline data for Smart Queue Management analysis with unique constraint on (DeviceId, InterfaceId).

### AgentConfigurations Table
Stores configuration for monitoring agents including polling intervals and feature flags.

### Licenses Table
Tracks license information with expiration dates and feature limits.

## Configuration

### StorageConfiguration (InfluxDB)
- **Url**: InfluxDB server URL (default: http://localhost:8086)
- **Token**: Authentication token
- **Organization**: Organization name (default: NetworkOptimizer)
- **Bucket**: Bucket name (default: network_metrics)
- **WriteTimeout**: Write operation timeout (default: 30 seconds)
- **MaxRetries**: Maximum retry attempts (default: 3)
- **BatchFlushIntervalSeconds**: Batch flush interval (default: 5 seconds, 0 to disable batching)
- **MaxBufferSize**: Maximum buffer size before forced flush (default: 1000)

### SqliteConfiguration
- **DatabasePath**: Path to SQLite database file (default: networkoptimizer.db)
- **EnableSensitiveDataLogging**: Enable EF Core sensitive data logging (default: false)
- **CommandTimeout**: Command timeout in seconds (default: 30)

## Migrations

EF Core migrations are included in the `Migrations/` folder. The initial migration creates all tables and indexes.

To apply migrations manually:
```bash
dotnet ef database update --project NetworkOptimizer.Storage
```

Or use the extension method:
```csharp
await serviceProvider.EnsureDatabaseCreatedAsync();
```

## Dependencies

- **InfluxDB.Client** (4.18.0): Official InfluxDB client library
- **Microsoft.EntityFrameworkCore.Sqlite** (10.0.1): SQLite database provider
- **Microsoft.EntityFrameworkCore.Design** (10.0.1): EF Core design-time tools
- **Microsoft.Extensions.Logging.Abstractions** (10.0.1): Logging abstractions

## .NET Version

Built for **.NET 10.0** with nullable reference types enabled and implicit usings.

## Best Practices

1. **Always dispose**: Both `InfluxDbStorage` and `SqliteRepository` implement `IDisposable`
2. **Use dependency injection**: Register services using the provided extension methods
3. **Handle exceptions**: Both storage implementations throw exceptions that should be caught
4. **Monitor health**: Regularly call `HealthCheckAsync()` to monitor InfluxDB connectivity
5. **Batch configuration**: Tune `BatchFlushIntervalSeconds` and `MaxBufferSize` based on your workload
6. **Database migrations**: Always apply migrations before using SQLite repository

## Example Application

```csharp
using NetworkOptimizer.Storage;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddLogging();

services.AddNetworkOptimizerStorage(
    new StorageConfiguration
    {
        Url = "http://localhost:8086",
        Token = Environment.GetEnvironmentVariable("INFLUX_TOKEN")!,
        Organization = "MyOrg",
        Bucket = "network_metrics"
    },
    new SqliteConfiguration
    {
        DatabasePath = "data/networkoptimizer.db"
    }
);

var provider = services.BuildServiceProvider();
await provider.EnsureDatabaseCreatedAsync();

// Use the services
var metricsStorage = provider.GetRequiredService<IMetricsStorage>();
var localRepo = provider.GetRequiredService<ILocalRepository>();

// Your application logic here...
```
