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
