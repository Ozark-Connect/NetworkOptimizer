namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Lockheed Martin Cyber Kill Chain stages, simplified for network IPS context.
/// </summary>
public enum KillChainStage
{
    Reconnaissance = 0,
    AttemptedExploitation = 1,
    ActiveExploitation = 2,
    PostExploitation = 3,
    Monitored = 4
}

public static class KillChainStageExtensions
{
    public static string ToDisplayString(this KillChainStage stage) => stage switch
    {
        KillChainStage.Reconnaissance => "Reconnaissance",
        KillChainStage.AttemptedExploitation => "Attempted Exploitation",
        KillChainStage.ActiveExploitation => "Active Exploitation",
        KillChainStage.PostExploitation => "Post-Exploitation",
        KillChainStage.Monitored => "Monitored",
        _ => stage.ToString()
    };
}
