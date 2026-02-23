namespace NetworkOptimizer.Threats.Models;

/// <summary>
/// Whether the IPS blocked the threat or only detected it.
/// </summary>
public enum ThreatAction
{
    Blocked = 0,
    Detected = 1
}
