using Microsoft.Extensions.Logging;
using NetworkOptimizer.Agents;
using NetworkOptimizer.Agents.Models;

namespace NetworkOptimizer.Agents.Examples;

/// <summary>
/// Example usage of the NetworkOptimizer.Agents library
/// </summary>
public class AgentDeploymentExample
{
    private readonly ILogger<AgentDeploymentExample> _logger;
    private readonly AgentDeployer _deployer;
    private readonly AgentHealthMonitor _healthMonitor;
    private readonly ScriptRenderer _scriptRenderer;

    public AgentDeploymentExample(ILogger<AgentDeploymentExample> logger)
    {
        _logger = logger;
        _scriptRenderer = new ScriptRenderer(LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ScriptRenderer>());
        _deployer = new AgentDeployer(LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AgentDeployer>(), _scriptRenderer);
        _healthMonitor = new AgentHealthMonitor(
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<AgentHealthMonitor>(),
            "agents.db",
            TimeSpan.FromMinutes(5)
        );
    }

    /// <summary>
    /// Example: Deploy agent to UDM/UCG device
    /// </summary>
    public async Task DeployUdmAgentExample()
    {
        // Configure SSH credentials
        var credentials = new SshCredentials
        {
            Host = "192.168.1.1",
            Port = 22,
            Username = "root",
            Password = "ubnt",  // Or use key-based auth
            TimeoutSeconds = 30
        };

        // Configure the agent
        var config = new AgentConfiguration
        {
            AgentId = Guid.NewGuid().ToString(),
            DeviceName = "UDM-Pro-Main",
            AgentType = AgentType.UDM,
            InfluxDbUrl = "http://influxdb.local:8086",
            InfluxDbOrg = "myorg",
            InfluxDbBucket = "network-metrics",
            InfluxDbToken = "your-influxdb-token-here",
            CollectionIntervalSeconds = 30,
            SpeedtestIntervalMinutes = 60,
            Tags = new Dictionary<string, string>
            {
                { "location", "home" },
                { "environment", "production" }
            },
            SshCredentials = credentials
        };

        // Test connection first
        try
        {
            await _deployer.TestConnectionAsync(credentials);
            _logger.LogInformation("SSH connection test successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH connection test failed");
            return;
        }

        // Deploy the agent
        var result = await _deployer.DeployAgentAsync(config);

        if (result.Success)
        {
            _logger.LogInformation("Successfully deployed agent {AgentId} to {Device}",
                result.AgentId, result.DeviceName);

            // Print deployment steps
            foreach (var step in result.Steps)
            {
                _logger.LogInformation("  Step: {Name} - {Status} ({Duration}ms)",
                    step.Name, step.Success ? "✓" : "✗", step.DurationMs);
            }

            // Print deployed files
            _logger.LogInformation("Deployed files:");
            foreach (var file in result.DeployedFiles)
            {
                _logger.LogInformation("  - {File}", file);
            }
        }
        else
        {
            _logger.LogError("Deployment failed: {Message}", result.Message);
        }
    }

    /// <summary>
    /// Example: Deploy agent to Linux system with key-based authentication
    /// </summary>
    public async Task DeployLinuxAgentExample()
    {
        // Configure SSH credentials with private key
        var credentials = new SshCredentials
        {
            Host = "linux-server.local",
            Port = 22,
            Username = "ubuntu",
            PrivateKeyPath = "/home/user/.ssh/id_rsa",
            PrivateKeyPassphrase = null,  // Set if key is encrypted
            TimeoutSeconds = 30
        };

        // Configure the agent
        var config = new AgentConfiguration
        {
            AgentId = Guid.NewGuid().ToString(),
            DeviceName = "Linux-Server-01",
            AgentType = AgentType.Linux,
            InfluxDbUrl = "http://influxdb.local:8086",
            InfluxDbOrg = "myorg",
            InfluxDbBucket = "network-metrics",
            InfluxDbToken = "your-influxdb-token-here",
            CollectionIntervalSeconds = 30,
            EnableDockerMetrics = true,  // Enable Docker monitoring
            Tags = new Dictionary<string, string>
            {
                { "role", "web-server" },
                { "datacenter", "us-east-1" }
            },
            SshCredentials = credentials
        };

        var result = await _deployer.DeployAgentAsync(config);

        if (result.Success)
        {
            _logger.LogInformation("Successfully deployed Linux agent");

            if (result.Verification != null)
            {
                _logger.LogInformation("Verification status: {Status}",
                    result.Verification.Passed ? "Passed" : "Failed");
                _logger.LogInformation("Service status: {Status}",
                    result.Verification.ServiceStatus ?? "N/A");
                _logger.LogInformation("Agent running: {Running}",
                    result.Verification.AgentRunning);
            }
        }
    }

