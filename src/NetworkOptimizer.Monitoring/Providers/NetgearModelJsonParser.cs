using System;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Pure-function translator from NetgearWebApp's <c>/api/model.json</c> response shape
/// into <see cref="CellularModemStats"/>. Extracted from
/// <c>NetgearNighthawkHotspotProvider</c> so it can be unit-tested without an HTTP transport.
/// </summary>
/// <remarks>
/// Schema notes (verified live against MR5200 sdxprairie, M6 Pro sdxlemur, and LM1200):
/// <list type="bullet">
///   <item><c>wwan.diagInfo</c> is an array; the first entry holds the per-RAT signal split</item>
///   <item><c>wwan.signalStrength</c> is the simple-LTE fallback when diagInfo is absent (e.g. LM1200)</item>
///   <item><c>wwan.lteBandInfo[]</c> / <c>wwan.nr5gBandInfo[]</c> carry per-CC band data; the entry with <c>isPcc:true</c> is the primary</item>
///   <item><c>wwanadv</c> carries band (<c>curBand</c>) and serving cell data (<c>cellId</c>, <c>chanId</c>, <c>LAC</c>, <c>MCC</c>/<c>MNC</c>) on models like the LM1200 where <c>lteBandInfo</c> is absent</item>
///   <item>Numeric values come back as strings with unit suffixes (e.g. <c>"-98 dBm"</c>, <c>"-17 dB"</c>)</item>
/// </list>
/// </remarks>
public static class NetgearModelJsonParser
{
    /// <summary>
    /// Parse a model.json document root into stats. Returns an empty (but non-null) stats
    /// object if <c>wwan</c> is missing - the polling pipeline can still surface that the
    /// modem responded, just without signal data.
    /// </summary>
    public static CellularModemStats Parse(JsonElement root, ModemPollContext context)
    {
        var stats = new CellularModemStats
        {
            Timestamp = DateTime.UtcNow,
            ModemHost = context.Host,
            ModemName = context.Name,
            ModemModel = TryGetString(root, "general", "deviceName") ?? context.ModemType,
        };

        if (!root.TryGetProperty("wwan", out var wwan) || wwan.ValueKind != JsonValueKind.Object)
        {
            return stats;
        }

        var connection = TryGetString(wwan, "connection");
        var connectionText = TryGetString(wwan, "connectionText");
        stats.RegistrationState = !string.IsNullOrEmpty(connectionText) ? connectionText : (connection ?? "");

        stats.Carrier = TryGetString(wwan, "registerNetworkDisplay")
                        ?? TryGetString(root, "general", "serviceProviderName")
                        ?? "";

        if (TryGetBool(wwan, out var roaming, "roaming"))
        {
            stats.IsRoaming = roaming;
        }

        // Signal: wwan.diagInfo is an array; the first entry holds the LTE+NR5G split
        if (wwan.TryGetProperty("diagInfo", out var diagInfo) &&
            diagInfo.ValueKind == JsonValueKind.Array &&
            diagInfo.GetArrayLength() > 0 &&
            diagInfo[0].ValueKind == JsonValueKind.Object)
        {
            var d = diagInfo[0];
            stats.Lte = BuildSignalInfo(d, "ltesigRssi", "ltesigRsrp", "ltesigRsrq", "ltesigSnr");
            stats.Nr5g = BuildSignalInfo(d, "nr5gsigRssi", "nr5gsigRsrp", "nr5gsigRsrq", "nr5gsigSnr");
        }

        // Fallback: wwan.signalStrength is a flat object with rssi/rsrp/rsrq/sinr
        if (stats.Lte == null && stats.Nr5g == null &&
            wwan.TryGetProperty("signalStrength", out var sig) &&
            sig.ValueKind == JsonValueKind.Object)
        {
            stats.Lte = BuildSignalInfo(sig, "rssi", "rsrp", "rsrq", "sinr");
        }

        bool hasNr5g = stats.Nr5g?.Rsrp.HasValue == true;
        stats.ActiveBand = BuildBandInfo(root, wwan, hasNr5g);
        stats.ServingCell = BuildCellInfo(root, wwan, stats.PrimarySignal, hasNr5g);

        return stats;
    }

