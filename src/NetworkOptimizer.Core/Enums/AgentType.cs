namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Represents the type of monitoring agent deployed on network segments.
/// </summary>
public enum AgentType
{
    /// <summary>
    /// Unknown or unspecified agent type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Linux-based monitoring agent for network performance testing.
    /// </summary>
    Linux = 1,

    /// <summary>
    /// Windows-based monitoring agent for network performance testing.
    /// </summary>
    Windows = 2,

    /// <summary>
    /// Docker container-based monitoring agent.
    /// </summary>
    Docker = 3,

    /// <summary>
    /// Raspberry Pi-based monitoring agent for IoT segments.
    /// </summary>
    RaspberryPi = 4,

    /// <summary>
    /// Cloud-based synthetic monitoring agent.
    /// </summary>
    CloudSynthetic = 5
}

/// <summary>
/// Extension methods for AgentType enum.
/// </summary>
public static class AgentTypeExtensions
{
    /// <summary>
    /// Gets the platform identifier for the agent type.
    /// </summary>
    public static string GetPlatform(this AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Linux => "linux-x64",
            AgentType.Windows => "win-x64",
            AgentType.Docker => "linux-docker",
            AgentType.RaspberryPi => "linux-arm64",
            AgentType.CloudSynthetic => "cloud",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Determines if the agent type supports hardware deployment.
    /// </summary>
    public static bool SupportsHardwareDeployment(this AgentType agentType)
    {
        return agentType switch
        {
            AgentType.Linux => true,
            AgentType.Windows => true,
            AgentType.Docker => true,
            AgentType.RaspberryPi => true,
            AgentType.CloudSynthetic => false,
            _ => false
        };
    }
}
