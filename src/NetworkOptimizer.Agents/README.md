# NetworkOptimizer.Agents

A comprehensive agent deployment and monitoring system for UniFi Dream Machines (UDM/UCG) and Linux systems. This library provides SSH-based deployment, health monitoring, and template-based configuration management.

## Features

### Agent Deployment
- **SSH-Based Deployment**: Secure deployment via SSH.NET with support for:
  - Password authentication
  - Private key authentication (with or without passphrase)
  - Connection testing before deployment
  - Deployment verification after installation

### Supported Platforms
- **UDM/UCG Devices**: UniFi Dream Machine, Dream Machine Pro, Dream Machine SE, Cloud Gateway Ultra, Cloud Gateway Max
  - Deploys to `/data/on_boot.d/` for persistence across reboots
  - Collects SQM metrics (Smart Queue Management)
  - Runs periodic speedtests
  - Monitors WAN traffic and system stats

- **Linux Systems**: Generic Linux servers (Ubuntu, Debian, CentOS, RHEL, Fedora)
  - Deploys as systemd service
  - Collects CPU, memory, disk, and network metrics
  - Optional Docker container monitoring
  - Automatic service management

### Health Monitoring
- Track agent heartbeats in SQLite database
- Detect offline agents
- Monitor agent status and uptime
- View comprehensive health statistics

### Template System
- Scriban-based template rendering
- Dynamic configuration injection
- Separate templates for UDM/UCG and Linux agents

## Project Structure

```
NetworkOptimizer.Agents/
├── Models/
│   ├── AgentConfiguration.cs      # Agent configuration model
│   ├── DeploymentResult.cs        # Deployment result tracking
│   └── SshCredentials.cs          # SSH authentication credentials
├── Templates/
│   ├── udm-agent-boot.sh.template          # UDM/UCG boot script
│   ├── udm-metrics-collector.sh.template   # UDM/UCG metrics collector
│   ├── install-udm.sh.template             # UDM/UCG installation
│   ├── linux-agent.sh.template             # Linux agent script
│   ├── linux-agent.service.template        # Systemd service definition
│   └── install-linux.sh.template           # Linux installation
├── AgentDeployer.cs               # Main deployment orchestrator
├── AgentHealthMonitor.cs          # Health monitoring and heartbeat tracking
├── ScriptRenderer.cs              # Template rendering engine
└── NetworkOptimizer.Agents.csproj # Project file
```

## Usage

### 1. Deploy an Agent

```csharp
using NetworkOptimizer.Agents;
using NetworkOptimizer.Agents.Models;

// Create SSH credentials
var credentials = new SshCredentials
{
    Host = "192.168.1.1",
    Port = 22,
    Username = "root",
    Password = "your-password",
    // OR use private key:
    // PrivateKeyPath = "/path/to/key",
    // PrivateKeyPassphrase = "key-passphrase"
};

// Create agent configuration
var config = new AgentConfiguration
{
    AgentId = Guid.NewGuid().ToString(),
    DeviceName = "UDM-Pro-Main",
    AgentType = AgentType.UDM,
    InfluxDbUrl = "http://influxdb.local:8086",
    InfluxDbOrg = "myorg",
    InfluxDbBucket = "network-metrics",
    InfluxDbToken = "your-token",
    CollectionIntervalSeconds = 30,
    SpeedtestIntervalMinutes = 60,
    SshCredentials = credentials
};

// Deploy the agent
var scriptRenderer = new ScriptRenderer(logger);
var deployer = new AgentDeployer(logger, scriptRenderer);

var result = await deployer.DeployAgentAsync(config);

if (result.Success)
{
    Console.WriteLine($"Successfully deployed agent: {result.Message}");
}
else
{
    Console.WriteLine($"Deployment failed: {result.Message}");
}
```

### 2. Test SSH Connection

