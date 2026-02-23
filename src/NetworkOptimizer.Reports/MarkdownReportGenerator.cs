using System.Text;

namespace NetworkOptimizer.Reports;

/// <summary>
/// Markdown report generator for network audit reports
/// Generates reports suitable for wikis, ticketing systems, and version control
/// </summary>
public class MarkdownReportGenerator
{
    private readonly BrandingOptions _branding;

    public MarkdownReportGenerator(BrandingOptions? branding = null)
    {
        _branding = branding ?? BrandingOptions.OzarkConnect();
    }

    /// <summary>
    /// Generate Markdown report and save to file
    /// </summary>
    public void GenerateReport(ReportData data, string outputPath)
    {
        var markdown = GenerateMarkdown(data);
        File.WriteAllText(outputPath, markdown);
    }

    /// <summary>
    /// Generate Markdown report as string
    /// </summary>
    public string GenerateMarkdown(ReportData data)
    {
        var sb = new StringBuilder();

        // Header
        ComposeHeader(sb, data);

        // Threat Intelligence Summary
        if (data.ThreatSummary != null && data.ThreatSummary.TotalEvents > 0)
        {
            ComposeThreatSummary(sb, data);
        }

        // Network Reference
        ComposeNetworkReference(sb, data);

        // Executive Summary
        ComposeExecutiveSummary(sb, data);

        // Action Items
        ComposeActionItems(sb, data);

        // Separator
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Switch Details
        ComposeSwitchDetails(sb, data);

        // Port Security Coverage Summary
        ComposePortSecuritySummary(sb, data);

        // Footer
        ComposeFooter(sb, data);

        return sb.ToString();
    }

    private void ComposeHeader(StringBuilder sb, ReportData data)
    {
        sb.AppendLine($"# {data.ClientName} Security Audit Report");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {data.GeneratedAt:MMMM dd, yyyy}");
        sb.AppendLine();
    }

