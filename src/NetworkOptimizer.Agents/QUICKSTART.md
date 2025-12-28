# NetworkOptimizer.Agents - Quick Start Guide

## Installation

1. Add the project to your solution or reference the NuGet package (when published)

```bash
dotnet add reference ./NetworkOptimizer.Agents/NetworkOptimizer.Agents.csproj
```

2. Install dependencies (automatic via NuGet)
   - SSH.NET 2024.1.0
   - Scriban 5.10.0
   - Microsoft.Data.Sqlite 8.0.0
   - Microsoft.Extensions.Logging.Abstractions 8.0.0

## 5-Minute Setup

### 1. Deploy to UDM/UCG Device

```csharp
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Agents;
using NetworkOptimizer.Agents.Models;

// Setup logging
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var deployerLogger = loggerFactory.CreateLogger<AgentDeployer>();
var rendererLogger = loggerFactory.CreateLogger<ScriptRenderer>();

// Create deployer
var renderer = new ScriptRenderer(rendererLogger);
var deployer = new AgentDeployer(deployerLogger, renderer);

// Configure credentials
var credentials = new SshCredentials
{
    Host = "192.168.1.1",     // Your UDM/UCG IP
    Username = "root",
    Password = "your-password"
};

// Configure agent
var config = new AgentConfiguration
{
    AgentId = Guid.NewGuid().ToString(),
    DeviceName = "UDM-Pro-Main",
    AgentType = AgentType.UDM,

    // InfluxDB settings
    InfluxDbUrl = "http://influxdb.local:8086",
    InfluxDbOrg = "myorg",
    InfluxDbBucket = "network-metrics",
    InfluxDbToken = "your-token-here",

    // Intervals
    CollectionIntervalSeconds = 30,    // Collect every 30s
    SpeedtestIntervalMinutes = 60,     // Speedtest every hour

    SshCredentials = credentials
};

// Deploy!
var result = await deployer.DeployAgentAsync(config);

if (result.Success)
{
    Console.WriteLine($"✓ Deployed successfully!");
    Console.WriteLine($"  Agent ID: {result.AgentId}");
    Console.WriteLine($"  Files deployed: {result.DeployedFiles.Count}");
}
else
{
    Console.WriteLine($"✗ Deployment failed: {result.Message}");
}
```

### 2. Deploy to Linux Server

```csharp
var credentials = new SshCredentials
{
    Host = "linux-server.local",
    Username = "ubuntu",
    PrivateKeyPath = "/home/user/.ssh/id_rsa",  // Use key instead of password
};

var config = new AgentConfiguration
{
    AgentId = Guid.NewGuid().ToString(),
    DeviceName = "Linux-Server-01",
    AgentType = AgentType.Linux,

    InfluxDbUrl = "http://influxdb.local:8086",
    InfluxDbOrg = "myorg",
    InfluxDbBucket = "network-metrics",
    InfluxDbToken = "your-token-here",

    CollectionIntervalSeconds = 30,
    EnableDockerMetrics = true,  // Monitor Docker containers

    SshCredentials = credentials
};

var result = await deployer.DeployAgentAsync(config);
```

### 3. Monitor Agent Health

```csharp
// Create health monitor
var healthLogger = loggerFactory.CreateLogger<AgentHealthMonitor>();
var healthMonitor = new AgentHealthMonitor(
    healthLogger,
    "agents.db",
    offlineThreshold: TimeSpan.FromMinutes(5)
);

// Agents automatically send heartbeats
// You can manually record one for testing:
await healthMonitor.RecordHeartbeatAsync(
    agentId: "test-agent-1",
    deviceName: "Test-Device",
    agentType: AgentType.UDM
);

// Check agent status
var status = await healthMonitor.GetAgentStatusAsync("test-agent-1");
Console.WriteLine($"Agent: {status?.DeviceName}");
Console.WriteLine($"Online: {status?.IsOnline}");
Console.WriteLine($"Last seen: {status?.SecondsSinceLastHeartbeat}s ago");

// Get statistics
var stats = await healthMonitor.GetHealthStatsAsync();
Console.WriteLine($"Total agents: {stats.TotalAgents}");
Console.WriteLine($"Online: {stats.OnlineAgents} ({stats.OnlinePercentage:F1}%)");
Console.WriteLine($"Offline: {stats.OfflineAgents}");
```

