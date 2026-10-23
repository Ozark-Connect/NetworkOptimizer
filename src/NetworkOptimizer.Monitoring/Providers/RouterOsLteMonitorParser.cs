using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Pure-function translator from MikroTik RouterOS console output into
/// <see cref="CellularModemStats"/>. Reads <c>/interface lte monitor ... once</c> and
/// <c>/system resource print</c>. Kept separate from the SSH provider so it can be unit-tested.
/// </summary>
/// <remarks>
/// The console prints one <c>key: value</c> per line, padded with spaces, and attaches units to
/// values (<c>-84dBm</c>, <c>-9dB</c>). One line packs several fields:
/// <c>primary-band: B20@10Mhz earfcn: 6200 phy-cellid: 11</c>. Identifiers (imei, imsi, iccid,
/// subscriber-number) are never read.
/// </remarks>
[VendorSpecific("MikroTik", "RouterOS /interface lte monitor console layout, unit suffixes, and packed band lines")]
public static class RouterOsLteMonitorParser
{
    private static readonly Regex KeyValueLine = new(@"^\s*([a-z][a-z0-9-]*):\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly Regex BandValue = new(
        @"^(?<band>[bn])(?<num>\d+)(?:@(?<bw>\d+(?:\.\d+)?)\s*mhz)?(?:\s+earfcn:\s*(?<ch>\d+))?(?:\s+phy-cellid:\s*(?<pci>\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Quectel's own family prefixes (RG/RM 5G, EG/EC LTE). EM is left out: Sierra Wireless uses it too.
    private static readonly Regex QuectelModel = new(@"^(?:RG|RM)\d{3}|^(?:EG|EC)\d{2}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Split console output into fields. A line with no key (a wrapped multi-value field such as
    /// <c>ca-band</c>) adds a value to the key above it. A key printed twice keeps both values.
    /// </summary>
    public static Dictionary<string, List<string>> ParseFields(string? output)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output))
            return fields;

        List<string>? last = null;
        foreach (var rawLine in AnsiEscape.Replace(output, "").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Trim().Length == 0)
            {
                last = null;
                continue;
            }

