using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Pure-function translator from the Zyxel DAL <c>cellwan_status</c> object (NR7101/NR7102,
/// NR730x, NR5103, FWA505 and other ZCFG-based 5G/LTE CPEs) into
/// <see cref="CellularModemStats"/>. Kept separate from <c>ZyxelNrProvider</c> so it can be
/// unit-tested without an HTTP transport or the session encryption.
/// </summary>
/// <remarks>
/// Field names and "not available" sentinels come from Zyxel's GPL
/// <c>zcfg_fe_dal_cellwan_status.c</c>:
/// <list type="bullet">
///   <item><c>INTF_*</c> is the primary serving cell: the LTE anchor on LTE and NSA, the NR cell on SA.</item>
///   <item><c>NSA_*</c> is the NR leg of an EN-DC session. Older builds name its PCI <c>NSA_PCI</c>, newer <c>NSA_PhyCellID</c>.</item>
///   <item>Unmeasured values read RSRP -140, RSRQ -240, RSSI -120, SINR -20, and -1 for cell ID and PCI.</item>
///   <item>Bandwidth is a 3GPP index (0-5 = 1.4, 3, 5, 10, 15, 20 MHz) on older builds and <c>"60M"</c> on newer.</item>
///   <item>Bands read <c>LTE_BC7</c> or <c>B7</c> for LTE and <c>N78</c> for NR, with an optional carrier-added suffix.</item>
/// </list>
/// </remarks>
[VendorSpecific("Zyxel", "DAL cellwan_status field names, sentinels, and band/bandwidth encodings")]
public static class ZyxelCellwanParser
{
    /// <summary>The DAL result string for a successful call.</summary>
    public const string SuccessResult = "ZCFG_SUCCESS";

    private static readonly int[] BandwidthIndexMhz = { 0, 3, 5, 10, 15, 20 };

    /// <summary>
    /// Read the first entry of a DAL response's <c>Object</c> array. Returns false when the
    /// call did not succeed or carries no object.
    /// </summary>
    public static bool TryGetFirstObject(JsonElement response, out JsonElement obj)
    {
        obj = default;
        if (response.ValueKind != JsonValueKind.Object)
            return false;

        if (response.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.String &&
            result.GetString() != SuccessResult)
            return false;

        if (!response.TryGetProperty("Object", out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() == 0 ||
            arr[0].ValueKind != JsonValueKind.Object)
            return false;

        obj = arr[0];
        return true;
    }

    /// <summary>
    /// Translate a <c>cellwan_status</c> object into stats. <paramref name="deviceInfo"/> is the
    /// <c>DeviceInfo</c> object from the <c>status</c> DAL call, when it was read.
    /// </summary>
    public static CellularModemStats Parse(JsonElement cellwan, JsonElement? deviceInfo, ModemPollContext context)
    {
        var accessTech = TryGetString(cellwan, "INTF_Current_Access_Technology") ?? "";
        var (carrier, plmn) = ParseNetworkInUse(TryGetString(cellwan, "INTF_Network_In_Use"), accessTech);

        var stats = new CellularModemStats
        {
            Timestamp = DateTime.UtcNow,
            ModemHost = context.ConfiguredHost ?? context.Host,
            ModemName = context.Name,
            // ProductClass is the Zyxel model; ModelName can be a carrier's rebrand ("5GEE Router").
            ModemModel = TryGetString(deviceInfo, "ProductClass")
                         ?? TryGetString(deviceInfo, "ModelName")
                         ?? context.ModemType,
            SoftwareVersion = TryGetString(deviceInfo, "SoftwareVersion"),
            Carrier = carrier ?? "",
        };

        var status = TryGetString(cellwan, "INTF_Status");
        stats.RegistrationState = status == null
            ? ""
            : string.Equals(status, "Up", StringComparison.OrdinalIgnoreCase) ? "registered" : status;

        var mcc = TryGetString(cellwan, "NSA_MCC");
        var mnc = TryGetString(cellwan, "NSA_MNC");
        if (IsDigits(mcc, 3, 3) && IsDigits(mnc, 2, 3))
        {
            stats.CarrierMcc = mcc!;
            stats.CarrierMnc = mnc!;
        }
        else if (plmn != null)
        {
            stats.CarrierMcc = plmn[..3];
            stats.CarrierMnc = plmn[3..];
        }
        var plmnDisplay = string.IsNullOrEmpty(stats.CarrierMcc) ? null : stats.CarrierMcc + stats.CarrierMnc;

        var isSa = IsStandalone(accessTech);
        var primarySignal = BuildSignal(cellwan, "INTF_RSRP", "INTF_RSRQ", "INTF_RSSI", "INTF_SINR");
        var primaryBand = ParseBand(TryGetString(cellwan, "INTF_Current_Band"));
        var primaryRfcn = TryGetInt(cellwan, "INTF_RFCN");
        var primaryBandwidth = ParseBandwidthMhz(cellwan, "INTF_Downlink_Bandwidth");

        if (isSa)
        {
            stats.Nr5g = primarySignal;
        }
        else
        {
            stats.Lte = primarySignal;
            if (TryGetBool(cellwan, "NSA_Enable") != false)
                stats.Nr5g = BuildSignal(cellwan, "NSA_RSRP", "NSA_RSRQ", "NSA_RSSI", "NSA_SINR");
        }

        if (stats.Nr5g != null && !isSa)
        {
            stats.ActiveBand = BuildBand(
                ParseBand(TryGetString(cellwan, "NSA_Band")),
                TryGetInt(cellwan, "NSA_RFCN"),
                ParseBandwidthMhz(cellwan, "NSA_DownlinkBandwidth") ?? ParseBandwidthMhz(cellwan, "NSA_DL_BW"));
        }
        stats.ActiveBand ??= BuildBand(primaryBand, primaryRfcn, primaryBandwidth);

        var pci = ValidPci(TryGetInt(cellwan, "INTF_PhyCell_ID"));
        var cellId = TryGetLong(cellwan, "INTF_Cell_ID");
        var tac = TryGetLong(cellwan, "INTF_TAC");
        if (primarySignal != null || pci.HasValue || cellId is > 0)
        {
            stats.ServingCell = new CellInfo
            {
                IsServing = true,
                PhysicalCellId = pci ?? 0,
                GlobalCellId = cellId is > 0 ? cellId.Value.ToString(CultureInfo.InvariantCulture) : null,
                Tac = tac is > 0 ? tac.Value.ToString(CultureInfo.InvariantCulture) : null,
                Earfcn = primaryRfcn is > 0 ? primaryRfcn : null,
                BandDescription = primaryBand == null ? null : new BandInfo { BandClass = primaryBand.Value.BandClass }.BandName,
                Plmn = plmnDisplay,
                Signal = primarySignal,
            };
        }

        stats.NeighborCells = ParseNeighbors(cellwan);
        return stats;
    }

    /// <summary>
    /// Split <c>INTF_Network_In_Use</c> ("Current_Example Mobile_NR5G-NSA_00101") into the operator
    /// name and the PLMN. Either is null when the string does not carry it.
    /// </summary>
    public static (string? Carrier, string? Plmn) ParseNetworkInUse(string? networkInUse, string accessTech)
    {
        if (string.IsNullOrWhiteSpace(networkInUse))
            return (null, null);

        var rest = networkInUse.Trim();
        if (rest.StartsWith("Current_", StringComparison.OrdinalIgnoreCase))
            rest = rest["Current_".Length..];

        string? plmn = null;
        var lastSep = rest.LastIndexOf('_');
        if (lastSep >= 0 && IsDigits(rest[(lastSep + 1)..], 5, 6))
        {
            plmn = rest[(lastSep + 1)..];
            rest = rest[..lastSep];
        }

        if (!string.IsNullOrEmpty(accessTech) &&
            rest.EndsWith("_" + accessTech, StringComparison.OrdinalIgnoreCase))
        {
            rest = rest[..^(accessTech.Length + 1)];
        }
        else if (plmn != null && rest.LastIndexOf('_') is var techSep and > 0)
        {
            // The access technology segment did not match the reported one exactly.
            rest = rest[..techSep];
        }

        rest = rest.Trim();
        return (rest.Length == 0 ? null : rest, plmn);
    }

    /// <summary>
    /// Parse a Zyxel band string into a radio interface and band class. Null when the string
    /// names no band.
    /// </summary>
    public static (string RadioInterface, string BandClass)? ParseBand(string? band)
    {
        if (string.IsNullOrWhiteSpace(band))
            return null;

        var m = Regex.Match(band, @"^\s*(?:LTE_BC|B)(\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success)
            return ("lte", $"eutran-{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}");

        m = Regex.Match(band, @"^\s*(?:NR5G[_ ]?(?:BAND)?[_ ]?|N)(\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success)
            return ("nr5g", $"n{int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)}");

        return null;
    }

    /// <summary>
    /// Read a bandwidth field as MHz: a 3GPP index 1-5, or a string such as <c>"60M"</c>.
    /// Index 0 (1.4 MHz) and unknown values are null.
    /// </summary>
    public static int? ParseBandwidthMhz(JsonElement source, string key)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(key, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var index))
            return index is >= 1 and <= 5 ? BandwidthIndexMhz[index] : null;

        if (prop.ValueKind == JsonValueKind.String)
        {
            var m = Regex.Match(prop.GetString() ?? "", @"^\s*(\d+)\s*M", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mhz) && mhz > 0)
                return mhz;
        }
        return null;
    }

    private static bool IsStandalone(string accessTech)
    {
        var upper = accessTech.ToUpperInvariant();
        return upper.StartsWith("NR", StringComparison.Ordinal) &&
               !upper.Contains("NSA", StringComparison.Ordinal) &&
               upper.Contains("SA", StringComparison.Ordinal);
    }

    /// <summary>
    /// Build signal info, dropping the firmware's "not available" sentinels and readings no
    /// radio can produce. Null when there is no RSRP.
    /// </summary>
    private static SignalInfo? BuildSignal(JsonElement source, string rsrpKey, string rsrqKey, string rssiKey, string sinrKey)
    {
        var rsrp = InRange(TryGetDouble(source, rsrpKey), -140, -30, excludeLow: true);
        if (!rsrp.HasValue)
            return null;

        return new SignalInfo
        {
            Rsrp = rsrp,
            Rsrq = InRange(TryGetDouble(source, rsrqKey), -43, 20, excludeLow: false),
            Rssi = InRange(TryGetDouble(source, rssiKey), -120, -1, excludeLow: true),
            Snr = InRange(TryGetDouble(source, sinrKey), -20, 60, excludeLow: true),
        };
    }

    private static BandInfo? BuildBand((string RadioInterface, string BandClass)? band, int? rfcn, int? bandwidthMhz)
    {
        if (band == null)
            return null;
        return new BandInfo
        {
            RadioInterface = band.Value.RadioInterface,
            BandClass = band.Value.BandClass,
            Channel = rfcn is > 0 ? rfcn.Value : 0,
            BandwidthMhz = bandwidthMhz,
        };
    }

    private static List<CellInfo> ParseNeighbors(JsonElement cellwan)
    {
        var cells = new List<CellInfo>();
        if (!cellwan.TryGetProperty("NBR_Info", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return cells;

        foreach (var nbr in arr.EnumerateArray())
        {
            if (nbr.ValueKind != JsonValueKind.Object || TryGetBool(nbr, "Enable") == false)
                continue;

            var pci = ValidPci(TryGetInt(nbr, "PhyCellID"));
            if (!pci.HasValue)
                continue;

            var rfcn = TryGetInt(nbr, "RFCN");
            cells.Add(new CellInfo
            {
                PhysicalCellId = pci.Value,
                Earfcn = rfcn is > 0 ? rfcn : null,
                Signal = BuildSignal(nbr, "RSRP", "RSRQ", "RSSI", "SINR"),
            });
        }
        return cells;
    }

    private static int? ValidPci(int? pci) => pci is >= 0 and <= 1007 ? pci : null;

    private static double? InRange(double? value, double low, double high, bool excludeLow)
    {
        if (!value.HasValue)
            return null;
        var v = value.Value;
        if (excludeLow ? v <= low : v < low)
            return null;
        return v > high ? null : v;
    }

    private static bool IsDigits(string? s, int minLength, int maxLength) =>
        s != null && s.Length >= minLength && s.Length <= maxLength && s.All(char.IsAsciiDigit);

    private static string? TryGetString(JsonElement? source, string key)
    {
        if (source is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(key, out var prop))
            return null;
        var s = prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            _ => null,
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool? TryGetBool(JsonElement source, string key)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(key, out var prop))
            return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when prop.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static int? TryGetInt(JsonElement source, string key)
    {
        var l = TryGetLong(source, key);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : null;
    }

    private static long? TryGetLong(JsonElement source, string key)
    {
        var d = TryGetDouble(source, key);
        return d.HasValue && d.Value == Math.Floor(d.Value) && Math.Abs(d.Value) < 9e15 ? (long)d.Value : null;
    }

    private static double? TryGetDouble(JsonElement source, string key)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(key, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
            return d;
        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }
}
