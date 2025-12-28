namespace NetworkOptimizer.Agents.Models;

/// <summary>
/// Result of an agent deployment operation
/// </summary>
public class DeploymentResult
{
    /// <summary>
    /// Whether the deployment was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Agent ID that was deployed
    /// </summary>
    public required string AgentId { get; set; }

    /// <summary>
    /// Device name
    /// </summary>
    public required string DeviceName { get; set; }

    /// <summary>
    /// Agent type deployed
    /// </summary>
    public AgentType AgentType { get; set; }

    /// <summary>
    /// Deployment timestamp
    /// </summary>
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Success or error message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Detailed deployment steps and their results
    /// </summary>
    public List<DeploymentStep> Steps { get; set; } = new();

    /// <summary>
    /// Files that were deployed
    /// </summary>
    public List<string> DeployedFiles { get; set; } = new();

    /// <summary>
    /// Verification results
    /// </summary>
    public VerificationResult? Verification { get; set; }

    /// <summary>
    /// Creates a successful deployment result
    /// </summary>
    public static DeploymentResult CreateSuccess(string agentId, string deviceName, AgentType agentType, string message)
    {
        return new DeploymentResult
        {
            Success = true,
            AgentId = agentId,
            DeviceName = deviceName,
            AgentType = agentType,
            Message = message
        };
    }

    /// <summary>
    /// Creates a failed deployment result
    /// </summary>
    public static DeploymentResult CreateFailure(string agentId, string deviceName, AgentType agentType, string message)
    {
        return new DeploymentResult
        {
            Success = false,
            AgentId = agentId,
            DeviceName = deviceName,
            AgentType = agentType,
            Message = message
        };
    }
}

/// <summary>
/// Individual deployment step
/// </summary>
public class DeploymentStep
{
    /// <summary>
    /// Step name
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Whether the step succeeded
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Step message or error
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When the step was executed
    /// </summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Step duration in milliseconds
    /// </summary>
    public long DurationMs { get; set; }
}

/// <summary>
/// Post-deployment verification results
/// </summary>
public class VerificationResult
{
    /// <summary>
    /// Whether verification passed
    /// </summary>
    public bool Passed { get; set; }

    /// <summary>
    /// Files verified as present
    /// </summary>
    public List<string> VerifiedFiles { get; set; } = new();

    /// <summary>
    /// Service status (if applicable)
    /// </summary>
    public string? ServiceStatus { get; set; }

    /// <summary>
    /// Agent process is running
    /// </summary>
    public bool AgentRunning { get; set; }

    /// <summary>
    /// Verification messages
    /// </summary>
    public List<string> Messages { get; set; } = new();
}
