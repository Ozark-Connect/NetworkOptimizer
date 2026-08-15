using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Per-device downtime estimates and offline budgets. Seeds come from measured upgrade
/// cycles (research 2026-08-14: 20 clean events across 4 sites); once a model has enough
/// site-recorded samples, its learned median wins over the seed.
/// </summary>
public class FirmwareTimingEstimator
{
    /// <summary>Learned timings below this sample count stay advisory; seeds win.</summary>
    public const int MinLearnedSamples = 3;

    /// <summary>Declare-stuck threshold for everything but Cloud Gateways.</summary>
    public const int DefaultOfflineBudgetSeconds = 900;

    /// <summary>Cloud Gateway UniFi OS cycles: download + install + console apps returning.</summary>
    public const int CloudGatewayOfflineBudgetSeconds = 1800;

    private readonly IReadOnlyDictionary<string, FirmwareModelTiming> _learned;

    public FirmwareTimingEstimator(IEnumerable<FirmwareModelTiming>? learnedTimings = null)
    {
        _learned = (learnedTimings ?? []).ToDictionary(t => t.Model, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Seed p50 downtime per class, in seconds.</summary>
    public static int SeedDowntimeSeconds(FirmwareDeviceClass cls) => cls switch
    {
        FirmwareDeviceClass.AccessPoint => 240,
        FirmwareDeviceClass.OlderAccessPoint => 420,
        FirmwareDeviceClass.Switch => 480,
        FirmwareDeviceClass.GatewayNetworkOnly => 300,
        FirmwareDeviceClass.CloudGatewayUniFiOs => 1080,
        _ => 480,
    };

    /// <summary>Offline budget (the "declare stuck" point) per class, in seconds.</summary>
    public static int OfflineBudgetSeconds(FirmwareDeviceClass cls) =>
        cls == FirmwareDeviceClass.CloudGatewayUniFiOs ? CloudGatewayOfflineBudgetSeconds : DefaultOfflineBudgetSeconds;

    /// <summary>
    /// Estimated downtime for a model: the site's learned median once it has
    /// <see cref="MinLearnedSamples"/> samples, the class seed otherwise.
    /// </summary>
    public int EstimateDowntimeSeconds(string model, FirmwareDeviceClass cls)
    {
        if (!string.IsNullOrEmpty(model) &&
            _learned.TryGetValue(model, out var t) &&
            t.SampleCount >= MinLearnedSamples &&
            t.MedianDowntimeSeconds > 0)
        {
            return t.MedianDowntimeSeconds;
        }
        return SeedDowntimeSeconds(cls);
    }

    public int EstimateDowntimeSeconds(PlannerDevice device) =>
        EstimateDowntimeSeconds(device.Model, Classify(device));

    public static FirmwareDeviceClass Classify(PlannerDevice device) =>
        Classify(device.Type, device.Model, device.DisplayModel);

    /// <summary>
    /// Class from type + model naming. Unknown gateways default to Cloud Gateway - the
    /// longer budget is the safe direction when the console goes dark mid-cycle.
    /// </summary>
    public static FirmwareDeviceClass Classify(DeviceType type, string model, string displayModel)
    {
        if (type == DeviceType.Gateway)
        {
            return UniFiProductDatabase.IsNetworkOnlyGateway(model, displayModel)
                ? FirmwareDeviceClass.GatewayNetworkOnly
                : FirmwareDeviceClass.CloudGatewayUniFiOs;
        }

        if (type == DeviceType.AccessPoint)
        {
            return IsOlderGenerationAp(model, displayModel)
                ? FirmwareDeviceClass.OlderAccessPoint
                : FirmwareDeviceClass.AccessPoint;
        }

        return FirmwareDeviceClass.Switch;
    }

    // Display-name markers for pre-U6 AP generations (WiFi 5 and earlier). Raw SKU codes
    // are ambiguous across generations (U7PG2 is the AC Pro), so match the friendly name.
    private static readonly string[] OlderApMarkers =
    [
        "UAP", "AC Pro", "AC Lite", "AC LR", "AC Mesh", "AC EDU", "AC SHD", "AC HD",
        "AC-", "nanoHD", "FlexHD", "BeaconHD", "IW HD", "IW-HD", "XG AP", "BaseStation",
    ];

    private static bool IsOlderGenerationAp(string model, string displayModel)
    {
        foreach (var marker in OlderApMarkers)
        {
            if (Contains(displayModel, marker)) return true;
        }
        return Contains(model, "UAP") && !Contains(model, "UAP6");
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
