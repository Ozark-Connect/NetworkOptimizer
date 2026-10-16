namespace NetworkOptimizer.Threats.Models;

/// <summary>A device's contacts with one address on a public block list over a time range, allowed or blocked.</summary>
public sealed record PublicListContactSummary(string DeviceIp, string Address, string List, bool Connected, int Events, DateTime LastSeen);
