namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>The limited native backend: one voluntary non-MLO 5 GHz AP move, never an eviction.</summary>
public static class ApAgentNativeSteering
{
    public const int ContractVersion = 28;
    public const string Route = "voluntary-bss-transitions";

    public static IReadOnlyList<string> SupportedVaps(ApAgentHealthPayload? health)
        => health is { BinaryVersion: >= ContractVersion }
            && health.LastProbeRun != default && health.CollectedAt != default
            && health.CollectedAt >= health.LastProbeRun
            && health.CollectedAt - health.LastProbeRun <= TimeSpan.FromMinutes(10)
            ? health.NativeVoluntaryVaps?.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? Array.Empty<string>()
            : Array.Empty<string>();

    public static bool EligibleClient(ApAgentClient client, IReadOnlyList<string> vaps)
        => !client.IsMlo && string.IsNullOrEmpty(client.MldMac) && client.Authorized
            && client.Band == "5" && client.Links.Count == 1
            && client.Links[0].Band == "5"
            && string.Equals(client.Links[0].Mac, client.Mac, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(client.Links[0].Ssid)
            && vaps.Contains(client.Links[0].Vap, StringComparer.Ordinal);

    public static List<ApAgentNativeCandidate> Candidates(IEnumerable<ApAgentNeighborReport> reports, string ssid)
    {
        var candidates = new List<ApAgentNativeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var neighborBytes = 0;
        foreach (var report in reports)
        {
            if (string.IsNullOrEmpty(report.Element) || report.Ssid != ssid || report.Security is not { Wpa: "2" } security
                || string.IsNullOrWhiteSpace(security.KeyMgmt) || string.IsNullOrWhiteSpace(security.Pairwise)) continue;
            byte[] element;
            try { element = Convert.FromHexString(report.Element); }
            catch (FormatException) { continue; }
            if (element.Length is < 13 or > 255 || element[0] % 2 != 0
                || element[10] is < 115 or > 130 || element[11] is < 36 or > 177 || element[12] == 0) continue;
            var bssid = string.Join(":", element.Take(6).Select(b => b.ToString("x2")));
            if (bssid == "00:00:00:00:00:00" || !bssid.Equals(report.Bssid, StringComparison.OrdinalIgnoreCase)) continue;
            var valid = true;
            for (var i = 13; i < element.Length;)
            {
                if (i + 2 > element.Length || i + 2 + element[i + 1] > element.Length) { valid = false; break; }
                i += 2 + element[i + 1];
            }
            if (!valid || !seen.Add(bssid)) continue;
            neighborBytes += element.Length + 2;
            candidates.Add(new() { Element = report.Element, Ssid = ssid, Security = security });
        }
        // Never silently truncate an intended set. A larger list needs an explicit selection policy.
        return candidates.Count <= 8 && neighborBytes <= 1000 ? candidates : new();
    }
}
