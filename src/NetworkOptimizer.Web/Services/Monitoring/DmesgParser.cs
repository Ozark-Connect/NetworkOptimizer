using System.Globalization;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Parses raw dmesg output into categorized findings. Pure static methods for testability.
///
/// Multi-WAN gateways fill the ring buffer with SFE connection removal messages at a rate
/// that pushes boot events out within hours. The parser counts noise categories rather than
/// showing individual lines, and flags when the buffer is dominated by them.
/// </summary>
public static partial class DmesgParser
{
    private const int MaxLinesPerCategory = 50;

    public static DmesgDiagnosticsReport Parse(string output, TimeSpan? uptime = null)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new DmesgDiagnosticsReport
            {
                RawOutput = output ?? string.Empty,
                RunError = "dmesg returned no output."
            };
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var totalLines = lines.Length;

        int sfeCount = 0;
        int bridgePromisc = 0;
        var oomLines = new List<string>();
        var panicLines = new List<string>();
        var ssdkLines = new List<string>();
        var phyLines = new List<string>();
        var pcieLines = new List<string>();
        var storageLines = new List<string>();
        var resetLines = new List<string>();
        var errorLines = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Strip the timestamp for pattern matching, keep the full line for display.
            var body = StripTimestamp(line);

            // --- Noise categories (count only) ---
            if (body.Contains("sfe_ipv4_remove_connection", StringComparison.Ordinal) ||
                body.Contains("sfe_ipv6_remove_connection", StringComparison.Ordinal))
            {
                sfeCount++;
                continue;
            }