## What Gets Deployed?

### UDM/UCG
- `/data/on_boot.d/99-network-optimizer.sh` - Starts on boot
- `/data/network-optimizer/metrics-collector.sh` - Main collector
- Logs to `/data/network-optimizer/agent.log` and `collector.log`

### Linux
- `/opt/network-optimizer/agent.sh` - Main agent script
- `/etc/systemd/system/network-optimizer-agent.service` - Systemd service
- Logs to `/var/log/network-optimizer/agent.log`

## Metrics Collected

### UDM/UCG
- SQM traffic (rx/tx bytes)
- WAN traffic (bytes, packets, errors per interface)
- System stats (CPU %, memory %, load average)
- Speedtest results (download, upload, ping)
- Agent heartbeats

### Linux
- CPU (usage %, load averages 1m/5m/15m)
- Memory (total, used, free, available, swap)
- Disk (usage, I/O reads/writes)
- Network (rx/tx bytes/packets/errors per interface)
- Docker containers (count, per-container CPU/memory)
- Agent heartbeats with uptime

## InfluxDB Line Protocol Format

All metrics are sent in InfluxDB line protocol format:

```
measurement,agent_id=xxx,device=xxx,agent_type=xxx field=value timestamp
```

Example:
```
cpu_stats,agent_id=abc123,device=UDM-Pro,agent_type=udm usage_percent=45.2,load_1m=1.5 1638360000000000000
```

## Verify Deployment

### UDM/UCG
```bash
# SSH into your UDM/UCG
ssh root@192.168.1.1

# Check if boot script exists
ls -la /data/on_boot.d/99-network-optimizer.sh

# Check if collector is running
pgrep -f metrics-collector

# View logs
tail -f /data/network-optimizer/agent.log
tail -f /data/network-optimizer/collector.log

# Manually start
/data/on_boot.d/99-network-optimizer.sh
```

### Linux
```bash
# SSH into your Linux server
ssh user@linux-server.local

# Check service status
systemctl status network-optimizer-agent.service

# View logs
journalctl -u network-optimizer-agent.service -f
# or
tail -f /var/log/network-optimizer/agent.log

# Restart service
sudo systemctl restart network-optimizer-agent.service
```

## Common Issues

### "SSH authentication failed"
- Check username/password or key path
- Verify SSH key permissions: `chmod 600 ~/.ssh/id_rsa`
- Try password auth first to test

### "InfluxDB connection failed"
- Verify InfluxDB URL is accessible from agent
- Check token has write permissions for the bucket
- Test with curl from the device

### "Agent shows offline"
- Check if agent process is running
- Review agent logs for errors
- Verify network connectivity to InfluxDB
- Check InfluxDB is receiving metrics

### "Speedtest not working on UDM"
- Install speedtest-cli: `pip3 install speedtest-cli`
- Or disable speedtests: `SpeedtestIntervalMinutes = 0`

## Next Steps

1. **Set up InfluxDB**: If you haven't already
   ```bash
   docker run -d -p 8086:8086 influxdb:2.7
   ```

2. **Create dashboards**: Use Grafana or InfluxDB UI to visualize metrics

3. **Monitor multiple devices**: Deploy to all your UDM/UCG and Linux systems

4. **Set up alerts**: Configure alerts for offline agents or metric thresholds

5. **Explore the code**: Check `Example.cs` for more advanced usage patterns

## Documentation

- **README.md** - Full documentation with all features
- **Example.cs** - Comprehensive code examples
- **PROJECT-SUMMARY.md** - Project architecture and file overview

## Support

For issues or questions:
1. Check the troubleshooting section in README.md
2. Review agent logs on the remote device
3. Enable debug logging in your application

## Security Notes

- Never commit SSH credentials to source control
- Use private key authentication in production
- Restrict InfluxDB token permissions to minimum required
- Store credentials in secure configuration (Azure Key Vault, AWS Secrets Manager, etc.)
- Review deployed scripts before running in production

## Performance

- **UDM/UCG**: Minimal impact (~1-2% CPU, ~10MB RAM)
- **Linux**: Minimal impact (~1% CPU, ~20MB RAM)
- **Network**: ~1KB per collection interval to InfluxDB
- **Disk**: Log rotation recommended for long-running deployments

## License

This is part of the NetworkOptimizer suite.
