namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Source identity used to evaluate a cellular modem's firewall access.
/// </summary>
public sealed record CellularModemIdentity(string? Ip, string? Mac);