```csharp
var deployer = new AgentDeployer(logger, scriptRenderer);

try
{
    await deployer.TestConnectionAsync(credentials);
    Console.WriteLine("SSH connection successful!");
}
catch (Exception ex)
{
    Console.WriteLine($"Connection failed: {ex.Message}");
}
```

### 3. Monitor Agent Health

```csharp
var healthMonitor = new AgentHealthMonitor(
    logger,
    databasePath: "agents.db",
    offlineThreshold: TimeSpan.FromMinutes(5)
);

// Record a heartbeat
await healthMonitor.RecordHeartbeatAsync(
    agentId: "agent-123",
    deviceName: "UDM-Pro",
    agentType: AgentType.UDM
);

// Get agent status
var status = await healthMonitor.GetAgentStatusAsync("agent-123");
if (status != null)
{
    Console.WriteLine($"Agent: {status.DeviceName}");
    Console.WriteLine($"Online: {status.IsOnline}");
    Console.WriteLine($"Last heartbeat: {status.SecondsSinceLastHeartbeat}s ago");
}

// Get all offline agents
var offlineAgents = await healthMonitor.GetOfflineAgentsAsync();
foreach (var agent in offlineAgents)
{
    Console.WriteLine($"Offline: {agent.DeviceName} (last seen {agent.SecondsSinceLastHeartbeat}s ago)");
}

// Get health statistics
var stats = await healthMonitor.GetHealthStatsAsync();
Console.WriteLine($"Total agents: {stats.TotalAgents}");
Console.WriteLine($"Online: {stats.OnlineAgents} ({stats.OnlinePercentage:F1}%)");
Console.WriteLine($"Offline: {stats.OfflineAgents}");
```

### 4. Render Templates

```csharp
var renderer = new ScriptRenderer(logger);

// Render a specific template
var bootScript = await renderer.RenderTemplateAsync(
    "udm-agent-boot.sh.template",
    config
);

// List available templates
var templates = renderer.ListAvailableTemplates();
foreach (var template in templates)
{
    Console.WriteLine($"Available: {template}");
}

// Validate templates for an agent type
if (!renderer.ValidateTemplates(AgentType.UDM, out var missing))
{
    Console.WriteLine($"Missing templates: {string.Join(", ", missing)}");
}
```

## Metrics Collected

### UDM/UCG Agents
- **SQM Metrics**: Traffic shaping statistics
- **WAN Traffic**: Bytes/packets sent and received, errors
- **System Stats**: CPU usage, memory usage, load average
- **Speedtests**: Download/upload speeds, ping latency
- **Heartbeats**: Agent online status

### Linux Agents
- **CPU**: Usage percentage, load averages (1m, 5m, 15m)
- **Memory**: Total, used, free, available, swap usage
- **Disk**: Usage, I/O operations (reads/writes)
- **Network**: Per-interface traffic, packets, errors
- **Docker** (optional): Container count, per-container CPU and memory
- **Heartbeats**: Agent online status, system uptime

## Deployment Details

### UDM/UCG Deployment
1. Creates `/data/network-optimizer/` directory
2. Deploys boot script to `/data/on_boot.d/99-network-optimizer.sh`
3. Deploys metrics collector to `/data/network-optimizer/metrics-collector.sh`
4. Runs installation script for dependency checks
5. Verifies deployment and starts collector

**Persistence**: Scripts in `/data/on_boot.d/` run automatically on boot, ensuring the agent survives firmware updates and reboots.

### Linux Deployment
1. Creates `/opt/network-optimizer/` and `/var/log/network-optimizer/` directories
2. Deploys agent script to `/opt/network-optimizer/agent.sh`
3. Deploys systemd service to `/etc/systemd/system/network-optimizer-agent.service`
4. Enables and starts the service
5. Verifies service is running

**Service Management**:
- Auto-starts on boot
- Auto-restarts on failure (10s delay)
- Resource limits: 256MB RAM, 10% CPU

## Configuration

### Agent Configuration Options

