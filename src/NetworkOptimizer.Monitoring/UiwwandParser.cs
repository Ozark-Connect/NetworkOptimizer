using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Parses the JSON output from UniFi's uiwwand radio status command
/// (<c>ubus call uiwwand call '{"method":"get-radio-status","params":{}}'</c>)
/// into <see cref="CellularModemStats"/>.
///
/// This command is available on all UniFi cellular modems (U5G-Max, U5G Backup,
/// U-LTE) and returns a normalized view of signal, band, carrier, and carrier
/// aggregation data regardless of the underlying QMI/QRTR transport.
/// </summary>
public static class UiwwandParser
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Parse the full ubus JSON response into a <see cref="CellularModemStats"/>.
    /// Returns null if the JSON is malformed or missing the <c>result</c> object.
    /// </summary>
    /// <param name="json">Raw JSON output from the ubus call.</param>
    /// <param name="host">Modem host for diagnostics.</param>
    /// <param name="name">Modem friendly name.</param>
    /// <param name="model">Modem model string.</param>
    public static CellularModemStats? Parse(string json, string host, string name, string model)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result))
                return null;

            var stats = new CellularModemStats
            {
                ModemHost = host,
                ModemName = name,
                ModemModel = model,
                Timestamp = DateTime.UtcNow,
            };

            ParseSignal(result, stats);
            ParseCarrier(result, stats);
            ParseBand(result, stats);
            ParseCell(result, stats);

            return stats;
        }
    }

    /// <summary>
    /// Populate LTE and/or NR5G signal info from the uiwwand result.
    /// In 5G SA mode, only Nr5g is populated (no LTE anchor).
    /// In NSA mode, Lte gets the base fields, Nr5g gets the -nr fields.
    /// In LTE-only mode, only Lte is populated.
    /// </summary>
    private static void ParseSignal(JsonElement result, CellularModemStats stats)
    {
        var isSa = GetBool(result, "5g-sa-mode");
        var hasNrSignal = result.TryGetProperty("rsrp-nr", out _);
        var ratMode = GetString(result, "rat-mode-active") ?? "";

        // NR5G signal from -nr suffixed fields
        if (hasNrSignal)
        {
            stats.Nr5g = new SignalInfo
            {
                Rsrp = GetDouble(result, "rsrp-nr"),
                Rsrq = GetDouble(result, "rsrq-nr"),
                Snr = GetDouble(result, "snr-nr"),
            };
        }

        // LTE signal from base fields (skip in SA mode to avoid
        // DetermineNetworkMode misclassifying as NSA)
        if (!isSa)
        {
            var rsrp = GetDouble(result, "rsrp");
            if (rsrp.HasValue)
            {
                stats.Lte = new SignalInfo
                {
                    Rsrp = rsrp,
                    Rsrq = GetDouble(result, "rsrq"),
                    Rssi = GetDouble(result, "rssi"),
                    Snr = GetDouble(result, "snr"),
                };
            }
        }

        // If SA mode but no -nr fields, fall back to base fields for Nr5g
        if (isSa && stats.Nr5g == null)
        {
            var rsrp = GetDouble(result, "rsrp");
            if (rsrp.HasValue)
            {
                stats.Nr5g = new SignalInfo
                {
                    Rsrp = rsrp,
                    Rsrq = GetDouble(result, "rsrq"),
                    Rssi = GetDouble(result, "rssi"),
                    Snr = GetDouble(result, "snr"),
                };
            }
        }
    }

    /// <summary>
    /// Populate carrier, registration state, and roaming info.
    /// </summary>
    private static void ParseCarrier(JsonElement result, CellularModemStats stats)
    {
        stats.Carrier = GetString(result, "registered-spn") ?? "";
        stats.CarrierMcc = GetInt(result, "mcc")?.ToString() ?? "";
        stats.CarrierMnc = GetInt(result, "mnc")?.ToString() ?? "";
        stats.IsRoaming = GetBool(result, "roaming");

        var hasCoverage = GetBool(result, "has-coverage");
        var regState = GetInt(result, "registration-state");
        stats.RegistrationState = hasCoverage ? "registered" : (regState.HasValue ? $"state-{regState}" : "");
    }

    /// <summary>
    /// Populate active band info from band-class, channel, and CA arrays.
    /// </summary>
    private static void ParseBand(JsonElement result, CellularModemStats stats)
    {
        var bandClass = GetString(result, "band-class");
        if (string.IsNullOrEmpty(bandClass))
            return;

        var ratMode = GetString(result, "rat-mode-active") ?? "";

        stats.ActiveBand = new BandInfo
        {
            RadioInterface = ratMode.Equals("5G", StringComparison.OrdinalIgnoreCase) ? "5gnr" : "lte",
            BandClass = bandClass,
            Channel = GetInt(result, "channel") ?? 0,
        };

        // Extract bandwidth from carrier aggregation arrays
        var bwMhz = GetPrimaryBandwidthMhz(result, "ca-nr") ?? GetPrimaryBandwidthMhz(result, "ca-lte");
        if (bwMhz.HasValue)
            stats.ActiveBand.BandwidthMhz = bwMhz.Value;
    }

    /// <summary>
    /// Populate serving cell info from cell-id and pci fields when available.
    /// </summary>
    private static void ParseCell(JsonElement result, CellularModemStats stats)
    {
        var cellId = GetInt(result, "cell-id");
        var pci = GetInt(result, "pci");

        if (!cellId.HasValue && !pci.HasValue)
            return;

        stats.ServingCell = new CellInfo
        {
            IsServing = true,
            GlobalCellId = cellId?.ToString(),
            PhysicalCellId = pci ?? 0,
            Earfcn = GetInt(result, "channel"),
        };
    }

    /// <summary>
    /// Find the primary carrier in a CA array and return its downlink bandwidth in MHz.
    /// </summary>
    private static int? GetPrimaryBandwidthMhz(JsonElement result, string arrayName)
    {
        if (!result.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in array.EnumerateArray())
        {
            var isPrimary = false;
            if (entry.TryGetProperty("primary", out var p))
                isPrimary = p.ValueKind == JsonValueKind.True;

            if (!isPrimary) continue;

            if (entry.TryGetProperty("dl-bw-mhz", out var bw))
            {
                if (bw.ValueKind == JsonValueKind.Number)
                    return (int)bw.GetDouble();
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetDouble(),
            JsonValueKind.String when double.TryParse(prop.GetString(), out var d) => d,
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number => prop.GetInt32(),
            JsonValueKind.String when int.TryParse(prop.GetString(), out var i) => i,
            _ => null,
        };
    }

    private static bool GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false,
        };
    }
}
