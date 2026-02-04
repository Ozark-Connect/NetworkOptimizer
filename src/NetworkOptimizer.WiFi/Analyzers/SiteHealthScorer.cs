using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Analyzers;

/// <summary>
/// Calculates the overall site health score from Wi-Fi data.
/// Configurable weights and thresholds.
/// </summary>
public class SiteHealthScorer
{
    private readonly SiteHealthScorerOptions _options;

    public SiteHealthScorer(SiteHealthScorerOptions? options = null)
    {
        _options = options ?? new SiteHealthScorerOptions();
    }

    /// <summary>
    /// Calculate site health score from current Wi-Fi data
    /// </summary>
    public SiteHealthScore Calculate(
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot> clients,
        RoamingTopology? roamingData = null)
    {
        var score = new SiteHealthScore
        {
            Timestamp = DateTimeOffset.UtcNow,
            Stats = CalculateStats(aps, clients, roamingData)
        };

        // Calculate each dimension
        score.SignalQuality = CalculateSignalQuality(clients);
        score.ChannelHealth = CalculateChannelHealth(aps);
        score.RoamingPerformance = CalculateRoamingPerformance(roamingData);
        score.AirtimeEfficiency = CalculateAirtimeEfficiency(aps, clients);
        score.ClientSatisfaction = CalculateClientSatisfaction(aps, clients);
        score.CapacityHeadroom = CalculateCapacityHeadroom(aps);

        // Collect issues from all dimensions
        CollectIssues(score, aps, clients, roamingData);

        // Calculate weighted overall score
        score.OverallScore = (int)Math.Round(
            score.SignalQuality.Score * score.SignalQuality.Weight +
            score.ChannelHealth.Score * score.ChannelHealth.Weight +
            score.RoamingPerformance.Score * score.RoamingPerformance.Weight +
            score.AirtimeEfficiency.Score * score.AirtimeEfficiency.Weight +
            score.ClientSatisfaction.Score * score.ClientSatisfaction.Weight +
            score.CapacityHeadroom.Score * score.CapacityHeadroom.Weight
        );

        return score;
    }

    private HealthSummaryStats CalculateStats(
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot> clients,
        RoamingTopology? roamingData)
    {
        var stats = new HealthSummaryStats
        {
            TotalAps = aps.Count,
            TotalClients = clients.Count,
            ClientsOn2_4GHz = clients.Count(c => c.Band == RadioBand.Band2_4GHz),
            ClientsOn5GHz = clients.Count(c => c.Band == RadioBand.Band5GHz),
            ClientsOn6GHz = clients.Count(c => c.Band == RadioBand.Band6GHz)
        };

        if (clients.Any())
        {
            var satisfactions = clients.Where(c => c.Satisfaction.HasValue).Select(c => c.Satisfaction!.Value);
            stats.AvgSatisfaction = satisfactions.Any() ? satisfactions.Average() : 0;

            var signals = clients.Where(c => c.Signal.HasValue).Select(c => c.Signal!.Value);
            stats.AvgSignalStrength = signals.Any() ? signals.Average() : 0;

            stats.WeakSignalClients = clients.Count(c => c.Signal.HasValue && c.Signal.Value < _options.WeakSignalThreshold);
            stats.LegacyClients = clients.Count(c => IsLegacyClient(c));
        }

        if (aps.Any())
        {
            var radios2g = aps.SelectMany(a => a.Radios).Where(r => r.Band == RadioBand.Band2_4GHz && r.ChannelUtilization.HasValue);
            var radios5g = aps.SelectMany(a => a.Radios).Where(r => r.Band == RadioBand.Band5GHz && r.ChannelUtilization.HasValue);
            var radios6g = aps.SelectMany(a => a.Radios).Where(r => r.Band == RadioBand.Band6GHz && r.ChannelUtilization.HasValue);

            stats.AvgChannelUtilization2_4GHz = radios2g.Any() ? radios2g.Average(r => r.ChannelUtilization!.Value) : 0;
            stats.AvgChannelUtilization5GHz = radios5g.Any() ? radios5g.Average(r => r.ChannelUtilization!.Value) : 0;
            stats.AvgChannelUtilization6GHz = radios6g.Any() ? radios6g.Average(r => r.ChannelUtilization!.Value) : 0;
        }

        if (roamingData != null)
        {
            stats.TotalRoamsLast24h = roamingData.Edges.Sum(e => e.TotalRoamAttempts);
            var totalAttempts = roamingData.Edges.Sum(e => e.TotalRoamAttempts);
            var totalSuccess = roamingData.Edges.Sum(e => e.TotalSuccessfulRoams);
            stats.RoamSuccessRate = totalAttempts > 0 ? (double)totalSuccess / totalAttempts * 100 : 100;
        }

        return stats;
    }

