using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Parses Quectel AT+QENG="servingcell" responses into <see cref="CellularModemStats"/>.
///
/// Supports four response formats (per Quectel AT Commands Manual):
/// <list type="bullet">
///   <item><description>NR5G-SA: single line with NR5G-SA fields</description></item>
///   <item><description>LTE: single line with LTE fields</description></item>
///   <item><description>NR5G-NSA (EN-DC): multi-line with LTE primary + NR5G-NSA secondary</description></item>
///   <item><description>WCDMA: single line with WCDMA fields (3G fallback)</description></item>
/// </list>
///
/// Used by the GL-iNet/Quectel modem provider via
/// <c>gl_modem -B {path} AT AT+QENG=\"servingcell\"</c>.
/// </summary>
public static class QuectelAtParser
{
    /// <summary>
    /// Parse the AT+QENG="servingcell" response into <see cref="CellularModemStats"/>.
    /// </summary>
    /// <param name="output">Raw AT command response (may include echo and OK lines).</param>
    /// <param name="host">Modem host for diagnostics.</param>
    /// <param name="name">Modem friendly name.</param>
    /// <param name="model">Modem model string.</param>
    public static CellularModemStats? Parse(string output, string host, string name, string model)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var stats = new CellularModemStats
        {
            ModemHost = host,
            ModemName = name,
            ModemModel = model,
            Timestamp = DateTime.UtcNow,
        };

        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (!line.StartsWith("+QENG:", StringComparison.OrdinalIgnoreCase))
                continue;

            var payload = line.Substring("+QENG:".Length).Trim();