    /// <summary>
    /// Example: Monitor agent health
    /// </summary>
    public async Task MonitorAgentHealthExample()
    {
        // Simulate receiving a heartbeat from an agent
        await _healthMonitor.RecordHeartbeatAsync(
            agentId: "agent-123",
            deviceName: "UDM-Pro-Main",
            agentType: AgentType.UDM,
            metadata: new Dictionary<string, string>
            {
                { "version", "1.0.0" },
                { "uptime", "86400" }
            }
        );

        // Get status of a specific agent
        var status = await _healthMonitor.GetAgentStatusAsync("agent-123");
        if (status != null)
        {
            _logger.LogInformation("Agent Status:");
            _logger.LogInformation("  Name: {Name}", status.DeviceName);
            _logger.LogInformation("  Type: {Type}", status.AgentType);
            _logger.LogInformation("  Online: {Online}", status.IsOnline);
            _logger.LogInformation("  Last Heartbeat: {Seconds}s ago",
                status.SecondsSinceLastHeartbeat);
            _logger.LogInformation("  First Seen: {FirstSeen}", status.FirstSeen);
        }

        // Get all agents
        var allAgents = await _healthMonitor.GetAllAgentsAsync();
        _logger.LogInformation("Total agents: {Count}", allAgents.Count);

        // Get offline agents
        var offlineAgents = await _healthMonitor.GetOfflineAgentsAsync();
        if (offlineAgents.Any())
        {
            _logger.LogWarning("Offline agents:");
            foreach (var agent in offlineAgents)
            {
                _logger.LogWarning("  - {Name} (offline for {Seconds}s)",
                    agent.DeviceName, agent.SecondsSinceLastHeartbeat);
            }
        }

        // Get health statistics
        var stats = await _healthMonitor.GetHealthStatsAsync();
        _logger.LogInformation("Health Statistics:");
        _logger.LogInformation("  Total: {Total}", stats.TotalAgents);
        _logger.LogInformation("  Online: {Online} ({Percent:F1}%)",
            stats.OnlineAgents, stats.OnlinePercentage);
        _logger.LogInformation("  Offline: {Offline}", stats.OfflineAgents);

        foreach (var agentType in stats.AgentsByType)
        {
            _logger.LogInformation("  {Type}: {Count}", agentType.Key, agentType.Value);
        }
    }

    /// <summary>
    /// Example: Work with templates
    /// </summary>
    public async Task TemplateRenderingExample()
    {
        var config = new AgentConfiguration
        {
            AgentId = "test-agent-1",
            DeviceName = "Test-Device",
            AgentType = AgentType.UDM,
            InfluxDbUrl = "http://localhost:8086",
            InfluxDbOrg = "test-org",
            InfluxDbBucket = "test-bucket",
            InfluxDbToken = "test-token",
            CollectionIntervalSeconds = 30,
            SpeedtestIntervalMinutes = 60,
            SshCredentials = new SshCredentials
            {
                Host = "localhost",
                Username = "test",
                Password = "test"
            }
        };

        // List available templates
        var templates = _scriptRenderer.ListAvailableTemplates();
        _logger.LogInformation("Available templates:");
        foreach (var template in templates)
        {
            _logger.LogInformation("  - {Template}", template);
        }

        // Validate templates for agent type
        if (_scriptRenderer.ValidateTemplates(AgentType.UDM, out var missingTemplates))
        {
            _logger.LogInformation("All UDM templates are present");
        }
        else
        {
            _logger.LogWarning("Missing templates: {Templates}",
                string.Join(", ", missingTemplates));
        }

        // Render a specific template
        var bootScript = await _scriptRenderer.RenderTemplateAsync(
            "udm-agent-boot.sh.template",
            config
        );

        _logger.LogInformation("Rendered boot script:");
        _logger.LogInformation(bootScript);

        // Get all templates for an agent type
        var udmTemplates = _scriptRenderer.GetTemplatesForAgent(AgentType.UDM);
        _logger.LogInformation("UDM templates: {Templates}",
            string.Join(", ", udmTemplates));
    }

    /// <summary>
    /// Example: Cleanup and maintenance
    /// </summary>
    public async Task MaintenanceExample()
    {
        // Remove an agent from monitoring
        await _healthMonitor.RemoveAgentAsync("old-agent-id");
        _logger.LogInformation("Removed old agent from monitoring");

        // Cleanup old heartbeat records (older than 30 days)
        await _healthMonitor.CleanupOldRecordsAsync(TimeSpan.FromDays(30));
        _logger.LogInformation("Cleaned up old heartbeat records");
    }

    /// <summary>
    /// Example: Bulk deployment to multiple devices
    /// </summary>
    public async Task BulkDeploymentExample()
    {
        var devices = new[]
        {
            new { Host = "192.168.1.1", Name = "UDM-Main", Type = AgentType.UDM },
            new { Host = "192.168.1.2", Name = "UCG-Branch", Type = AgentType.UCG },
            new { Host = "192.168.1.10", Name = "Linux-Server", Type = AgentType.Linux }
        };

        foreach (var device in devices)
        {
            var credentials = new SshCredentials
            {
                Host = device.Host,
                Port = 22,
                Username = "root",
                Password = "password"  // Use secure credential storage in production
            };

            var config = new AgentConfiguration
            {
                AgentId = Guid.NewGuid().ToString(),
                DeviceName = device.Name,
                AgentType = device.Type,
                InfluxDbUrl = "http://influxdb.local:8086",
                InfluxDbOrg = "myorg",
                InfluxDbBucket = "network-metrics",
                InfluxDbToken = "token",
                CollectionIntervalSeconds = 30,
                SpeedtestIntervalMinutes = 60,
                EnableDockerMetrics = device.Type == AgentType.Linux,
                SshCredentials = credentials
            };

            try
            {
                var result = await _deployer.DeployAgentAsync(config);
                if (result.Success)
                {
                    _logger.LogInformation("✓ Deployed to {Device}", device.Name);
                }
                else
                {
                    _logger.LogError("✗ Failed to deploy to {Device}: {Error}",
                        device.Name, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception deploying to {Device}", device.Name);
            }
        }
    }
}
