using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Models;
using Xunit;

namespace NetworkOptimizer.Threats.Tests;

public class KillChainClassifierTests
{
    private readonly KillChainClassifier _classifier = new();

    private static ThreatEvent CreateEvent(
        string category = "",
        string signatureName = "",
        ThreatAction action = ThreatAction.Detected,
        int severity = 3)
    {
        return new ThreatEvent
        {
            InnerAlertId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            SourceIp = "192.0.2.10",
            DestIp = "198.51.100.1",
            DestPort = 80,
            Protocol = "TCP",
            Category = category,
            SignatureName = signatureName,
            Action = action,
            Severity = severity
        };
    }

    // --- Reconnaissance ---

    [Theory]
    [InlineData("SCAN")]
    [InlineData("Attempted Information Leak SCAN")]
    [InlineData("POLICY Violation")]
    [InlineData("ICMP Probe")]
    [InlineData("Network INFO Gathering")]
    [InlineData("RECON Activity")]
    [InlineData("DISCOVERY attempt")]
    public void Classify_ScanCategory_ReturnsReconnaissance(string category)
    {
        var evt = CreateEvent(category: category);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    [Fact]
    public void Classify_ScanInSignatureName_ReturnsReconnaissance()
    {
        var evt = CreateEvent(category: "Misc", signatureName: "ET SCAN Nmap SYN scan");
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    // --- AttemptedExploitation (exploit + blocked) ---

    [Theory]
    [InlineData("EXPLOIT")]
    [InlineData("CVE-2024-1234")]
    [InlineData("RCE Attempt")]
    [InlineData("Buffer OVERFLOW")]
    [InlineData("SQL INJECTION")]
    [InlineData("SQLI attempt")]
    [InlineData("XSS Reflected")]
    [InlineData("SHELLCODE detected")]
    [InlineData("Web ATTACK")]
    public void Classify_ExploitCategoryBlocked_ReturnsAttemptedExploitation(string category)
    {
        var evt = CreateEvent(category: category, action: ThreatAction.Blocked);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.AttemptedExploitation, result);
    }

    // --- ActiveExploitation (exploit + detected) ---

    [Theory]
    [InlineData("EXPLOIT")]
    [InlineData("CVE-2024-5678")]
    [InlineData("RCE Attempt")]
    public void Classify_ExploitCategoryDetected_ReturnsActiveExploitation(string category)
    {
        var evt = CreateEvent(category: category, action: ThreatAction.Detected);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.ActiveExploitation, result);
    }

    // --- PostExploitation ---