            if (payload.Contains("\"NR5G-SA\"", StringComparison.OrdinalIgnoreCase))
            {
                ParseNr5gSa(payload, stats);
            }
            else if (payload.Contains("\"NR5G-NSA\"", StringComparison.OrdinalIgnoreCase))
            {
                ParseNr5gNsa(payload, stats);
            }
            else if (payload.Contains("\"LTE\"", StringComparison.OrdinalIgnoreCase))
            {
                ParseLte(payload, stats);
            }
            else if (payload.Contains("\"WCDMA\"", StringComparison.OrdinalIgnoreCase))
            {
                ParseWcdma(payload, stats);
            }
        }

        // Only return stats if we parsed at least some signal data
        if (stats.Lte == null && stats.Nr5g == null)
            return null;

        return stats;
    }

    /// <summary>
    /// Parse NR5G-SA format:
    /// "servingcell",state,"NR5G-SA",duplex_mode,MCC,MNC,cellID,PCID,TAC,ARFCN,band,NR_DL_bandwidth,RSRP,RSRQ,SINR,scs,srxlev
    /// </summary>
    private static void ParseNr5gSa(string payload, CellularModemStats stats)
    {
        var fields = SplitFields(payload);
        if (fields.Length < 17) return;

        // fields[0] = "servingcell", [1] = state, [2] = "NR5G-SA"
        stats.CarrierMcc = Unquote(fields[4]);
        stats.CarrierMnc = Unquote(fields[5]);
        stats.RegistrationState = "registered";

        var rsrp = ParseDoubleField(fields[12]);
        var rsrq = ParseDoubleField(fields[13]);
        var snr = ParseDoubleField(fields[14]);

        if (rsrp.HasValue || rsrq.HasValue || snr.HasValue)
        {
            stats.Nr5g = new SignalInfo { Rsrp = rsrp, Rsrq = rsrq, Snr = snr };
        }

        var cellId = Unquote(fields[6]);
        var pci = ParseIntField(fields[7]);
        if (!string.IsNullOrEmpty(cellId) || pci.HasValue)
        {
            stats.ServingCell = new CellInfo
            {
                IsServing = true,
                GlobalCellId = cellId,
                PhysicalCellId = pci ?? 0,
                Tac = Unquote(fields[8]),
                Earfcn = ParseIntField(fields[9]),
            };
        }

        var bandStr = Unquote(fields[10]);
        if (!string.IsNullOrEmpty(bandStr))
        {
            stats.ActiveBand = new BandInfo
            {
                RadioInterface = "5gnr",
                BandClass = NormalizeBandClass(bandStr, "nr"),
                Channel = ParseIntField(fields[9]) ?? 0,
                BandwidthMhz = ParseBandwidthMhz(Unquote(fields[11])),
            };
        }
    }

    /// <summary>
    /// Parse LTE format:
    /// "servingcell",state,"LTE",is_tdd,MCC,MNC,cellID,PCID,earfcn,freq_band_ind,UL_bandwidth,DL_bandwidth,TAC,RSRP,RSRQ,RSSI,SINR,CQI,tx_power,srxlev
    ///
    /// Also used for the LTE line in NR5G-NSA (EN-DC) dual connectivity response:
    /// "LTE",is_tdd,MCC,MNC,cellID,PCID,earfcn,freq_band_ind,UL_bandwidth,DL_bandwidth,TAC,RSRP,RSRQ,RSSI,SINR,CQI,tx_power,srxlev
    /// </summary>
    private static void ParseLte(string payload, CellularModemStats stats)
    {
        var fields = SplitFields(payload);

        // Determine offset: full response has "servingcell",state prefix; ENDC subline does not
        int offset;
        if (fields.Length >= 2 && Unquote(fields[0]).Equals("servingcell", StringComparison.OrdinalIgnoreCase))
            offset = 2; // "servingcell", state, "LTE", ...
        else
            offset = 0; // "LTE", ...

        int lteIdx = offset; // index of "LTE" field
        if (lteIdx + 17 > fields.Length) return;

        stats.CarrierMcc = Unquote(fields[lteIdx + 1 + 1]); // MCC
        stats.CarrierMnc = Unquote(fields[lteIdx + 1 + 2]); // MNC
        stats.RegistrationState = "registered";

        stats.Lte = new SignalInfo
        {
            Rsrp = ParseDoubleField(fields[lteIdx + 1 + 10]), // RSRP
            Rsrq = ParseDoubleField(fields[lteIdx + 1 + 11]), // RSRQ
            Rssi = ParseDoubleField(fields[lteIdx + 1 + 12]), // RSSI
            Snr = ParseDoubleField(fields[lteIdx + 1 + 13]),  // SINR
        };

        var cellId = Unquote(fields[lteIdx + 1 + 3]);  // cellID
        var pci = ParseIntField(fields[lteIdx + 1 + 4]); // PCID
        if (stats.ServingCell == null && (!string.IsNullOrEmpty(cellId) || pci.HasValue))
        {
            stats.ServingCell = new CellInfo
            {
                IsServing = true,
                GlobalCellId = cellId,
                PhysicalCellId = pci ?? 0,
                Tac = Unquote(fields[lteIdx + 1 + 9]),     // TAC
                Earfcn = ParseIntField(fields[lteIdx + 1 + 5]), // earfcn
            };
        }

        var bandInd = Unquote(fields[lteIdx + 1 + 6]); // freq_band_ind
        if (stats.ActiveBand == null && !string.IsNullOrEmpty(bandInd))
        {
            stats.ActiveBand = new BandInfo
            {
                RadioInterface = "lte",
                BandClass = NormalizeBandClass(bandInd, "lte"),
                Channel = ParseIntField(fields[lteIdx + 1 + 5]) ?? 0,
                BandwidthMhz = ParseBandwidthMhz(Unquote(fields[lteIdx + 1 + 8])), // DL_bandwidth
            };
        }
    }

    /// <summary>
    /// Parse NR5G-NSA (EN-DC) subline:
    /// "NR5G-NSA",MCC,MNC,PCID,RSRP,SINR,RSRQ,ARFCN,band,NR_DL_bandwidth,scs
    /// </summary>
    private static void ParseNr5gNsa(string payload, CellularModemStats stats)
    {
        var fields = SplitFields(payload);

        // Find the "NR5G-NSA" field
        int nsaIdx = -1;
        for (int i = 0; i < fields.Length; i++)
        {
            if (Unquote(fields[i]).Equals("NR5G-NSA", StringComparison.OrdinalIgnoreCase))
            {
                nsaIdx = i;
                break;
            }
        }
        if (nsaIdx < 0 || nsaIdx + 10 > fields.Length) return;

        stats.Nr5g = new SignalInfo
        {
            Rsrp = ParseDoubleField(fields[nsaIdx + 4]),  // RSRP
            Rsrq = ParseDoubleField(fields[nsaIdx + 6]),  // RSRQ
            Snr = ParseDoubleField(fields[nsaIdx + 5]),   // SINR
        };

        // In NSA mode, NR band overrides the LTE band as the primary display
        var bandStr = Unquote(fields[nsaIdx + 8]);
        if (!string.IsNullOrEmpty(bandStr))
        {
            stats.ActiveBand = new BandInfo
            {
                RadioInterface = "5gnr",
                BandClass = NormalizeBandClass(bandStr, "nr"),
                Channel = ParseIntField(fields[nsaIdx + 7]) ?? 0,
                BandwidthMhz = ParseBandwidthMhz(Unquote(fields[nsaIdx + 9])),
            };
        }
    }

    /// <summary>
    /// Parse WCDMA format (3G fallback):
    /// "servingcell",state,"WCDMA",MCC,MNC,LAC,cellID,uarfcn,PSC,RAC,RSCP,ecio,phych,SF,slot,speech_code,comMod
    /// </summary>
    private static void ParseWcdma(string payload, CellularModemStats stats)
    {
        var fields = SplitFields(payload);
        if (fields.Length < 12) return;

        // fields[0] = "servingcell", [1] = state, [2] = "WCDMA"
        stats.CarrierMcc = Unquote(fields[3]);
        stats.CarrierMnc = Unquote(fields[4]);
        stats.RegistrationState = "registered";

        // Map RSCP to RSRP for display purposes (both are dBm power measurements)
        var rscp = ParseDoubleField(fields[10]);
        var ecio = ParseDoubleField(fields[11]);

        stats.Lte = new SignalInfo
        {
            Rsrp = rscp,
            Rsrq = ecio,
        };

        var cellId = Unquote(fields[6]);
        var psc = ParseIntField(fields[8]);
        if (!string.IsNullOrEmpty(cellId) || psc.HasValue)
        {
            stats.ServingCell = new CellInfo
            {
                IsServing = true,
                GlobalCellId = cellId,
                PhysicalCellId = psc ?? 0,
                Tac = Unquote(fields[5]), // LAC for WCDMA
                Earfcn = ParseIntField(fields[7]), // UARFCN
            };
        }
    }

    /// <summary>
    /// Normalize a band indicator to the format used by BandInfo.GetBandName().
    /// Quectel returns band numbers (e.g. "71", "2") or NR band names (e.g. "n71").
    /// </summary>
    private static string NormalizeBandClass(string bandStr, string rat)
    {
        bandStr = bandStr.Trim('"');

        if (rat == "nr")
        {
            // Already in "n71" format
            if (bandStr.StartsWith("n", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(bandStr.AsSpan(1), out _))
                return bandStr.ToLowerInvariant();

            // Bare number - prefix with "n"
            if (int.TryParse(bandStr, out _))
                return $"n{bandStr}";
        }
        else
        {
            // LTE band number to eutran-N format
            if (int.TryParse(bandStr, out var bandNum))
                return $"eutran-{bandNum}";

            // Already in eutran-N format
            if (bandStr.StartsWith("eutran-", StringComparison.OrdinalIgnoreCase))
                return bandStr.ToLowerInvariant();
        }

        return bandStr;
    }

    /// <summary>
    /// Parse bandwidth string to MHz. GL-iNet firmware reports the direct MHz value
    /// (e.g. "5", "10", "20", "100").
    /// </summary>
    private static int? ParseBandwidthMhz(string? bwStr)
    {
        if (string.IsNullOrEmpty(bwStr))
            return null;

        bwStr = bwStr.Replace("MHz", "", StringComparison.OrdinalIgnoreCase).Trim();

        if (int.TryParse(bwStr, out var val) && val > 0)
            return val;

        return null;
    }

    /// <summary>
    /// Split a comma-separated AT response into fields, preserving quoted values.
    /// </summary>
    private static string[] SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        fields.Add(current.ToString().Trim());

        return fields.ToArray();
    }

    private static string Unquote(string field)
    {
        field = field.Trim();
        if (field.Length >= 2 && field[0] == '"' && field[^1] == '"')
            return field[1..^1];
        return field;
    }

    private static double? ParseDoubleField(string field)
    {
        field = Unquote(field).Trim();
        if (string.IsNullOrEmpty(field) || field == "-" || field == "-32768")
            return null;
        return double.TryParse(field, out var val) ? val : null;
    }

    private static int? ParseIntField(string field)
    {
        field = Unquote(field).Trim();
        if (string.IsNullOrEmpty(field) || field == "-")
            return null;

        // Handle hex values (Quectel cellID is often hex)
        if (field.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            field.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(field.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var hexVal)
                ? hexVal
                : null;
        }

        return int.TryParse(field, out var val) ? val : null;
    }
}