            if (body.Contains("entered promiscuous mode", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("left promiscuous mode", StringComparison.OrdinalIgnoreCase))
            {
                bridgePromisc++;
                continue;
            }

            // --- OOM / Memory ---
            if (body.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("killed process", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("oom-kill", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("oom_kill", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("invoked oom-killer", StringComparison.OrdinalIgnoreCase))
            {
                AddCapped(oomLines, line);
                continue;
            }

            // --- Kernel panic / crash ---
            if (body.Contains("Kernel panic", StringComparison.OrdinalIgnoreCase) ||
                (body.Contains("Oops:", StringComparison.OrdinalIgnoreCase) &&
                 !body.Contains("ramoops", StringComparison.OrdinalIgnoreCase)) ||
                body.Contains("BUG:", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("watchdog reset", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("Watchdog expired", StringComparison.OrdinalIgnoreCase))
            {
                AddCapped(panicLines, line);
                continue;
            }

            // --- SSDK / QCA switch ---
            if (body.Contains("ssdk", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("qca-ssdk", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("qca_hppe", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("regi_init", StringComparison.OrdinalIgnoreCase))
            {
                AddCapped(ssdkLines, line);
                continue;
            }

            // --- PHY / SFP / SerDes / link ---
            if (MatchesPhyPattern(body))
            {
                AddCapped(phyLines, line);
                continue;
            }

            // --- PCIe ---
            if (body.Contains("pcie", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("Phy link never came up", StringComparison.OrdinalIgnoreCase))
            {
                AddCapped(pcieLines, line);
                continue;
            }

            // --- Reset reason ---
            if (body.Contains("restart_reason", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("scm_restart_reason", StringComparison.OrdinalIgnoreCase))
            {
                AddCapped(resetLines, line);
                continue;
            }

            // --- Storage ---
            if (body.Contains("I/O error", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("EXT4-fs error", StringComparison.OrdinalIgnoreCase) ||
                (body.Contains("md/raid", StringComparison.OrdinalIgnoreCase) &&
                 body.Contains("degraded", StringComparison.OrdinalIgnoreCase)))
            {
                AddCapped(storageLines, line);
                continue;
            }

            // --- General errors/warnings (catch-all, after specific categories) ---
            if (IsKernelError(body))
            {
                AddCapped(errorLines, line);
            }
        }

        var categories = new List<DmesgCategory>();

        // Errors first
        if (panicLines.Count > 0)
            categories.Add(MakeCategory("panic", "Kernel Panic / Crash", DmesgSeverity.Error, panicLines));

        if (oomLines.Count > 0)
            categories.Add(MakeCategory("oom", "Out of Memory", DmesgSeverity.Error, oomLines));

        if (storageLines.Count > 0)
            categories.Add(MakeCategory("storage", "Storage Errors", DmesgSeverity.Error, storageLines));

        // Warnings
        if (pcieLines.Count > 0)
            categories.Add(MakeCategory("pcie", "PCIe", DmesgSeverity.Warning, pcieLines));

        if (resetLines.Count > 0)
            categories.Add(MakeCategory("reset", "Reset Reason", DmesgSeverity.Info, resetLines));

        // Informational
        if (ssdkLines.Count > 0)
            categories.Add(MakeCategory("ssdk", "QCA Switch (SSDK)", DmesgSeverity.Info, ssdkLines));

        if (phyLines.Count > 0)
            categories.Add(MakeCategory("phy", "PHY / SFP / Link", DmesgSeverity.Info, phyLines));

        if (errorLines.Count > 0)
            categories.Add(MakeCategory("errors", "Other Errors and Warnings", DmesgSeverity.Warning, errorLines));

        // Noise summaries
        if (sfeCount > 0)
        {
            categories.Add(new DmesgCategory
            {
                Id = "sfe",
                Title = "SFE Connection Tracking",
                Severity = DmesgSeverity.Info,
                Summary = $"{sfeCount:N0} SFE connection removals{(sfeCount > 100 ? " - normal for multi-WAN" : "")}",
                Count = sfeCount
            });
        }

        if (bridgePromisc > 0)
        {
            categories.Add(new DmesgCategory
            {
                Id = "bridge",
                Title = "Bridge State Changes",
                Severity = DmesgSeverity.Info,
                Summary = $"{bridgePromisc:N0} promiscuous mode transitions",
                Count = bridgePromisc
            });
        }

        var noiseCount = sfeCount + bridgePromisc;
        var noiseRatio = totalLines > 0 ? (double)noiseCount / totalLines : 0;

        return new DmesgDiagnosticsReport
        {
            CollectedAt = DateTime.UtcNow,
            Categories = categories,
            TotalLines = totalLines,
            ApproximateUptime = uptime,
            RawOutput = output,
            RingBufferDominatedByNoise = noiseRatio > 0.8 && noiseCount > 100
        };
    }

    private static bool MatchesPhyPattern(string body)
    {
        if (body.Contains("serdes", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("sgmii", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("uniphy", StringComparison.OrdinalIgnoreCase))
            return true;

        if (body.Contains("link up", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("link down", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("link becomes ready", StringComparison.OrdinalIgnoreCase))
            return true;

        if (body.Contains("PHY Link", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("sfp_phy", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsKernelError(string body)
    {
        if (body.Contains("ERROR", StringComparison.Ordinal) ||
            body.Contains("WARN", StringComparison.Ordinal))
            return true;

        // ssdk WARN messages already caught above
        if (body.Contains(":WARN:", StringComparison.Ordinal) ||
            body.Contains(":ERROR:", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static void AddCapped(List<string> list, string line)
    {
        if (list.Count < MaxLinesPerCategory)
            list.Add(line);
    }

    private static DmesgCategory MakeCategory(string id, string title, DmesgSeverity severity, List<string> lines)
    {
        var count = lines.Count;
        var summary = count == 1
            ? lines[0]
            : $"{count} {title.ToLower(CultureInfo.InvariantCulture)} events";

        return new DmesgCategory
        {
            Id = id,
            Title = title,
            Severity = severity,
            Summary = summary,
            Lines = lines,
            Count = count
        };
    }

    /// <summary>
    /// Strips the timestamp prefix from a dmesg line. Handles both formats:
    /// kernel uptime "[12345.678901] " and human-readable "[Sat Aug 15 22:19:32 2026] ".
    /// </summary>
    internal static string StripTimestamp(string line)
    {
        if (line.Length < 3 || line[0] != '[') return line;
        var close = line.IndexOf(']');
        if (close < 0 || close >= line.Length - 1) return line;
        return line[(close + 1)..].TrimStart();
    }

}
