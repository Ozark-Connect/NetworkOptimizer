namespace NetworkOptimizer.AgentProtocol;

/// <summary>Shared names for Change Control's agent-side revert.</summary>
public static class ConfigRevert
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "config-revert";

    /// <summary>Report states.</summary>
    public const string Armed = "armed";
    public const string Held = "held";
    public const string Reverted = "reverted";
    public const string RevertFailed = "revert_failed";

    /// <summary>The placeholder a create's new ID replaces in later calls.</summary>
    public const string RefPrefix = "@ref:";
}
