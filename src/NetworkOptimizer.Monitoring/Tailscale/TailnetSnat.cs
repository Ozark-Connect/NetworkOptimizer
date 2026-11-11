using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Monitoring.Tailscale;

/// <summary>
/// One connection a subnet router or exit node source-NATed for a tailnet peer: the peer's original
/// source, and the router address and port the rest of the network sees instead.
/// </summary>
public sealed record TailnetSnatEntry(
    string Proto,
    string PeerIp,
    int PeerPort,
    string RemoteIp,
    int RemotePort,
    string NatIp,
    int NatPort);

/// <summary>
/// Reads the source NAT a Tailscale subnet router or exit node applies, from the host's conntrack
/// table. Only that host's table holds the peer's tailnet source: everything downstream sees the
/// router's own address. TCP and UDP only, since the match downstream is by port.
/// </summary>
public static class TailnetSnat
{
    /// <summary>Where the kernel lists the host network namespace's connections.</summary>
    public const string ConntrackPath = "/proc/net/nf_conntrack";

    /// <summary>The most entries one read returns, so a flood cannot grow the reply without bound.</summary>
    public const int MaxEntries = 20000;

    /// <summary>Reads the host's table; throws when it cannot be opened.</summary>
    public static async Task<IReadOnlyList<TailnetSnatEntry>> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new TailscaleLocalException("Routed traffic can be attributed only on Linux, where the conntrack table is readable.");
        try
        {
            using var reader = new StreamReader(ConntrackPath);
            return await Task.Run(() => Parse(reader), ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Network Optimizer's container runs as a non-root user, and the table is root-only.
            throw new TailscaleLocalException(
                $"Only root can read {ConntrackPath}, and Network Optimizer does not run as root here. An On-Site Agent on this host can read it.", ex);
        }
        catch (IOException ex)
        {
            throw new TailscaleLocalException($"Could not read {ConntrackPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses /proc/net/nf_conntrack lines, keeping the SNATed connections of tailnet sources.</summary>
    public static IReadOnlyList<TailnetSnatEntry> Parse(TextReader reader)
    {
        var entries = new List<TailnetSnatEntry>();
        while (entries.Count < MaxEntries && reader.ReadLine() is { } line)
        {
            // Cheap filter before tokenizing: almost every line in a busy table is not a tailnet source.
            if (!line.Contains("src=100.", StringComparison.Ordinal) && !line.Contains("src=fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ParseLine(line) is { } entry)
                entries.Add(entry);
        }
        return entries;
    }

    /// <summary>The entry for one line, or null when it is not a tailnet source the host translated.</summary>
    public static TailnetSnatEntry? ParseLine(string line)
    {
        string? proto = null;
        string? origSrc = null, origDst = null, replyDst = null;
        int origSport = 0, origDport = 0, replyDport = 0;
        var tuple = 0;
        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                if (proto == null && token is "tcp" or "udp") proto = token;
                continue;
            }
            var value = token[(eq + 1)..];
            switch (token[..eq])
            {
                case "src":
                    tuple++;
                    if (tuple == 1) origSrc = value;
                    break;
                case "dst":
                    if (tuple == 1) origDst = value;
                    else if (tuple == 2) replyDst = value;
                    break;
                case "sport" when tuple == 1:
                    int.TryParse(value, out origSport);
                    break;
                case "dport" when tuple == 1:
                    int.TryParse(value, out origDport);
                    break;
                case "dport" when tuple == 2:
                    int.TryParse(value, out replyDport);
                    break;
            }
        }

        if (proto == null || origSrc == null || origDst == null || replyDst == null) return null;
        if (!IPAddress.TryParse(origSrc, out var src) || !NetworkUtilities.IsTailscaleAddress(src)) return null;
        // Peer to peer inside the tailnet is not translated; neither is a connection the router did not NAT.
        if (NetworkUtilities.IsTailscaleAddress(origDst)) return null;
        if (string.Equals(replyDst, origSrc, StringComparison.OrdinalIgnoreCase) && replyDport == origSport) return null;
        if (origSport <= 0 || replyDport <= 0 || origDport <= 0) return null;
        return new TailnetSnatEntry(proto, origSrc, origSport, origDst, origDport, replyDst, replyDport);
    }
}