            var m = KeyValueLine.Match(line);
            if (m.Success)
            {
                var key = m.Groups[1].Value;
                if (!fields.TryGetValue(key, out last))
                {
                    last = new List<string>();
                    fields[key] = last;
                }
                last.Add(m.Groups[2].Value.Trim());
            }
            else
            {
                last?.Add(line.Trim());
            }
        }
        return fields;
    }

    /// <summary>
    /// Whether the output is RouterOS's column form for several interfaces at once, which this
    /// parser cannot read. Unconfirmed layout: the column form is expected to carry a <c>name</c>
    /// row with one value per interface.
    /// </summary>
    public static bool HasMultipleInterfaces(Dictionary<string, List<string>> fields) =>
        Get(fields, "name") is { } name && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1;

    /// <summary>
    /// Translate <c>/interface lte monitor ... once</c> output into stats. Null when the output
    /// carries no RSRP for either radio.
    /// </summary>
    public static CellularModemStats? Parse(string? monitorOutput, string host, string name, string modemModel)
    {
        var fields = ParseFields(monitorOutput);

        // The NR fields and the SA routing below follow the RouterOS CLI reference and a forum NSA
        // paste. Unconfirmed against this provider's own output: the only trace so far is LTE.
        var isSa = IsStandalone(Get(fields, "data-class"));
        var primarySignal = BuildSignal(fields, "rsrp", "rsrq", "rssi", "sinr");
        var nrSignal = BuildSignal(fields, "nr-rsrp", "nr-rsrq", "nr-rssi", "nr-sinr");

        var stats = new CellularModemStats
        {
            Timestamp = DateTime.UtcNow,
            ModemHost = host,
            ModemName = name,
            ModemModel = modemModel,
            Carrier = Get(fields, "current-operator") ?? "",
            IsRoaming = IsTrue(Get(fields, "roaming")),
            RegistrationState = RegistrationState(fields),
            ModuleModel = Get(fields, "model"),
            SoftwareVersion = Get(fields, "revision"),
        };
        stats.ModuleVendor = Get(fields, "manufacturer") ?? InferModuleVendor(stats.ModuleModel);

        if (isSa)
        {
            stats.Nr5g = nrSignal ?? primarySignal;
        }
        else
        {
            stats.Lte = primarySignal;
            stats.Nr5g = nrSignal;
        }

        var servingSignal = isSa ? stats.Nr5g : stats.Lte;
        if (servingSignal == null && stats.Nr5g == null)
            return null;

        var primary = Get(fields, "primary-band") is { } pb ? ParseBand(pb) : null;
        stats.ActiveBand = primary?.Band;

        // An NSA session carries its NR leg in ca-band; show that band, as the other providers do.
        if (!isSa && stats.Nr5g != null && fields.TryGetValue("ca-band", out var caBands))
        {
            var nrLeg = caBands.Select(ParseBand).FirstOrDefault(b => b?.Band.RadioInterface == "nr5g");
            if (nrLeg != null)
                stats.ActiveBand = nrLeg.Value.Band;
        }

        var pci = GetInt(fields, "phy-cellid") ?? primary?.Pci;
        var earfcn = GetInt(fields, "earfcn") ?? (primary?.Band.Channel is > 0 ? (int?)primary.Value.Band.Channel : null);
        var cellId = Get(fields, "current-cellid");
        stats.ServingCell = new CellInfo
        {
            IsServing = true,
            PhysicalCellId = pci ?? 0,
            GlobalCellId = cellId != null && cellId.All(char.IsAsciiDigit) ? cellId : null,
            Earfcn = earfcn,
            BandDescription = primary?.Band.BandName,
            Signal = servingSignal ?? stats.Nr5g,
        };

        return stats;
    }

    /// <summary>
    /// Parse a band value such as <c>B20@10Mhz earfcn: 6200 phy-cellid: 11</c>. <c>B</c> is an
    /// LTE band and <c>n</c> an NR band. Null when the value names no band.
    /// </summary>
    public static (BandInfo Band, int? Pci)? ParseBand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var m = BandValue.Match(value.Trim());
        if (!m.Success)
            return null;

        var isNr = m.Groups["band"].Value.Equals("n", StringComparison.OrdinalIgnoreCase);
        var number = int.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture);
        int? bandwidth = null;
        if (m.Groups["bw"].Success &&
            double.TryParse(m.Groups["bw"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bw) &&
            bw == Math.Floor(bw) && bw > 0)
        {
            bandwidth = (int)bw;
        }

        var band = new BandInfo
        {
            RadioInterface = isNr ? "nr5g" : "lte",
            BandClass = isNr ? $"n{number}" : $"eutran-{number}",
            Channel = m.Groups["ch"].Success && int.TryParse(m.Groups["ch"].Value, CultureInfo.InvariantCulture, out var ch) ? ch : 0,
            BandwidthMhz = bandwidth,
        };
        int? pci = m.Groups["pci"].Success && int.TryParse(m.Groups["pci"].Value, CultureInfo.InvariantCulture, out var p) ? p : null;
        return (band, pci);
    }

    /// <summary>RouterOS version from <c>/system resource print</c>, without the channel ("7.24.5").</summary>
    public static string? ParseVersion(string? resourceOutput) =>
        Get(ParseFields(resourceOutput), "version")?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    /// <summary>Board name from <c>/system resource print</c> ("ATLGM").</summary>
    public static string? ParseBoardName(string? resourceOutput) => Get(ParseFields(resourceOutput), "board-name");

    /// <summary>
    /// The module maker, inferred from Quectel's own model prefixes when RouterOS does not print
    /// <c>manufacturer</c>. Null for any other model.
    /// </summary>
    public static string? InferModuleVendor(string? model) =>
        !string.IsNullOrWhiteSpace(model) && QuectelModel.IsMatch(model.Trim()) ? "Quectel" : null;

    private static string RegistrationState(Dictionary<string, List<string>> fields)
    {
        if (Get(fields, "registration-status") is { } registration)
            return registration;

        // RouterOS prints "running" (seen) or "connected" (manual examples) once the data session is up.
        var status = Get(fields, "status");
        return status is null ? ""
            : status.Equals("running", StringComparison.OrdinalIgnoreCase) ||
              status.Equals("connected", StringComparison.OrdinalIgnoreCase) ? "registered"
            : status;
    }

    private static bool IsStandalone(string? dataClass)
    {
        if (string.IsNullOrEmpty(dataClass))
            return false;
        var upper = dataClass.ToUpperInvariant();
        return (upper.Contains("5G", StringComparison.Ordinal) || upper.Contains("NR", StringComparison.Ordinal)) &&
               upper.Contains("SA", StringComparison.Ordinal) &&
               !upper.Contains("NSA", StringComparison.Ordinal);
    }

    /// <summary>Signal info with readings no radio can produce dropped. Null when there is no RSRP.</summary>
    private static SignalInfo? BuildSignal(Dictionary<string, List<string>> fields, string rsrp, string rsrq, string rssi, string sinr)
    {
        var rsrpValue = InRange(GetDouble(fields, rsrp), -140, -30);
        if (!rsrpValue.HasValue)
            return null;

        return new SignalInfo
        {
            Rsrp = rsrpValue,
            Rsrq = InRange(GetDouble(fields, rsrq), -43, 20),
            Rssi = InRange(GetDouble(fields, rssi), -120, -1),
            Snr = InRange(GetDouble(fields, sinr), -20, 60),
        };
    }

    private static double? InRange(double? value, double low, double high) =>
        value is { } v && v >= low && v <= high ? v : null;

    private static string? Get(Dictionary<string, List<string>> fields, string key) =>
        fields.TryGetValue(key, out var values) && values.Count > 0 && values[0].Length > 0 ? values[0] : null;

    private static double? GetDouble(Dictionary<string, List<string>> fields, string key)
    {
        var m = Get(fields, key) is { } s ? LeadingNumber.Match(s) : null;
        return m is { Success: true } &&
               double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int? GetInt(Dictionary<string, List<string>> fields, string key)
    {
        var s = Get(fields, key);
        return s != null && s.All(char.IsAsciiDigit) && int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool IsTrue(string? value) =>
        value != null && (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("true", StringComparison.OrdinalIgnoreCase));
}