    private static SignalInfo? BuildSignalInfo(JsonElement source, string rssiKey, string rsrpKey, string rsrqKey, string snrKey)
    {
        var rssi = TryGetDouble(source, rssiKey);
        var rsrp = TryGetDouble(source, rsrpKey);
        var rsrq = TryGetDouble(source, rsrqKey);
        var snr = TryGetDouble(source, snrKey);

        if (!rsrp.HasValue && !rssi.HasValue && !snr.HasValue && !rsrq.HasValue)
        {
            return null;
        }

        return new SignalInfo
        {
            Rssi = rssi,
            Rsrp = rsrp,
            Rsrq = rsrq,
            Snr = snr,
        };
    }

    private static BandInfo? BuildBandInfo(JsonElement root, JsonElement wwan, bool hasNr5g)
    {
        var arrayKey = hasNr5g ? "nr5gBandInfo" : "lteBandInfo";
        var rat = hasNr5g ? "nr5g" : "lte";

        var pcc = FindPccEntry(wwan, arrayKey);
        if (pcc == null)
        {
            if (hasNr5g) return null;
            var curBand = TryGetString(wwan, "curBand")
                       ?? TryGetString(root, "wwanadv", "curBand");
            return string.IsNullOrEmpty(curBand) ? null : ParseCurBand(curBand);
        }

        var info = new BandInfo { RadioInterface = rat };

        if (TryGetInt(pcc.Value, out var band, "band"))
        {
            info.BandClass = hasNr5g ? $"n{band}" : $"eutran-{band}";
        }

        if (TryGetInt(pcc.Value, out var channel, "channel"))
        {
            info.Channel = channel;
        }

        var bw = TryGetString(pcc.Value, "dlBandwidth");
        if (!string.IsNullOrEmpty(bw))
        {
            var match = Regex.Match(bw, @"\d+");
            if (match.Success && int.TryParse(match.Value, out var bwInt))
            {
                info.BandwidthMhz = bwInt;
            }
        }

        return info;
    }

    private static CellInfo? BuildCellInfo(JsonElement root, JsonElement wwan, SignalInfo? signal, bool hasNr5g)
    {
        var arrayKey = hasNr5g ? "nr5gBandInfo" : "lteBandInfo";
        var pcc = FindPccEntry(wwan, arrayKey);
        if (pcc != null)
        {
            var cell = new CellInfo
            {
                IsServing = true,
                Signal = signal,
            };

            if (TryGetInt(pcc.Value, out var pci, "phyCid")) cell.PhysicalCellId = pci;
            if (TryGetInt(pcc.Value, out var ch, "channel")) cell.Earfcn = ch;

            return cell;
        }

        // Do not use wwanadv fallback for 5G NR serving cell
        if (hasNr5g) return null;

        // Fallback for Netgear LM1200 / Sierra Wireless: cell details are reported under wwanadv
        if (root.TryGetProperty("wwanadv", out var wwanadv) && wwanadv.ValueKind == JsonValueKind.Object)
        {
            var cell = new CellInfo
            {
                IsServing = true,
                Signal = signal,
            };

            // GlobalCellId: check decimal or hex string, or integer
            if (TryGetInt(wwanadv, out var cellIdInt, "cellId") && cellIdInt > 0)
            {
                cell.GlobalCellId = cellIdInt.ToString(CultureInfo.InvariantCulture);
            }
            else if (wwanadv.TryGetProperty("cellId", out var cellIdProp) && cellIdProp.ValueKind == JsonValueKind.String)
            {
                var s = cellIdProp.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexVal) && hexVal > 0)
                    {
                        cell.GlobalCellId = hexVal.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decVal) && decVal > 0)
                    {
                        cell.GlobalCellId = decVal.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }

            if (TryGetInt(wwanadv, out var ch, "chanId") && ch > 0)
            {
                cell.Earfcn = ch;
            }

