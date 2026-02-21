namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Types of attack patterns detected by analyzing correlated threat events.
/// </summary>
public enum PatternType
{
    ScanSweep = 0,
    BruteForce = 1,
    ExploitCampaign = 2,
    DDoS = 3
}
