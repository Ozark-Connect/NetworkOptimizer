namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Narrows LAN speed test history. Both parts are optional; an empty filter matches every LAN result.
/// </summary>
/// <param name="Search">Partial, case-insensitive match on device host, device name, client MAC, or any
/// hop's name, MAC, or IP in the stored path (the same fields as <c>SpeedTestFilterHelper.MatchesFilter</c>).</param>
/// <param name="DeviceHost">Exact, case-insensitive device host.</param>
public sealed record SpeedTestHistoryFilter(string? Search = null, string? DeviceHost = null)
{
    /// <summary>The search text, trimmed and lowercased, or null when there is none.</summary>
    public string? NormalizedSearch => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim().ToLowerInvariant();

    /// <summary>True when the filter narrows nothing.</summary>
    public bool IsEmpty => NormalizedSearch == null && string.IsNullOrEmpty(DeviceHost);
}

/// <summary>
/// The WAN a stored WAN speed test result is assigned to, as WAN Speed Test groups its series, and whether
/// that result succeeded. Distinct on all three, so the set stays a handful of rows.
/// </summary>
public sealed record WanResultSource(string? WanNetworkGroup, string? WanName, bool Success);

/// <summary>
/// A cheap fingerprint of a set of speed test results. Equal stamps mean nothing was added or deleted.
/// </summary>
/// <param name="Count">Rows in the set.</param>
/// <param name="MaxId">The highest Id, 0 when empty.</param>
/// <param name="NewestTestTime">The newest test time, <see cref="DateTime.MinValue"/> when empty.</param>
public sealed record SpeedTestResultsStamp(int Count, int MaxId, DateTime NewestTestTime)
{
    /// <summary>The stamp of an empty set.</summary>
    public static SpeedTestResultsStamp None { get; } = new(0, 0, DateTime.MinValue);
}

/// <summary>
/// One page of speed test history, newest first.
/// </summary>
/// <param name="Items">The page's results, without the raw iperf3 JSON.</param>
/// <param name="Page">The 1-based page returned, clamped to the last page when the request ran past it.</param>
/// <param name="Total">Results matching the filter.</param>
/// <param name="TotalUnfiltered">All LAN results, whatever the filter.</param>
public sealed record SpeedTestHistoryPage(List<Iperf3Result> Items, int Page, int Total, int TotalUnfiltered)
{
    /// <summary>An empty first page.</summary>
    public static SpeedTestHistoryPage Empty => new([], 1, 0, 0);
}
