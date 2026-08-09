using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// The outcome of a modem poll: the stats, or the reason there are none.
/// <para>
/// A bare null told the caller only that nothing came back, which is what the UI then had to
/// say. The reason travels with the failure so a rejected password and an unreachable device
/// read differently to the person looking at them.
/// </para>
/// </summary>
public sealed record ModemPollResult
{
    /// <summary>The polled stats, or null when the poll failed.</summary>
    public CellularModemStats? Stats { get; init; }

    /// <summary>
    /// One short user-facing line saying why there are no stats. Null on success.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>Whether the poll produced stats.</summary>
    public bool Success => Stats != null;

    /// <summary>A successful poll.</summary>
    public static ModemPollResult Ok(CellularModemStats stats) => new() { Stats = stats };

    /// <summary>A failed poll, with the reason to show the user.</summary>
    public static ModemPollResult Failed(string reason) => new() { FailureReason = reason };
}
