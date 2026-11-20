using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The links that open one speed test result, expanded, on its page. Shared by the WAN, LAN, and Client speed
/// test alerts and the Dashboard's speed test cards, so a result opens the same way from each.
/// </summary>
public static class SpeedTestLinks
{
    /// <summary>WAN Speed Test, narrowed to the result's WAN, with the result open.</summary>
    public static string WanResult(Iperf3Result result) => $"/wan-speedtest{WanFilter(result)}result={result.Id}";

    /// <summary>LAN Speed Test with the result open.</summary>
    public static string LanResult(int resultId) => $"/speedtest?result={resultId}";

    /// <summary>Client Speed Test with the result open.</summary>
    public static string ClientResult(int resultId) => $"/client-speedtest?result={resultId}";

    // The page filters on the WAN key, which is what WanNetworkGroup already holds ("WAN",
    // "WAN2"). Without it a multi-WAN site opens on whichever WAN it was left on, which is
    // not the one the link is about. Same filter the ISP Health hero link passes.
    //
    // A bonded test records "WAN+WAN2", which becomes the comma list the analysis charts
    // already read. WAN Speed Test understands a single key today and ignores a list, but
    // alert URLs are frozen into history when the alert is raised: a row written without
    // the filter never gains one, while a row carrying it starts working the day that page
    // learns to read it.
    private static string WanFilter(Iperf3Result result) => string.IsNullOrWhiteSpace(result.WanNetworkGroup)
        ? "?"
        : $"?wan={Uri.EscapeDataString(string.Join(",", result.WanNetworkGroup.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries)))}&";
}
