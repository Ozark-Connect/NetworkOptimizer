using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Assigns a kill chain stage to each threat event based on signature category and action.
/// All rules are deterministic - no ML, every classification is explainable.
/// </summary>
public class KillChainClassifier
{
    // Category keywords for classification (IPS events)
    private static readonly string[] ReconKeywords = ["SCAN", "POLICY", "INFO", "ICMP", "RECON", "DISCOVERY"];
    private static readonly string[] ExploitKeywords = ["EXPLOIT", "CVE", "RCE", "OVERFLOW", "INJECTION", "SQLI", "XSS", "SHELLCODE", "ATTACK"];
    private static readonly string[] PostExploitKeywords = ["TROJAN", "MALWARE", "CNC", "C2", "COMMAND AND CONTROL", "BACKDOOR", "RAT", "EXFILTRATION", "BOTNET"];

    // Sensitive ports for flow classification
    private static readonly HashSet<int> SensitivePorts = new()
    {
        22, 23, 25, 445, 1433, 1521, 3306, 3389, 5432, 5900, 5985, 5986, 6379, 8080, 8443, 27017
    };

    /// <summary>
    /// Classify a threat event into a kill chain stage.
    /// </summary>
    public KillChainStage Classify(ThreatEvent evt)
    {
        // Info-level events are explicitly allowed traffic - not threats, just monitored
        if (evt.Severity <= 1)
            return KillChainStage.Monitored;

        return evt.EventSource == EventSource.TrafficFlow
            ? ClassifyFlow(evt)
            : ClassifyIps(evt);
    }

    private KillChainStage ClassifyIps(ThreatEvent evt)
    {
        var category = evt.Category.ToUpperInvariant();
        var signature = evt.SignatureName.ToUpperInvariant();
        var combined = $"{category} {signature}";

        // Post-exploitation: C2, malware, trojans
        if (MatchesAny(combined, PostExploitKeywords))
            return KillChainStage.PostExploitation;

        // Exploitation: Check action to distinguish attempted vs active
        // Low/Info severity (1-2) can't be Active Exploitation - downgrade to Attempted
        if (MatchesAny(combined, ExploitKeywords))
        {
            if (evt.Action == ThreatAction.Blocked || evt.Severity <= 2)
                return KillChainStage.AttemptedExploitation;
            return KillChainStage.ActiveExploitation;
        }

        // Reconnaissance: Scans, policy violations, info gathering
        if (MatchesAny(combined, ReconKeywords))
            return KillChainStage.Reconnaissance;

        // Default: classify based on severity and action
        if (evt.Severity >= 4 && evt.Action == ThreatAction.Detected)
            return KillChainStage.ActiveExploitation;

        if (evt.Severity >= 4)
            return KillChainStage.AttemptedExploitation;

        return KillChainStage.Reconnaissance;
    }

    private KillChainStage ClassifyFlow(ThreatEvent evt)
    {
        var isIncoming = "incoming".Equals(evt.Direction, StringComparison.OrdinalIgnoreCase);
        var isOutgoing = "outgoing".Equals(evt.Direction, StringComparison.OrdinalIgnoreCase);
        var isBlocked = evt.Action == ThreatAction.Blocked;
        var isSensitivePort = SensitivePorts.Contains(evt.DestPort);
        var isHighRisk = "high".Equals(evt.RiskLevel, StringComparison.OrdinalIgnoreCase);

        // Outgoing + high risk -> likely data exfiltration or C2
        if (isOutgoing && isHighRisk)
            return KillChainStage.PostExploitation;

        // Incoming + allowed + sensitive port -> active exploitation (severity 3+ only)
        if (isIncoming && !isBlocked && isSensitivePort)
            return evt.Severity <= 2 ? KillChainStage.AttemptedExploitation : KillChainStage.ActiveExploitation;

        // Incoming + blocked + sensitive port -> attempted exploitation
        if (isIncoming && isBlocked && isSensitivePort)
            return KillChainStage.AttemptedExploitation;

        // Incoming + blocked -> reconnaissance
        if (isIncoming && isBlocked)
            return KillChainStage.Reconnaissance;

        // Default: classify by severity
        if (evt.Severity >= 4 && !isBlocked)
            return KillChainStage.ActiveExploitation;

        if (evt.Severity >= 4)
            return KillChainStage.AttemptedExploitation;

        return KillChainStage.Reconnaissance;
    }

    private static bool MatchesAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
