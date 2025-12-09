using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for deploying and managing monitoring agents across network segments.
/// Provides methods for agent lifecycle management and configuration.
/// </summary>
public interface IAgentDeployer
{
    /// <summary>
    /// Deploys a new monitoring agent to a target host.
    /// </summary>
    /// <param name="deployment">Agent deployment configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deployment result with agent status.</returns>
    Task<AgentDeploymentResult> DeployAgentAsync(AgentDeployment deployment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing agent to a new version.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="targetVersion">Target version to update to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the update was successful.</returns>
    Task<bool> UpdateAgentAsync(string agentId, string targetVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a monitoring agent from a host.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the agent was successfully removed.</returns>
    Task<bool> RemoveAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current status of all deployed agents.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of agent statuses.</returns>
    Task<List<AgentStatus>> GetAllAgentStatusesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the status of a specific agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Agent status, or null if not found.</returns>
    Task<AgentStatus?> GetAgentStatusAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the configuration of a deployed agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="configuration">New agent configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the configuration was successfully updated.</returns>
    Task<bool> UpdateAgentConfigurationAsync(string agentId, AgentConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a stopped agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the agent was successfully started.</returns>
    Task<bool> StartAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a running agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the agent was successfully stopped.</returns>
    Task<bool> StopAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts an agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the agent was successfully restarted.</returns>
    Task<bool> RestartAgentAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers potential hosts for agent deployment in a network segment.
    /// </summary>
    /// <param name="networkSegment">Network segment to scan (CIDR notation).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered potential agent hosts.</returns>
    Task<List<AgentHost>> DiscoverPotentialHostsAsync(string networkSegment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a host meets the requirements for agent deployment.
    /// </summary>
    /// <param name="host">Host to validate.</param>
    /// <param name="agentType">Type of agent to deploy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with any issues found.</returns>
    Task<HostValidationResult> ValidateHostAsync(AgentHost host, AgentType agentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves logs from a specific agent.
    /// </summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="lines">Number of recent log lines to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of log entries.</returns>
    Task<List<AgentLog>> GetAgentLogsAsync(string agentId, int lines = 100, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents agent deployment configuration.
/// </summary>
public class AgentDeployment
{
    /// <summary>
    /// Target host for deployment.
    /// </summary>
    public AgentHost Host { get; set; } = new();

    /// <summary>
    /// Type of agent to deploy.
    /// </summary>
    public AgentType AgentType { get; set; } = AgentType.Unknown;

    /// <summary>
    /// Agent version to deploy.
    /// </summary>
    public string Version { get; set; } = "latest";

    /// <summary>
    /// Agent name/identifier.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Network segment where the agent will be deployed.
    /// </summary>
    public string NetworkSegment { get; set; } = string.Empty;

    /// <summary>
    /// VLAN ID where the agent will be deployed.
    /// </summary>
    public int? VlanId { get; set; }

    /// <summary>
    /// Initial agent configuration.
    /// </summary>
    public AgentConfiguration Configuration { get; set; } = new();

    /// <summary>
    /// SSH credentials for deployment (if applicable).
    /// </summary>
    public SshCredentials? SshCredentials { get; set; }

    /// <summary>
    /// Additional deployment parameters.
    /// </summary>
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Represents a potential or actual host for agent deployment.
/// </summary>
public class AgentHost
{
    /// <summary>
    /// Hostname or IP address.
    /// </summary>
    public string HostnameOrIp { get; set; } = string.Empty;

    /// <summary>
    /// Operating system type.
    /// </summary>
    public string OperatingSystem { get; set; } = string.Empty;

    /// <summary>
    /// OS version.
    /// </summary>
    public string OsVersion { get; set; } = string.Empty;

    /// <summary>
    /// CPU architecture (x64, arm64, etc.).
    /// </summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>
    /// Available memory in MB.
    /// </summary>
    public long AvailableMemoryMb { get; set; }

    /// <summary>
    /// Available disk space in MB.
    /// </summary>
    public long AvailableDiskMb { get; set; }

    /// <summary>
    /// Indicates whether SSH is available.
    /// </summary>
    public bool SshAvailable { get; set; }

    /// <summary>
    /// SSH port number.
    /// </summary>
    public int SshPort { get; set; } = 22;

    /// <summary>
    /// Additional host properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
/// Represents SSH credentials for deployment.
/// </summary>
public class SshCredentials
{
    /// <summary>
    /// SSH username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// SSH password (if using password authentication).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// SSH private key (if using key-based authentication).
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// Passphrase for the private key (if applicable).
    /// </summary>
    public string? KeyPassphrase { get; set; }
}

/// <summary>
/// Represents the result of an agent deployment operation.
/// </summary>
public class AgentDeploymentResult
{
    /// <summary>
    /// Indicates whether the deployment was successful.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Agent identifier (if deployment was successful).
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Agent status (if deployment was successful).
    /// </summary>
    public AgentStatus? AgentStatus { get; set; }

    /// <summary>
    /// Error message (if deployment failed).
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Deployment logs and output.
    /// </summary>
    public List<string> Logs { get; set; } = new();

    /// <summary>
    /// Duration of the deployment process.
    /// </summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Represents the result of host validation.
/// </summary>
public class HostValidationResult
{
    /// <summary>
    /// Indicates whether the host is valid for deployment.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation issues found.
    /// </summary>
    public List<string> Issues { get; set; } = new();

    /// <summary>
    /// List of warnings (non-blocking issues).
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Recommended agent type for the host.
    /// </summary>
    public AgentType? RecommendedAgentType { get; set; }
}
