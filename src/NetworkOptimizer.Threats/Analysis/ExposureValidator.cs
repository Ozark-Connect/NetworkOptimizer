using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Cross-references threat events with port forward rules to identify exposed services under attack.
/// This is the key differentiator: we don't just show threats, we show which of YOUR exposed
/// services are being targeted and how.
/// </summary>
public class ExposureValidator
{
    private readonly ILogger<ExposureValidator> _logger;

    private static readonly Dictionary<int, string> WellKnownPorts = new()
    {
        [20] = "FTP Data", [21] = "FTP", [22] = "SSH", [23] = "Telnet",
        [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [110] = "POP3",
        [143] = "IMAP", [443] = "HTTPS", [993] = "IMAPS", [995] = "POP3S",
        [1433] = "MSSQL", [3306] = "MySQL", [3389] = "RDP", [5432] = "PostgreSQL",
        [5900] = "VNC", [8080] = "HTTP Proxy", [8443] = "HTTPS Alt"
    };

    public ExposureValidator(ILogger<ExposureValidator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Build an exposure report by matching threats to port forward rules.
    /// </summary>
    public async Task<ExposureReport> ValidateAsync(
        List<UniFiPortForwardRule>? portForwardRules,
        IThreatRepository repository,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var report = new ExposureReport();

        if (portForwardRules == null || portForwardRules.Count == 0)
        {
            _logger.LogDebug("No port forward rules to validate");
            return report;
        }

        // Get threat counts by destination port
        var threatsByPort = await repository.GetThreatCountsByPortAsync(from, to, cancellationToken);

        // Get events for exposed ports to build per-service details
        var exposedPorts = portForwardRules
            .SelectMany(r => ParsePorts(r.DstPort))
            .Distinct()
            .ToHashSet();

        var totalThreats = 0;

        foreach (var rule in portForwardRules)
        {
            var ports = ParsePorts(rule.DstPort);
            foreach (var port in ports)
            {
                var threats = threatsByPort.GetValueOrDefault(port, 0);
                if (threats == 0) continue;

                totalThreats += threats;

                // Get detailed data for this port
                var portEvents = await repository.GetEventsAsync(from, to, destPort: port, limit: 500, cancellationToken: cancellationToken);

                var service = new ExposedService
                {
                    Port = port,
                    Protocol = rule.Proto ?? "tcp",
                    ServiceName = GetServiceName(port, rule.Name),
                    ForwardTarget = $"{rule.Fwd}:{rule.FwdPort ?? port.ToString()}",
                    RuleName = rule.Name,
                    ThreatCount = threats,
                    UniqueSourceIps = portEvents.Select(e => e.SourceIp).Distinct().Count(),
                    TopSignatures = portEvents
                        .GroupBy(e => e.SignatureName)
                        .OrderByDescending(g => g.Count())
                        .Take(5)
                        .Select(g => g.Key)
                        .ToList(),
                    SeverityBreakdown = portEvents
                        .GroupBy(e => e.Severity)
                        .ToDictionary(g => g.Key, g => g.Count())
                };

                report.ExposedServices.Add(service);
            }
        }

        report.TotalExposedPorts = report.ExposedServices.Count;
        report.TotalThreatsTargetingExposed = totalThreats;

        // Calculate geo-block recommendation
        report.GeoBlockRecommendation = await CalculateGeoBlockRecommendation(repository, from, to, cancellationToken);

        return report;
    }

    private async Task<GeoBlockRecommendation?> CalculateGeoBlockRecommendation(
        IThreatRepository repository, DateTime from, DateTime to, CancellationToken ct)
    {
        var countryDist = await repository.GetCountryDistributionAsync(from, to, ct);
        if (countryDist.Count == 0) return null;

        var totalThreats = countryDist.Values.Sum();
        if (totalThreats < 10) return null;

        // Find countries that contribute >5% of threats each
        var significantCountries = countryDist
            .Where(c => (double)c.Value / totalThreats >= 0.05)
            .OrderByDescending(c => c.Value)
            .Take(5)
            .ToList();

        if (significantCountries.Count == 0) return null;

        var preventable = significantCountries.Sum(c => c.Value);
        var percentage = (double)preventable / totalThreats * 100;

        return new GeoBlockRecommendation
        {
            Countries = significantCountries.Select(c => c.Key).ToList(),
            PreventionPercentage = Math.Round(percentage, 1),
            Description = $"Blocking {string.Join(", ", significantCountries.Select(c => c.Key))} would have prevented {percentage:F0}% of threats"
        };
    }

    private static string GetServiceName(int port, string? ruleName)
    {
        if (!string.IsNullOrEmpty(ruleName)) return ruleName;
        return WellKnownPorts.GetValueOrDefault(port, $"Port {port}");
    }

    private static List<int> ParsePorts(string? portSpec)
    {
        if (string.IsNullOrEmpty(portSpec)) return [];

        var ports = new List<int>();
        foreach (var part in portSpec.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Contains('-'))
            {
                var range = trimmed.Split('-');
                if (int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end))
                {
                    for (var p = start; p <= end && p <= start + 100; p++) // Cap range
                        ports.Add(p);
                }
            }
            else if (int.TryParse(trimmed, out var port))
            {
                ports.Add(port);
            }
        }
        return ports;
    }
}