            // Prefer TAC, fallback to LAC
            if (TryGetInt(wwanadv, out var tac, "TAC") && tac > 0)
            {
                cell.Tac = tac.ToString(CultureInfo.InvariantCulture);
            }
            else if (TryGetInt(wwanadv, out var lac, "LAC") && lac > 0)
            {
                cell.Tac = lac.ToString(CultureInfo.InvariantCulture);
            }

            // PLMN: keep digits exactly as sent by the modem (strings) to preserve leading zeros
            var mcc = TryGetString(wwanadv, "MCC");
            var mnc = TryGetString(wwanadv, "MNC");
            if (!string.IsNullOrEmpty(mcc) && !string.IsNullOrEmpty(mnc))
            {
                cell.Plmn = $"{mcc}{mnc}";
            }

            // Return fallback cell only if at least one field actually populated
            if (cell.GlobalCellId != null || cell.Earfcn.HasValue || cell.Tac != null || cell.Plmn != null)
            {
                return cell;
            }
        }

        return null;
    }

    private static JsonElement? FindPccEntry(JsonElement wwan, string arrayKey)
    {
        if (!wwan.TryGetProperty(arrayKey, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("isPcc", out var isPcc)) continue;
            var isPccTrue =
                isPcc.ValueKind == JsonValueKind.True ||
                (isPcc.ValueKind == JsonValueKind.String &&
                 string.Equals(isPcc.GetString(), "true", StringComparison.OrdinalIgnoreCase));
            if (isPccTrue) return entry;
        }
        return null;
    }

    /// <summary>
    /// Parse a <c>curBand</c> string like "LTE B2" or "NR5G n77" into a BandInfo,
    /// used as a fallback when the typed band-info arrays are missing.
    /// </summary>
    private static BandInfo ParseCurBand(string curBand)
    {
        var info = new BandInfo();
        var match = Regex.Match(curBand, @"(LTE|NR5G|5G)\s*([A-Za-z]?)(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            info.BandClass = curBand;
            return info;
        }

        var rat = match.Groups[1].Value.ToUpperInvariant();
        var num = match.Groups[3].Value;

        if (rat == "LTE")
        {
            info.RadioInterface = "lte";
            info.BandClass = $"eutran-{num}";
        }
        else
        {
            info.RadioInterface = "nr5g";
            info.BandClass = $"n{num}";
        }

        return info;
    }

    private static string? TryGetString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool TryGetInt(JsonElement root, out int value, params string[] path)
    {
        value = 0;
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return false;
            if (!current.TryGetProperty(segment, out var next)) return false;
            current = next;
        }
        if (current.ValueKind == JsonValueKind.Number && current.TryGetInt32(out var n))
        {
            value = n;
            return true;
        }
        if (current.ValueKind == JsonValueKind.String)
        {
            var raw = current.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var token = raw.TrimStart().Split(' ')[0];
            if (int.TryParse(token, out var s))
            {
                value = s;
                return true;
            }
        }
        return false;
    }

    private static double? TryGetDouble(JsonElement source, string key)
    {
        if (source.ValueKind != JsonValueKind.Object) return null;
        if (!source.TryGetProperty(key, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d)) return d;
        if (prop.ValueKind == JsonValueKind.String)
        {
            var raw = prop.GetString();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // NetgearWebApp returns values like "-98 dBm", "-17 dB", "0 dB". Take the leading numeric token.
            var token = raw.TrimStart().Split(' ')[0];
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
        }
        return null;
    }

    private static bool TryGetBool(JsonElement root, out bool value, params string[] path)
    {
        value = false;
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return false;
            if (!current.TryGetProperty(segment, out var next)) return false;
            current = next;
        }
        if (current.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (current.ValueKind == JsonValueKind.False) { value = false; return true; }
        if (current.ValueKind == JsonValueKind.String)
        {
            var raw = current.GetString();
            if (bool.TryParse(raw, out var b)) { value = b; return true; }
        }
        return false;
    }
}