    private void ComposeNetworkReference(StringBuilder sb, ReportData data)
    {
        sb.AppendLine("## Network Reference");
        sb.AppendLine();

        if (!data.Networks.Any())
        {
            sb.AppendLine("*No networks configured*");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Network | VLAN | Subnet |");
        sb.AppendLine("|---------|------|--------|");

        var sortedNetworks = data.Networks.OrderBy(n => n.VlanId).ToList();
        foreach (var network in sortedNetworks)
        {
            var vlanStr = network.VlanId == 1
                ? $"{network.VlanId} (native)"
                : network.VlanId.ToString();

            sb.AppendLine($"| {network.Name} | {vlanStr} | {network.Subnet} |");
        }

        sb.AppendLine();
    }

    private void ComposeExecutiveSummary(StringBuilder sb, ReportData data)
    {
        sb.AppendLine("## Executive Summary");
        sb.AppendLine();

        // Security Posture Rating
        var ratingText = data.SecurityScore.Rating switch
        {
            SecurityRating.Excellent => "**Overall Security Posture: EXCELLENT ✓**",
            SecurityRating.Good => "**Overall Security Posture: GOOD ✓**",
            SecurityRating.Fair => "**Overall Security Posture: FAIR ⚠**",
            SecurityRating.NeedsWork => "**Overall Security Posture: NEEDS ATTENTION ✗**",
            _ => "**Overall Security Posture: UNKNOWN**"
        };

        sb.AppendLine(ratingText);
        sb.AppendLine();

        // Hardening measures
        if (data.HardeningNotes.Any())
        {
            sb.AppendLine("Hardening measures already in place:");
            foreach (var note in data.HardeningNotes)
            {
                sb.AppendLine($"- {note}");
            }
            sb.AppendLine();
        }

        // Topology notes
        if (data.TopologyNotes.Any())
        {
            sb.AppendLine("Network topology notes:");
            foreach (var note in data.TopologyNotes)
            {
                sb.AppendLine($"- {note}");
            }
            sb.AppendLine();
        }
    }

    private void ComposeActionItems(StringBuilder sb, ReportData data)
    {
        sb.AppendLine("## Action Items");
        sb.AppendLine();

        if (!data.CriticalIssues.Any() && !data.RecommendedImprovements.Any())
        {
            sb.AppendLine("**No action items — all checks passed.** ✓");
            sb.AppendLine();
            return;
        }

        // Critical Issues
        if (data.CriticalIssues.Any())
        {
            sb.AppendLine($"### 🔴 Critical ({data.CriticalIssues.Count})");
            sb.AppendLine();
            sb.AppendLine("| Device | Port | Issue | Action |");
            sb.AppendLine("|--------|------|-------|--------|");

            foreach (var issue in data.CriticalIssues)
            {
                var portText = issue.PortIndex.HasValue
                    ? $"{issue.PortIndex} ({issue.PortName})"
                    : issue.PortName;

                sb.AppendLine($"| {issue.SwitchName} | {portText} | {issue.Message} | {issue.RecommendedAction} |");
            }

            sb.AppendLine();
        }

        // Recommended Improvements
        if (data.RecommendedImprovements.Any())
        {
            sb.AppendLine($"### 🟡 Recommended ({data.RecommendedImprovements.Count})");
            sb.AppendLine();
            sb.AppendLine("| Device | Port | Issue |");
            sb.AppendLine("|--------|------|-------|");

            foreach (var issue in data.RecommendedImprovements)
            {
                var portText = issue.PortIndex.HasValue
                    ? $"{issue.PortIndex} ({issue.PortName})"
                    : issue.PortName;

                sb.AppendLine($"| {issue.SwitchName} | {portText} | {issue.Message} |");
            }

            sb.AppendLine();
        }
    }

    private void ComposeSwitchDetails(StringBuilder sb, ReportData data)
    {
        foreach (var switchDevice in data.Switches)
        {
            var switchType = switchDevice.IsGateway ? "Gateway" : "Switch";
            sb.AppendLine($"## [{switchType}] {switchDevice.Name} ({switchDevice.ModelName})");
            sb.AppendLine();

            // Check if any port has isolation
            var hasIsolation = switchDevice.Ports.Any(p => p.Isolation);

            // Build table header
            if (hasIsolation)
            {
                sb.AppendLine("| Port | Name | Link | Forward | Native VLAN | PoE | Port Sec | Isolation | Status |");
                sb.AppendLine("|------|------|------|---------|-------------|-----|----------|-----------|--------|");
            }
            else
            {
                sb.AppendLine("| Port | Name | Link | Forward | Native VLAN | PoE | Port Sec | Status |");
                sb.AppendLine("|------|------|------|---------|-------------|-----|----------|--------|");
            }

            // Port rows
            foreach (var port in switchDevice.Ports)
            {
                var (status, statusType) = port.GetStatus(switchDevice.MaxCustomMacAcls > 0);

                var nativeVlan = port.NativeVlan.HasValue && !string.IsNullOrEmpty(port.NativeNetwork)
                    ? $"{port.NativeNetwork} ({port.NativeVlan})"
                    : port.Forward == "disabled" ? "—" : "";

                var forward = port.Forward == "customize" ? "custom" : port.Forward;

                if (hasIsolation)
                {
                    sb.AppendLine($"| {port.PortIndex} | {port.Name} | {port.GetLinkStatus()} | {forward} | {nativeVlan} | {port.GetPoeStatus()} | {port.GetPortSecurityStatus()} | {port.GetIsolationStatus()} | {status} |");
                }
                else
                {
                    sb.AppendLine($"| {port.PortIndex} | {port.Name} | {port.GetLinkStatus()} | {forward} | {nativeVlan} | {port.GetPoeStatus()} | {port.GetPortSecurityStatus()} | {status} |");
                }
            }

            sb.AppendLine();

            // Notes
            if (switchDevice.MaxCustomMacAcls == 0)
            {
                sb.AppendLine($"*Note: {switchDevice.ModelName} doesn't support MAC ACLs*");
                sb.AppendLine();
            }

            // Excluded networks note
            var portsWithExclusions = switchDevice.Ports.Where(p => p.ExcludedNetworks.Any()).ToList();
            if (portsWithExclusions.Any())
            {
                foreach (var port in portsWithExclusions)
                {
                    var excluded = string.Join(", ", port.ExcludedNetworks);
                    sb.AppendLine($"*Port {port.PortIndex} excludes: {excluded}*");
                }
                sb.AppendLine();
            }
        }
    }

    private void ComposePortSecuritySummary(StringBuilder sb, ReportData data)
    {
        sb.AppendLine("## Port Security Coverage Summary");
        sb.AppendLine();
        sb.AppendLine("| Switch | Total | Disabled | MAC Restricted | Unprotected Active |");
        sb.AppendLine("|--------|-------|----------|----------------|-------------------|");

        foreach (var switchDevice in data.Switches)
        {
            var macStr = switchDevice.MaxCustomMacAcls > 0
                ? switchDevice.MacRestrictedPorts.ToString()
                : "0 (no ACL support)";

            sb.AppendLine($"| {switchDevice.Name} | {switchDevice.TotalPorts} | {switchDevice.DisabledPorts} | {macStr} | {switchDevice.UnprotectedActivePorts} |");
        }

        sb.AppendLine();
    }

    private void ComposeThreatSummary(StringBuilder sb, ReportData data)
    {
        var threat = data.ThreatSummary!;

        sb.AppendLine("## Threat Intelligence");
        sb.AppendLine();
        sb.AppendLine($"*{threat.TimeRange}*");
        sb.AppendLine();

        // Summary stats
        sb.AppendLine("| Total Events | Blocked | Detected | Unique Sources |");
        sb.AppendLine("|-------------|---------|----------|----------------|");
        sb.AppendLine($"| {threat.TotalEvents:N0} | {threat.TotalBlocked:N0} | {threat.TotalDetected:N0} | {threat.UniqueSourceIps:N0} |");
        sb.AppendLine();

        // Kill chain
        if (threat.ByKillChain.Any())
        {
            sb.AppendLine("### Kill Chain Distribution");
            sb.AppendLine();
            foreach (var stage in threat.ByKillChain.OrderByDescending(k => k.Value))
            {
                var pct = threat.TotalEvents > 0 ? (double)stage.Value / threat.TotalEvents * 100 : 0;
                sb.AppendLine($"- **{stage.Key}**: {stage.Value:N0} ({pct:F0}%)");
            }
            sb.AppendLine();
        }

        // Top sources
        if (threat.TopSources.Any())
        {
            sb.AppendLine("### Top Threat Sources");
            sb.AppendLine();
            sb.AppendLine("| IP Address | Country | ASN | Events |");
            sb.AppendLine("|-----------|---------|-----|--------|");
            foreach (var source in threat.TopSources.Take(5))
            {
                sb.AppendLine($"| {source.Ip} | {source.CountryCode ?? "-"} | {source.AsnOrg ?? "-"} | {source.EventCount:N0} |");
            }
            sb.AppendLine();
        }

        // Exposed services
        if (threat.ExposedServices.Any())
        {
            sb.AppendLine("### Exposed Services Under Attack");
            sb.AppendLine();
            sb.AppendLine("| Port | Service | Forward To | Threats | Sources |");
            sb.AppendLine("|------|---------|-----------|---------|---------|");
            foreach (var svc in threat.ExposedServices)
            {
                sb.AppendLine($"| {svc.Port} | {svc.ServiceName} | {svc.ForwardTarget} | {svc.ThreatCount:N0} | {svc.UniqueSourceIps:N0} |");
            }
            sb.AppendLine();
        }
    }

    private void ComposeFooter(StringBuilder sb, ReportData data)
    {
        sb.AppendLine("---");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(_branding.CustomFooter))
        {
            sb.AppendLine(_branding.CustomFooter);
        }
        else if (_branding.ShowProductAttribution)
        {
            sb.AppendLine($"*Generated by {_branding.ProductName} for {_branding.CompanyName} | {data.GeneratedAt:yyyy-MM-dd HH:mm}*");
        }

        sb.AppendLine();
    }
}