    [Theory]
    [InlineData("A Network TROJAN was Detected")]
    [InlineData("MALWARE")]
    [InlineData("CNC Traffic")]
    [InlineData("C2 Beacon")]
    [InlineData("COMMAND AND CONTROL")]
    [InlineData("BACKDOOR Activity")]
    [InlineData("RAT Communication")]
    [InlineData("EXFILTRATION Attempt")]
    [InlineData("BOTNET Activity")]
    public void Classify_PostExploitCategory_ReturnsPostExploitation(string category)
    {
        var evt = CreateEvent(category: category);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.PostExploitation, result);
    }

    [Fact]
    public void Classify_TrojanInSignatureName_ReturnsPostExploitation()
    {
        var evt = CreateEvent(category: "Misc", signatureName: "ET TROJAN Win32/AgentTesla");
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.PostExploitation, result);
    }

    // --- PostExploitation takes priority over Exploit keywords ---

    [Fact]
    public void Classify_PostExploitAndExploitKeywords_ReturnsPostExploitation()
    {
        // Category has both TROJAN and EXPLOIT - post-exploitation should win
        var evt = CreateEvent(category: "TROJAN EXPLOIT", action: ThreatAction.Blocked);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.PostExploitation, result);
    }

    // --- Default classification by severity ---

    [Fact]
    public void Classify_UnknownCategoryHighSeverityDetected_ReturnsActiveExploitation()
    {
        var evt = CreateEvent(category: "Unknown Category", severity: 4, action: ThreatAction.Detected);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.ActiveExploitation, result);
    }

    [Fact]
    public void Classify_UnknownCategoryHighSeverityBlocked_ReturnsAttemptedExploitation()
    {
        var evt = CreateEvent(category: "Unknown Category", severity: 4, action: ThreatAction.Blocked);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.AttemptedExploitation, result);
    }

    [Fact]
    public void Classify_UnknownCategoryCriticalSeverityDetected_ReturnsActiveExploitation()
    {
        var evt = CreateEvent(category: "Unknown Category", severity: 5, action: ThreatAction.Detected);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.ActiveExploitation, result);
    }

    [Fact]
    public void Classify_UnknownCategoryLowSeverity_ReturnsReconnaissance()
    {
        var evt = CreateEvent(category: "Unknown Category", severity: 2, action: ThreatAction.Detected);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    [Fact]
    public void Classify_UnknownCategoryMediumSeverity_ReturnsReconnaissance()
    {
        var evt = CreateEvent(category: "Something Unusual", severity: 3, action: ThreatAction.Detected);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    // --- Case insensitivity: category is uppercased before matching ---

    [Fact]
    public void Classify_LowercaseScan_ReturnsReconnaissance()
    {
        var evt = CreateEvent(category: "scan detection");
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    [Fact]
    public void Classify_MixedCaseExploit_ReturnsCorrectStage()
    {
        var evt = CreateEvent(category: "Exploit Attempt", action: ThreatAction.Blocked);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.AttemptedExploitation, result);
    }

    // --- Flow classification ---

    private static ThreatEvent CreateFlowEvent(
        string direction = "incoming",
        string riskLevel = "low",
        ThreatAction action = ThreatAction.Detected,
        int destPort = 80,
        int severity = 3)
    {
        return new ThreatEvent
        {
            InnerAlertId = $"flow-{Guid.NewGuid()}",
            Timestamp = DateTime.UtcNow,
            SourceIp = "192.0.2.10",
            DestIp = "198.51.100.1",
            DestPort = destPort,
            Protocol = "TCP",
            Category = $"{riskLevel} risk {direction} HTTPS",
            SignatureName = $"Flow: HTTPS {direction} allowed",
            Action = action,
            Severity = severity,
            EventSource = EventSource.TrafficFlow,
            Direction = direction,
            RiskLevel = riskLevel,
            Service = "HTTPS"
        };
    }

    [Fact]
    public void Classify_FlowOutgoingHighRisk_ReturnsPostExploitation()
    {
        var evt = CreateFlowEvent(direction: "outgoing", riskLevel: "high");
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.PostExploitation, result);
    }

    [Fact]
    public void Classify_FlowIncomingAllowedSensitivePort_ReturnsActiveExploitation()
    {
        var evt = CreateFlowEvent(direction: "incoming", action: ThreatAction.Detected, destPort: 22);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.ActiveExploitation, result);
    }

    [Fact]
    public void Classify_FlowIncomingBlockedSensitivePort_ReturnsAttemptedExploitation()
    {
        var evt = CreateFlowEvent(direction: "incoming", action: ThreatAction.Blocked, destPort: 3389);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.AttemptedExploitation, result);
    }

    [Fact]
    public void Classify_FlowIncomingBlockedNormalPort_ReturnsReconnaissance()
    {
        var evt = CreateFlowEvent(direction: "incoming", action: ThreatAction.Blocked, destPort: 80);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    [Fact]
    public void Classify_FlowHighSeverityAllowed_ReturnsActiveExploitation()
    {
        var evt = CreateFlowEvent(direction: "outgoing", riskLevel: "low", severity: 4, action: ThreatAction.Detected, destPort: 443);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.ActiveExploitation, result);
    }

    [Fact]
    public void Classify_FlowLowRiskOutgoing_ReturnsReconnaissance()
    {
        var evt = CreateFlowEvent(direction: "outgoing", riskLevel: "low", severity: 1, destPort: 443);
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }

    [Fact]
    public void Classify_IpsEventStillUsesKeywords()
    {
        // Ensure IPS events still use keyword-based classification
        var evt = CreateEvent(category: "SCAN Activity");
        evt.EventSource = EventSource.Ips;
        var result = _classifier.Classify(evt);
        Assert.Equal(KillChainStage.Reconnaissance, result);
    }
}