| Property | Type | Description | Default |
|----------|------|-------------|---------|
| `AgentId` | string | Unique identifier for the agent | Required |
| `DeviceName` | string | Friendly name for the device | Required |
| `AgentType` | AgentType | Type of agent (UDM, UCG, Linux) | Required |
| `InfluxDbUrl` | string | InfluxDB endpoint URL | Required |
| `InfluxDbOrg` | string | InfluxDB organization | Required |
| `InfluxDbBucket` | string | InfluxDB bucket name | Required |
| `InfluxDbToken` | string | InfluxDB authentication token | Required |
| `CollectionIntervalSeconds` | int | Metric collection interval | 30 |
| `SpeedtestIntervalMinutes` | int | Speedtest interval (UDM/UCG) | 60 |
| `EnableDockerMetrics` | bool | Enable Docker metrics (Linux) | false |
| `Tags` | Dictionary | Additional metric tags | {} |
| `SshCredentials` | SshCredentials | SSH connection details | Required |

### Template Variables

Templates have access to all configuration values:
- `{{ agent_id }}`
- `{{ device_name }}`
- `{{ agent_type }}`
- `{{ influxdb_url }}`
- `{{ influxdb_org }}`
- `{{ influxdb_bucket }}`
- `{{ influxdb_token }}`
- `{{ collection_interval }}`
- `{{ speedtest_interval }}`
- `{{ enable_docker }}`
- `{{ is_udm }}`, `{{ is_ucg }}`, `{{ is_linux }}`, `{{ is_unifi }}`

## Dependencies

- **SSH.NET** (2024.1.0): SSH/SFTP client library
- **Scriban** (5.10.0): Template rendering engine
- **Microsoft.Data.Sqlite** (8.0.0): SQLite database for health monitoring
- **Microsoft.Extensions.Logging.Abstractions** (8.0.0): Logging infrastructure

## Error Handling

The deployment system includes comprehensive error handling:
- Connection testing before deployment
- Step-by-step execution with error capture
- Deployment verification after installation
- Detailed error messages and logging
- Graceful rollback on failure

Each deployment step is tracked in `DeploymentResult.Steps` with:
- Step name
- Success/failure status
- Error message (if failed)
- Execution duration

## Logging

All components use `ILogger<T>` for structured logging:
- Debug: Detailed operation information
- Information: Successful operations
- Warning: Non-fatal issues
- Error: Operation failures with exceptions

## Security Considerations

1. **SSH Credentials**: Store securely, never commit to source control
2. **InfluxDB Tokens**: Use read/write tokens with minimal permissions
3. **File Permissions**: Scripts are created with appropriate execute permissions
4. **Service Isolation**: Linux agent runs with security hardening (NoNewPrivileges, PrivateTmp)
5. **Resource Limits**: CPU and memory limits prevent resource exhaustion

## Troubleshooting

### UDM/UCG Issues
- **Check logs**: `tail -f /data/network-optimizer/agent.log`
- **Verify boot script**: `ls -la /data/on_boot.d/99-network-optimizer.sh`
- **Check if running**: `pgrep -f metrics-collector`
- **Manual start**: `/data/on_boot.d/99-network-optimizer.sh`

### Linux Issues
- **Check service status**: `systemctl status network-optimizer-agent.service`
- **View logs**: `journalctl -u network-optimizer-agent.service -f`
- **Restart service**: `systemctl restart network-optimizer-agent.service`
- **Check permissions**: Ensure `/opt/network-optimizer/agent.sh` is executable

### Common Problems
1. **InfluxDB connection fails**: Verify URL, token, and network connectivity
2. **SSH authentication fails**: Check credentials, key permissions, and host keys
3. **Agent offline**: Check network, verify service is running, review logs
4. **Missing metrics**: Ensure dependencies are installed (curl, bc, docker if needed)

## License

This project is part of the NetworkOptimizer suite.
