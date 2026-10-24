namespace NetworkOptimizer.Threats.Models;

/// <summary>A device's contacts with one address on a public block list over a time range, allowed or blocked.</summary>
public sealed record PublicListContactSummary(string DeviceIp, string Address, string List, bool Connected, int Events, DateTime LastSeen);

/// <summary>A device's connections to one listed address and port that the gateway dropped, over a time range.</summary>
public sealed record PublicListDropSummary(string DeviceIp, string Address, string Protocol, int Port, string List, int Drops, DateTime LastSeen);
