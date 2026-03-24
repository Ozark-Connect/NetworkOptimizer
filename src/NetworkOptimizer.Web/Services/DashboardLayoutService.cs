using System.Text.Json;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Dashboard card identifiers for the main content grid.
/// </summary>
public static class DashboardCards
{
    public const string StatsRow = "stats-row";
    public const string SecurityPosture = "security-posture";
    public const string SqmStatus = "sqm-status";
    public const string ThreatTrends = "threat-trends";
    public const string CellularStats = "cellular-stats";
    public const string SpeedTests = "speed-tests";
    public const string WiFiOptimizer = "wifi-optimizer";
    public const string RecentAlerts = "recent-alerts";
    public const string DeviceStatus = "device-status";

    /// <summary>All valid card IDs</summary>
    public static readonly string[] All =
    [
        StatsRow, SecurityPosture, SqmStatus, ThreatTrends, CellularStats,
        SpeedTests, WiFiOptimizer, RecentAlerts, DeviceStatus
    ];

    /// <summary>Default full-width cards</summary>
    public static readonly HashSet<string> DefaultFullWidth = new()
    {
        StatsRow, DeviceStatus
    };

    /// <summary>Display names for cards</summary>
    public static string GetDisplayName(string cardId) => cardId switch
    {
        StatsRow => "Quick Stats",
        SecurityPosture => "Security Posture",
        SqmStatus => "Adaptive SQM",
        ThreatTrends => "Threat Trends",
        CellularStats => "Cellular Stats",
        SpeedTests => "Speed Tests",
        WiFiOptimizer => "Wi-Fi Optimizer",
        RecentAlerts => "Recent Audit Issues",
        DeviceStatus => "Device Status",
        _ => cardId
    };
}

/// <summary>
/// Stat item identifiers for the stats row.
/// </summary>
public static class DashboardStatItems
{
    public const string TotalDevices = "total-devices";
    public const string SecurityScore = "security-score";
    public const string SqmStatus = "sqm-status";
    public const string ActiveAlerts = "active-alerts";
    public const string ThreatEvents = "threat-events";
    public const string WiFiHealth = "wifi-health";

    /// <summary>All valid stat item IDs</summary>
    public static readonly string[] All =
    [
        TotalDevices, SecurityScore, SqmStatus, ActiveAlerts, ThreatEvents, WiFiHealth
    ];

    /// <summary>Display names for stat items</summary>
    public static string GetDisplayName(string statId) => statId switch
    {
        TotalDevices => "Total Devices",
        SecurityScore => "Security Score",
        SqmStatus => "Adaptive SQM",
        ActiveAlerts => "Active Alerts",
        ThreatEvents => "Threat Events",
        WiFiHealth => "Wi-Fi Health",
        _ => statId
    };
}

/// <summary>
/// User-customizable dashboard layout configuration.
/// </summary>
public class DashboardLayout
{
    /// <summary>Ordered list of card configurations</summary>
    public List<DashboardCardConfig> Cards { get; set; } = new();

    /// <summary>Ordered list of visible stat item IDs within the stats row</summary>
    public List<string> StatItems { get; set; } = new();

    /// <summary>Stat items the user explicitly removed (so merge doesn't re-add them)</summary>
    public List<string> RemovedStatItems { get; set; } = new();
}

/// <summary>
/// Configuration for a single dashboard card.
/// </summary>
public class DashboardCardConfig
{
    public string Id { get; set; } = string.Empty;
    public bool Visible { get; set; } = true;
    public bool FullWidth { get; set; }

    /// <summary>Card IDs stacked below this card in the same grid cell</summary>
    public List<string> StackedCards { get; set; } = new();
}

/// <summary>
/// Manages dashboard layout preferences (card order, visibility, stat items).
/// </summary>
public class DashboardLayoutService
{
    private readonly ISystemSettingsService _settings;
    private readonly ILogger<DashboardLayoutService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public DashboardLayoutService(ISystemSettingsService settings, ILogger<DashboardLayoutService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Load the user's dashboard layout, or return the default.
    /// </summary>
    public async Task<DashboardLayout> GetLayoutAsync()
    {
        try
        {
            var json = await _settings.GetAsync(SystemSettingKeys.DashboardLayout);
            if (!string.IsNullOrEmpty(json))
            {
                var layout = JsonSerializer.Deserialize<DashboardLayout>(json, JsonOptions);
                if (layout != null)
                {
                    // Merge in any new cards/stats that were added since the layout was saved
                    MergeDefaults(layout);
                    return layout;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load dashboard layout, using defaults");
        }

        return GetDefaultLayout();
    }

    /// <summary>
    /// Save the user's dashboard layout.
    /// </summary>
    public async Task SaveLayoutAsync(DashboardLayout layout)
    {
        var json = JsonSerializer.Serialize(layout, JsonOptions);
        await _settings.SetAsync(SystemSettingKeys.DashboardLayout, json);
        _logger.LogDebug("Saved dashboard layout");
    }

    /// <summary>
    /// Reset to default layout.
    /// </summary>
    public async Task ResetLayoutAsync()
    {
        await _settings.SetAsync(SystemSettingKeys.DashboardLayout, null);
        _logger.LogDebug("Reset dashboard layout to defaults");
    }

    /// <summary>
    /// Returns the default dashboard layout matching the original hardcoded order.
    /// </summary>
    public static DashboardLayout GetDefaultLayout()
    {
        var cards = DashboardCards.All.Select(id => new DashboardCardConfig
        {
            Id = id,
            Visible = true,
            FullWidth = DashboardCards.DefaultFullWidth.Contains(id)
        }).ToList();

        // Default stack: SQM hosts Threat Trends
        var sqmCard = cards.Find(c => c.Id == DashboardCards.SqmStatus);
        if (sqmCard != null)
            sqmCard.StackedCards.Add(DashboardCards.ThreatTrends);

        return new DashboardLayout
        {
            Cards = cards,
            StatItems = DashboardStatItems.All.ToList()
        };
    }

    /// <summary>
    /// Ensure any newly added cards/stats appear in existing saved layouts.
    /// </summary>
    private static void MergeDefaults(DashboardLayout layout)
    {
        var existingCardIds = new HashSet<string>(layout.Cards.Select(c => c.Id));
        foreach (var cardId in DashboardCards.All)
        {
            if (!existingCardIds.Contains(cardId))
            {
                layout.Cards.Add(new DashboardCardConfig { Id = cardId, Visible = true });
            }
        }

        var existingStatIds = new HashSet<string>(layout.StatItems);
        var removedStatIds = new HashSet<string>(layout.RemovedStatItems);
        foreach (var statId in DashboardStatItems.All)
        {
            if (!existingStatIds.Contains(statId) && !removedStatIds.Contains(statId))
            {
                layout.StatItems.Add(statId);
            }
        }
    }
}
