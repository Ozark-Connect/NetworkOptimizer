using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Analysis;

/// <summary>
/// Assigns a kill chain stage to each threat event based on signature category and action.
/// All rules are deterministic - no ML, every classification is explainable.
/// </summary>
public class KillChainClassifier
{
    // Category keywords for classification
    private static readonly string[] ReconKeywords = ["SCAN", "POLICY", "INFO", "ICMP", "RECON", "DISCOVERY"];
    private static readonly string[] ExploitKeywords = ["EXPLOIT", "CVE", "RCE", "OVERFLOW", "INJECTION", "SQLI", "XSS", "SHELLCODE", "ATTACK"];
    private static readonly string[] PostExploitKeywords = ["TROJAN", "MALWARE", "CNC", "C2", "COMMAND AND CONTROL", "BACKDOOR", "RAT", "EXFILTRATION", "BOTNET"];

    /// <summary>
    /// Classify a threat event into a kill chain stage.
    /// </summary>
    public KillChainStage Classify(ThreatEvent evt)
    {
        var category = evt.Category.ToUpperInvariant();
        var signature = evt.SignatureName.ToUpperInvariant();
        var combined = $"{category} {signature}";

        // Post-exploitation: C2, malware, trojans
        if (MatchesAny(combined, PostExploitKeywords))
            return KillChainStage.PostExploitation;

        // Exploitation: Check action to distinguish attempted vs active
        if (MatchesAny(combined, ExploitKeywords))
        {
            return evt.Action == ThreatAction.Blocked
                ? KillChainStage.AttemptedExploitation
                : KillChainStage.ActiveExploitation;
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
