using System.Text.RegularExpressions;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Parses qmicli command output into structured CellularModemStats
/// </summary>
public static class QmicliParser
{
    /// <summary>
    /// Parse --nas-get-signal-info output
    /// </summary>
    public static (SignalInfo? lte, SignalInfo? nr5g) ParseSignalInfo(string output)
    {
        SignalInfo? lte = null;
        SignalInfo? nr5g = null;
        string? currentSection = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed == "LTE:")
            {
                currentSection = "LTE";
                lte = new SignalInfo();
            }
            else if (trimmed == "5G:")
            {
                currentSection = "5G";
                nr5g = new SignalInfo();
            }
            else if (currentSection != null)
            {
                var signal = currentSection == "LTE" ? lte : nr5g;
                if (signal == null) continue;

                if (TryParseDbValue(trimmed, "RSRP:", out var rsrp))
                    signal.Rsrp = rsrp;
                else if (TryParseDbValue(trimmed, "RSRQ:", out var rsrq))
                    signal.Rsrq = rsrq;
                else if (TryParseDbValue(trimmed, "RSSI:", out var rssi))
                    signal.Rssi = rssi;
                else if (TryParseDbValue(trimmed, "SNR:", out var snr))
                    signal.Snr = snr;
            }
        }

        return (lte, nr5g);
    }

    /// <summary>
    /// Parse --nas-get-serving-system output
    /// </summary>
    public static (string registrationState, string carrier, string mcc, string mnc, bool isRoaming) ParseServingSystem(string output)
    {
        string registrationState = "";
        string carrier = "";
        string mcc = "";
        string mnc = "";
        bool isRoaming = false;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Registration state:"))
                registrationState = ExtractQuotedValue(trimmed);
            else if (trimmed.StartsWith("Description:"))
                carrier = ExtractQuotedValue(trimmed);
            else if (trimmed.StartsWith("MCC:"))
                mcc = ExtractQuotedValue(trimmed);
            else if (trimmed.StartsWith("MNC:"))
                mnc = ExtractQuotedValue(trimmed);
            else if (trimmed.StartsWith("Roaming status:"))
                isRoaming = ExtractQuotedValue(trimmed) != "off";
        }

        return (registrationState, carrier, mcc, mnc, isRoaming);
    }

    /// <summary>
    /// Parse --nas-get-cell-location-info output
    /// </summary>
    public static (CellInfo? servingCell, List<CellInfo> neighborCells) ParseCellLocationInfo(string output)
    {
        CellInfo? servingCell = null;
        var neighborCells = new List<CellInfo>();
        bool inIntraFreq = false;
        bool inInterFreq = false;
        int? currentEarfcn = null;
        string? currentBandDesc = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Intrafrequency LTE Info"))
            {
                inIntraFreq = true;
                inInterFreq = false;
            }
            else if (trimmed.StartsWith("Interfrequency LTE Info"))
            {
                inIntraFreq = false;
                inInterFreq = true;
            }
            else if (trimmed.StartsWith("LTE Info Neighboring"))
            {
                inIntraFreq = false;
                inInterFreq = false;
            }

            // Parse serving cell info (from intrafrequency section header)
            if (inIntraFreq)
            {
                if (trimmed.StartsWith("PLMN:") && servingCell == null)
                {
                    servingCell = new CellInfo { IsServing = true };
                    servingCell.Plmn = ExtractQuotedValue(trimmed);
                }
                else if (trimmed.StartsWith("Tracking Area Code:") && servingCell != null)
                    servingCell.Tac = ExtractQuotedValue(trimmed);
                else if (trimmed.StartsWith("Global Cell ID:") && servingCell != null)
                    servingCell.GlobalCellId = ExtractQuotedValue(trimmed);
                else if (trimmed.StartsWith("EUTRA Absolute RF Channel Number:") && servingCell != null)
                {
                    var match = Regex.Match(trimmed, @"'(\d+)'.*\((.+)\)");
                    if (match.Success)
                    {
                        servingCell.Earfcn = int.Parse(match.Groups[1].Value);
                        servingCell.BandDescription = match.Groups[2].Value;
                    }
                }
                else if (trimmed.StartsWith("Serving Cell ID:") && servingCell != null)
                    servingCell.PhysicalCellId = int.TryParse(ExtractQuotedValue(trimmed), out var pci) ? pci : 0;
                else if (trimmed.StartsWith("Physical Cell ID:") && servingCell != null && servingCell.Signal == null)
                {
                    // This is in the Cell [0] block of intrafreq - it's the serving cell details
                    servingCell.PhysicalCellId = int.TryParse(ExtractQuotedValue(trimmed), out var pci) ? pci : 0;
                }
                else if (trimmed.StartsWith("RSRP:") && servingCell != null)
                {
                    servingCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        servingCell.Signal.Rsrp = val;
                }
                else if (trimmed.StartsWith("RSRQ:") && servingCell != null)
                {
                    servingCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        servingCell.Signal.Rsrq = val;
                }
                else if (trimmed.StartsWith("RSSI:") && servingCell != null)
                {
                    servingCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        servingCell.Signal.Rssi = val;
                }
            }

            // Parse neighbor cells from interfrequency section
            if (inInterFreq)
            {
                if (trimmed.StartsWith("EUTRA Absolute RF Channel Number:"))
                {
                    var match = Regex.Match(trimmed, @"'(\d+)'.*\((.+)\)");
                    if (match.Success)
                    {
                        currentEarfcn = int.Parse(match.Groups[1].Value);
                        currentBandDesc = match.Groups[2].Value;
                    }
                }
                else if (trimmed.StartsWith("Physical Cell ID:"))
                {
                    var cell = new CellInfo
                    {
                        IsServing = false,
                        Earfcn = currentEarfcn,
                        BandDescription = currentBandDesc,
                        Signal = new SignalInfo()
                    };
                    cell.PhysicalCellId = int.TryParse(ExtractQuotedValue(trimmed), out var pci) ? pci : 0;
                    neighborCells.Add(cell);
                }
                else if (trimmed.StartsWith("RSRP:") && neighborCells.Count > 0)
                {
                    var lastCell = neighborCells[^1];
                    lastCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        lastCell.Signal.Rsrp = val;
                }
                else if (trimmed.StartsWith("RSRQ:") && neighborCells.Count > 0)
                {
                    var lastCell = neighborCells[^1];
                    lastCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        lastCell.Signal.Rsrq = val;
                }
                else if (trimmed.StartsWith("RSSI:") && neighborCells.Count > 0)
                {
                    var lastCell = neighborCells[^1];
                    lastCell.Signal ??= new SignalInfo();
                    if (TryParseDbValueAlt(trimmed, out var val))
                        lastCell.Signal.Rssi = val;
                }
            }

            // Timing advance (outside sections)
            if (trimmed.StartsWith("LTE Timing Advance:") && servingCell != null)
            {
                var match = Regex.Match(trimmed, @"'(\d+)'");
                if (match.Success)
                    servingCell.TimingAdvance = int.Parse(match.Groups[1].Value);
            }
        }

        return (servingCell, neighborCells);
    }

    /// <summary>
    /// Parse --nas-get-rf-band-info output
    /// </summary>
    public static BandInfo? ParseRfBandInfo(string output)
    {
        BandInfo? band = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Radio Interface:"))
            {
                band ??= new BandInfo();
                band.RadioInterface = ExtractQuotedValue(trimmed);
            }
            else if (trimmed.StartsWith("Active Band Class:") && band != null)
                band.BandClass = ExtractQuotedValue(trimmed);
            else if (trimmed.StartsWith("Active Channel:") && band != null)
                band.Channel = int.TryParse(ExtractQuotedValue(trimmed), out var ch) ? ch : 0;
            else if (trimmed.StartsWith("Bandwidth:") && band != null && !trimmed.Contains("Radio"))
                band.BandwidthMhz = int.TryParse(ExtractQuotedValue(trimmed), out var bw) ? bw : null;
        }

        return band;
    }

    private static string ExtractQuotedValue(string line)
    {
        var match = Regex.Match(line, @"'([^']*)'");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static bool TryParseDbValue(string line, string prefix, out double value)
    {
        value = 0;
        if (!line.StartsWith(prefix)) return false;

        var match = Regex.Match(line, @"'(-?\d+\.?\d*)\s*dB");
        if (match.Success)
        {
            return double.TryParse(match.Groups[1].Value, out value);
        }
        return false;
    }

    private static bool TryParseDbValueAlt(string line, out double value)
    {
        value = 0;
        // Format: RSRP: '-93.6' dBm
        var match = Regex.Match(line, @"'(-?\d+\.?\d*)'");
        if (match.Success)
        {
            return double.TryParse(match.Groups[1].Value, out value);
        }
        return false;
    }
}
