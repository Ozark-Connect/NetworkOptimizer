namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Names what a rollout covers, for the alerts that announce one. A rollout can be console-only -
/// a Cloud Gateway's UniFi OS build waits while every device reports nothing pending - so copy
/// that leads with a device count reads as "0 devices across 0 waves" on a real rollout.
/// </summary>
public static class RolloutScopeCopy
{
    /// <summary>Whether this plan covers any console-level update.</summary>
    /// <param name="document">The plan document.</param>
    public static bool IncludesConsole(RolloutPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IncludesUniFiNetworkUpdate || document.IncludesUniFiOsUpdate;
    }

    /// <summary>
    /// Whether the console is upgraded as a device in its own right: a Cloud Gateway taking a UniFi
    /// OS build that no device step already covers. This is the rule the report adds its console row
    /// under, so anything counting devices has to ask it rather than count waves.
    /// </summary>
    /// <param name="document">The plan document.</param>
    public static bool ConsoleCountsAsDevice(RolloutPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IncludesUniFiOsUpdate
            && !string.IsNullOrWhiteSpace(document.ConsoleMac)
            && !document.Waves.SelectMany(w => w.Steps)
                .Any(s => string.Equals(s.Mac, document.ConsoleMac, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Devices the rollout upgrades, the console included where it is one of them. Matches the
    /// report's row count: a console-only rollout is one device, not none.
    /// </summary>
    /// <param name="document">The plan document.</param>
    public static int DeviceCount(RolloutPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Waves.Sum(w => w.Steps.Count) + (ConsoleCountsAsDevice(document) ? 1 : 0);
    }

    /// <summary>
    /// Everything the rollout upgrades: devices plus the console as one unit when it has any
    /// console-level work (Network app, UniFi OS, or both) and is not already in the device count.
    /// </summary>
    public static int TotalScope(RolloutPlanDocument document)
    {
        var devices = DeviceCount(document);
        if (!IncludesConsole(document)) return devices;
        var consoleInWaves = !string.IsNullOrWhiteSpace(document.ConsoleMac)
            && document.Waves.SelectMany(w => w.Steps)
                .Any(s => string.Equals(s.Mac, document.ConsoleMac, StringComparison.OrdinalIgnoreCase));
        var consoleAlreadyCounted = consoleInWaves || ConsoleCountsAsDevice(document);
        return devices + (consoleAlreadyCounted ? 0 : 1);
    }

    /// <summary>
    /// Total waves including console-level phases: wave 0 (Network app) and the final OS wave are
    /// each one wave when included.
    /// </summary>
    public static int TotalWaves(RolloutPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Waves.Count
            + (document.IncludesUniFiNetworkUpdate ? 1 : 0)
            + (document.IncludesUniFiOsUpdate ? 1 : 0);
    }

    /// <summary>
    /// The console surfaces a plan covers, or null when it covers none.
    /// </summary>
    /// <param name="document">The plan document.</param>
    public static string? ConsoleSurfaces(RolloutPlanDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return (document.IncludesUniFiNetworkUpdate, document.IncludesUniFiOsUpdate) switch
        {
            (true, true) => "the UniFi Network application and UniFi OS",
            (true, false) => "the UniFi Network application",
            (false, true) => "UniFi OS",
            _ => null,
        };
    }

    /// <summary>
    /// What is being upgraded, with waves where devices are involved: "3 devices across 2 waves",
    /// "the UniFi Network application and UniFi OS", or the two joined.
    /// </summary>
    /// <param name="document">The plan document.</param>
    /// <param name="devices">Devices the rollout will touch.</param>
    /// <param name="waves">Waves those devices are split into.</param>
    public static string Scope(RolloutPlanDocument document, int devices, int waves)
    {
        var console = ConsoleSurfaces(document);
        if (devices == 0 && console != null)
            return console;

        var deviceText = $"{devices} device{(devices == 1 ? "" : "s")} across {waves} wave{(waves == 1 ? "" : "s")}";
        return console == null ? deviceText : $"{deviceText}, plus {console}";
    }

    /// <summary>
    /// What is being upgraded, without waves, for copy that continues into a clause of its own.
    /// </summary>
    /// <param name="document">The plan document.</param>
    /// <param name="devices">Devices the rollout will touch.</param>
    public static string Subject(RolloutPlanDocument document, int devices)
    {
        var console = ConsoleSurfaces(document);
        if (devices == 0 && console != null)
            return console;

        var deviceText = $"{devices} device{(devices == 1 ? "" : "s")}";
        return console == null ? deviceText : $"{deviceText} and {console}";
    }

    /// <summary>Capitalizes a phrase that starts a sentence.</summary>
    /// <param name="phrase">The phrase.</param>
    public static string Sentence(string phrase) =>
        string.IsNullOrEmpty(phrase) ? phrase : char.ToUpperInvariant(phrase[0]) + phrase[1..];
}