    private ScoreDimension CalculateSignalQuality(List<WirelessClientSnapshot> clients)
    {
        var dimension = new ScoreDimension
        {
            Name = "Signal Quality",
            Weight = _options.SignalQualityWeight
        };

        if (!clients.Any())
        {
            dimension.Score = 100;
            dimension.Status = "No clients connected";
            return dimension;
        }

        var clientsWithSignal = clients.Where(c => c.Signal.HasValue).ToList();
        if (!clientsWithSignal.Any())
        {
            dimension.Score = 80;
            dimension.Status = "No signal data available";
            return dimension;
        }

        // Score based on signal distribution
        var excellent = clientsWithSignal.Count(c => c.Signal >= _options.ExcellentSignalThreshold);
        var good = clientsWithSignal.Count(c => c.Signal >= _options.GoodSignalThreshold && c.Signal < _options.ExcellentSignalThreshold);
        var fair = clientsWithSignal.Count(c => c.Signal >= _options.WeakSignalThreshold && c.Signal < _options.GoodSignalThreshold);
        var poor = clientsWithSignal.Count(c => c.Signal < _options.WeakSignalThreshold);

        var total = clientsWithSignal.Count;
        var score = (excellent * 100 + good * 80 + fair * 50 + poor * 20) / total;

        dimension.Score = Math.Max(0, Math.Min(100, score));
        dimension.Status = poor > 0 ? $"{poor} clients with weak signal" : "All clients have good signal";

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Excellent signal (>-50 dBm)",
            Value = $"{excellent} clients ({excellent * 100 / total}%)",
            Impact = excellent > 0 ? 10 : 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Good signal (-50 to -65 dBm)",
            Value = $"{good} clients ({good * 100 / total}%)",
            Impact = 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Weak signal (<-75 dBm)",
            Value = $"{poor} clients ({poor * 100 / total}%)",
            Impact = poor > 0 ? -20 : 0
        });

        return dimension;
    }

    private ScoreDimension CalculateChannelHealth(List<AccessPointSnapshot> aps)
    {
        var dimension = new ScoreDimension
        {
            Name = "Channel Health",
            Weight = _options.ChannelHealthWeight
        };

        if (!aps.Any())
        {
            dimension.Score = 100;
            dimension.Status = "No access points";
            return dimension;
        }

        var allRadios = aps.SelectMany(a => a.Radios).ToList();
        if (!allRadios.Any())
        {
            dimension.Score = 80;
            dimension.Status = "No radio data available";
            return dimension;
        }

        var radiosWithUtilization = allRadios.Where(r => r.ChannelUtilization.HasValue).ToList();
        var radiosWithInterference = allRadios.Where(r => r.Interference.HasValue).ToList();

        var avgUtilization = radiosWithUtilization.Any() ? radiosWithUtilization.Average(r => r.ChannelUtilization!.Value) : 0;
        var avgInterference = radiosWithInterference.Any() ? radiosWithInterference.Average(r => r.Interference!.Value) : 0;
        var highUtilRadios = radiosWithUtilization.Count(r => r.ChannelUtilization > _options.HighUtilizationThreshold);

        // Score: 100 - utilization penalty - interference penalty
        var utilizationPenalty = avgUtilization > 50 ? (avgUtilization - 50) : 0;
        var interferencePenalty = avgInterference > 20 ? (avgInterference - 20) * 0.5 : 0;

        dimension.Score = Math.Max(0, Math.Min(100, (int)(100 - utilizationPenalty - interferencePenalty)));
        dimension.Status = highUtilRadios > 0
            ? $"{highUtilRadios} radios with high utilization"
            : "Channel utilization is healthy";

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Average channel utilization",
            Value = $"{avgUtilization:F1}%",
            Impact = avgUtilization > 70 ? -20 : avgUtilization > 50 ? -10 : 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Average interference",
            Value = $"{avgInterference:F1}%",
            Impact = avgInterference > 30 ? -15 : avgInterference > 15 ? -5 : 0
        });

        return dimension;
    }

    private ScoreDimension CalculateRoamingPerformance(RoamingTopology? roamingData)
    {
        var dimension = new ScoreDimension
        {
            Name = "Roaming Performance",
            Weight = _options.RoamingPerformanceWeight
        };

        if (roamingData == null || !roamingData.Edges.Any())
        {
            dimension.Score = 100;
            dimension.Status = "No roaming data available";
            return dimension;
        }

        var totalAttempts = roamingData.Edges.Sum(e => e.TotalRoamAttempts);
        var totalSuccess = roamingData.Edges.Sum(e => e.TotalSuccessfulRoams);
        var successRate = totalAttempts > 0 ? (double)totalSuccess / totalAttempts * 100 : 100;

        var fastRoamingCount = roamingData.Edges.Sum(e =>
            e.Endpoint1ToEndpoint2.FastRoaming + e.Endpoint2ToEndpoint1.FastRoaming);
        var fastRoamingPct = totalSuccess > 0 ? (double)fastRoamingCount / totalSuccess * 100 : 0;

        // Score based on success rate primarily
        dimension.Score = (int)Math.Round(successRate);

        // Bonus for fast roaming usage
        if (fastRoamingPct > 50) dimension.Score = Math.Min(100, dimension.Score + 5);

        dimension.Status = successRate < 95
            ? $"{100 - successRate:F1}% roam failures"
            : "Roaming is healthy";

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Roam success rate",
            Value = $"{successRate:F1}%",
            Impact = successRate < 90 ? -20 : successRate < 95 ? -10 : 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Fast roaming (802.11r) usage",
            Value = $"{fastRoamingPct:F1}%",
            Impact = fastRoamingPct > 50 ? 5 : 0,
            Description = fastRoamingPct < 20 ? "Consider enabling fast roaming" : null
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Total roams (24h)",
            Value = totalAttempts.ToString(),
            Impact = 0
        });

        return dimension;
    }

    private ScoreDimension CalculateAirtimeEfficiency(List<AccessPointSnapshot> aps, List<WirelessClientSnapshot> clients)
    {
        var dimension = new ScoreDimension
        {
            Name = "Airtime Efficiency",
            Weight = _options.AirtimeEfficiencyWeight
        };

        if (!clients.Any())
        {
            dimension.Score = 100;
            dimension.Status = "No clients to analyze";
            return dimension;
        }

        var legacyCount = clients.Count(IsLegacyClient);
        var legacyPct = (double)legacyCount / clients.Count * 100;

        // Check for clients on 2.4GHz that could be on 5GHz
        var on2g = clients.Count(c => c.Band == RadioBand.Band2_4GHz);
        var on5gOr6g = clients.Count(c => c.Band == RadioBand.Band5GHz || c.Band == RadioBand.Band6GHz);

        // Calculate TX retry impact from APs
        var avgTxRetries = aps
            .SelectMany(a => a.Radios)
            .Where(r => r.TxRetriesPct.HasValue)
            .Select(r => r.TxRetriesPct!.Value)
            .DefaultIfEmpty(0)
            .Average();

        // Score: start at 100, subtract for issues
        var score = 100.0;
        score -= legacyPct * 0.5; // Legacy clients hurt airtime
        score -= avgTxRetries * 0.5; // High retries indicate inefficiency

        dimension.Score = Math.Max(0, Math.Min(100, (int)Math.Round(score)));
        dimension.Status = legacyCount > 0
            ? $"{legacyCount} legacy clients affecting airtime"
            : "Airtime usage is efficient";

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Legacy clients (Wi-Fi 4 or older)",
            Value = $"{legacyCount} ({legacyPct:F1}%)",
            Impact = legacyCount > 0 ? -(int)(legacyPct * 0.5) : 0,
            Description = legacyCount > 0 ? "Legacy clients consume more airtime for the same data" : null
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Average TX retry rate",
            Value = $"{avgTxRetries:F1}%",
            Impact = avgTxRetries > 10 ? -15 : avgTxRetries > 5 ? -5 : 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Band distribution",
            Value = $"2.4GHz: {on2g}, 5/6GHz: {on5gOr6g}",
            Impact = 0
        });

        return dimension;
    }

    private ScoreDimension CalculateClientSatisfaction(List<AccessPointSnapshot> aps, List<WirelessClientSnapshot> clients)
    {
        var dimension = new ScoreDimension
        {
            Name = "Client Satisfaction",
            Weight = _options.ClientSatisfactionWeight
        };

        // Use UniFi's satisfaction scores where available
        var clientSatisfactions = clients
            .Where(c => c.Satisfaction.HasValue)
            .Select(c => c.Satisfaction!.Value)
            .ToList();

        var apSatisfactions = aps
            .Where(a => a.Satisfaction.HasValue)
            .Select(a => a.Satisfaction!.Value)
            .ToList();

        if (!clientSatisfactions.Any() && !apSatisfactions.Any())
        {
            dimension.Score = 80;
            dimension.Status = "No satisfaction data available";
            return dimension;
        }

        var avgClientSat = clientSatisfactions.Any() ? clientSatisfactions.Average() : 0;
        var avgApSat = apSatisfactions.Any() ? apSatisfactions.Average() : 0;

        // Weight client satisfaction more heavily
        dimension.Score = (int)Math.Round(
            clientSatisfactions.Any() && apSatisfactions.Any()
                ? avgClientSat * 0.7 + avgApSat * 0.3
                : clientSatisfactions.Any() ? avgClientSat : avgApSat
        );

        var lowSatClients = clientSatisfactions.Count(s => s < 50);
        dimension.Status = lowSatClients > 0
            ? $"{lowSatClients} clients with low satisfaction"
            : "Client experience is good";

        if (clientSatisfactions.Any())
        {
            dimension.Factors.Add(new ScoreFactor
            {
                Name = "Average client satisfaction",
                Value = $"{avgClientSat:F0}%",
                Impact = avgClientSat < 50 ? -20 : avgClientSat < 70 ? -10 : 0
            });
        }

        if (apSatisfactions.Any())
        {
            dimension.Factors.Add(new ScoreFactor
            {
                Name = "Average AP satisfaction",
                Value = $"{avgApSat:F0}%",
                Impact = avgApSat < 50 ? -10 : avgApSat < 70 ? -5 : 0
            });
        }

        return dimension;
    }

    private ScoreDimension CalculateCapacityHeadroom(List<AccessPointSnapshot> aps)
    {
        var dimension = new ScoreDimension
        {
            Name = "Capacity Headroom",
            Weight = _options.CapacityHeadroomWeight
        };

        if (!aps.Any())
        {
            dimension.Score = 100;
            dimension.Status = "No access points";
            return dimension;
        }

        // Check client distribution across APs
        var clientCounts = aps.Select(a => a.TotalClients).ToList();
        var maxClients = clientCounts.Max();
        var avgClients = clientCounts.Average();

        // Check for imbalanced load
        var imbalanceRatio = avgClients > 0 ? maxClients / avgClients : 1;
        var overloadedAps = aps.Count(a => a.TotalClients > _options.HighClientCountThreshold);

        // Score: penalize overload and imbalance
        var score = 100.0;
        if (overloadedAps > 0) score -= overloadedAps * 15;
        if (imbalanceRatio > 2) score -= (imbalanceRatio - 2) * 10;

        dimension.Score = Math.Max(0, Math.Min(100, (int)Math.Round(score)));
        dimension.Status = overloadedAps > 0
            ? $"{overloadedAps} APs with high client count"
            : "Capacity is well distributed";

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Max clients on single AP",
            Value = maxClients.ToString(),
            Impact = maxClients > _options.HighClientCountThreshold ? -15 : 0
        });

        dimension.Factors.Add(new ScoreFactor
        {
            Name = "Load balance ratio",
            Value = $"{imbalanceRatio:F1}x",
            Impact = imbalanceRatio > 3 ? -15 : imbalanceRatio > 2 ? -10 : 0,
            Description = imbalanceRatio > 2 ? "Some APs have significantly more clients than others" : null
        });

        return dimension;
    }

    private void CollectIssues(
        SiteHealthScore score,
        List<AccessPointSnapshot> aps,
        List<WirelessClientSnapshot> clients,
        RoamingTopology? roamingData)
    {
        // Signal issues
        var weakSignalClients = clients.Where(c => c.Signal.HasValue && c.Signal.Value < _options.WeakSignalThreshold).ToList();
        foreach (var client in weakSignalClients.Take(5))
        {
            score.Issues.Add(new HealthIssue
            {
                Severity = client.Signal < -80 ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning,
                Dimension = "Signal Quality",
                Title = "Weak signal",
                Description = $"Client has weak signal ({client.Signal} dBm)",
                AffectedEntity = client.Name,
                Recommendation = "Move closer to AP or add additional coverage",
                ScoreImpact = -5
            });
        }

        // Channel utilization issues
        var highUtilRadios = aps
            .SelectMany(a => a.Radios.Select(r => (Ap: a, Radio: r)))
            .Where(x => x.Radio.ChannelUtilization > _options.HighUtilizationThreshold)
            .ToList();

        foreach (var (ap, radio) in highUtilRadios.Take(5))
        {
            // Tailor recommendation based on band
            var recommendation = radio.Band == RadioBand.Band2_4GHz
                ? "Enable band steering to move capable clients to 5 GHz or 6 GHz"
                : "Try a different channel or reduce channel width";

            score.Issues.Add(new HealthIssue
            {
                Severity = radio.ChannelUtilization > 90 ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning,
                Dimension = "Channel Health",
                Title = "High channel utilization",
                Description = $"{radio.Band.ToDisplayString()} radio at {radio.ChannelUtilization}% utilization",
                AffectedEntity = ap.Name,
                Recommendation = recommendation,
                ScoreImpact = -10
            });
        }

        // Roaming issues
        if (roamingData != null)
        {
            var failedRoamEdges = roamingData.Edges
                .Where(e => e.TotalRoamAttempts > e.TotalSuccessfulRoams)
                .ToList();

            foreach (var edge in failedRoamEdges.Take(3))
            {
                var ap1Name = roamingData.Vertices.FirstOrDefault(v => v.Mac == edge.Endpoint1Mac)?.Name ?? edge.Endpoint1Mac;
                var ap2Name = roamingData.Vertices.FirstOrDefault(v => v.Mac == edge.Endpoint2Mac)?.Name ?? edge.Endpoint2Mac;
                var failures = edge.TotalRoamAttempts - edge.TotalSuccessfulRoams;

                score.Issues.Add(new HealthIssue
                {
                    Severity = failures > 5 ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning,
                    Dimension = "Roaming Performance",
                    Title = "Roaming failures",
                    Description = $"{failures} failed roams between these APs",
                    AffectedEntity = $"{ap1Name} ↔ {ap2Name}",
                    Recommendation = "Check for coverage gaps or enable fast roaming",
                    ScoreImpact = -5 * failures
                });
            }
        }

        // Legacy client issues
        var legacyClients = clients.Where(IsLegacyClient).ToList();
        if (legacyClients.Count > 3)
        {
            score.Issues.Add(new HealthIssue
            {
                Severity = HealthIssueSeverity.Info,
                Dimension = "Airtime Efficiency",
                Title = "Legacy clients detected",
                Description = $"{legacyClients.Count} clients using Wi-Fi 4 or older",
                Recommendation = "Legacy clients consume more airtime - consider upgrading or isolating to separate SSID",
                ScoreImpact = -5
            });
        }
    }

    private bool IsLegacyClient(WirelessClientSnapshot client)
    {
        // Wi-Fi 4 (802.11n) = "n", Wi-Fi 5 (802.11ac) = "ac", Wi-Fi 6 = "ax", Wi-Fi 7 = "be"
        var proto = client.WifiProtocol?.ToLowerInvariant();
        return proto switch
        {
            "a" or "b" or "g" or "n" => true,
            _ => false
        };
    }
}

/// <summary>
/// Configuration options for site health scoring
/// </summary>
public class SiteHealthScorerOptions
{
    // Dimension weights (must sum to 1.0)
    public double SignalQualityWeight { get; set; } = 0.20;
    public double ChannelHealthWeight { get; set; } = 0.20;
    public double RoamingPerformanceWeight { get; set; } = 0.15;
    public double AirtimeEfficiencyWeight { get; set; } = 0.15;
    public double ClientSatisfactionWeight { get; set; } = 0.20;
    public double CapacityHeadroomWeight { get; set; } = 0.10;

    // Signal thresholds (dBm)
    public int ExcellentSignalThreshold { get; set; } = -50;
    public int GoodSignalThreshold { get; set; } = -65;
    public int WeakSignalThreshold { get; set; } = -75;

    // Utilization thresholds (%)
    public int HighUtilizationThreshold { get; set; } = 70;

    // Client count thresholds
    public int HighClientCountThreshold { get; set; } = 30;
}
