using NetworkOptimizer.Core.Models;

namespace NetworkOptimizer.Core.Interfaces;

/// <summary>
/// Interface for the network audit engine.
/// Provides methods for analyzing network configurations and identifying optimization opportunities.
/// </summary>
public interface IAuditEngine
{
    /// <summary>
    /// Performs a comprehensive audit of a network site.
    /// </summary>
    /// <param name="siteId">Site identifier to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Complete audit report with findings and scores.</returns>
    Task<AuditReport> PerformAuditAsync(string siteId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits device configurations for best practices and optimization opportunities.
    /// </summary>
    /// <param name="device">Device to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for the device.</returns>
    Task<List<AuditResult>> AuditDeviceAsync(UniFiDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits network configuration including VLANs, firewall rules, and routing.
    /// </summary>
    /// <param name="configuration">Network configuration to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for the network configuration.</returns>
    Task<List<AuditResult>> AuditNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits SQM configuration and performance.
    /// </summary>
    /// <param name="sqmConfig">SQM configuration to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for SQM configuration.</returns>
    Task<List<AuditResult>> AuditSqmConfigurationAsync(SqmConfiguration sqmConfig, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits wireless network configurations for security and performance.
    /// </summary>
    /// <param name="wirelessNetworks">Wireless networks to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for wireless configurations.</returns>
    Task<List<AuditResult>> AuditWirelessConfigurationAsync(List<WirelessNetwork> wirelessNetworks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits firewall rules for security best practices.
    /// </summary>
    /// <param name="firewallRules">Firewall rules to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for firewall rules.</returns>
    Task<List<AuditResult>> AuditFirewallRulesAsync(List<FirewallRule> firewallRules, CancellationToken cancellationToken = default);

    /// <summary>
    /// Audits switch port configurations.
    /// </summary>
    /// <param name="portConfigurations">Port configurations to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of audit findings for port configurations.</returns>
    Task<List<AuditResult>> AuditPortConfigurationsAsync(List<PortConfiguration> portConfigurations, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calculates an overall health score based on audit findings.
    /// </summary>
    /// <param name="findings">List of audit findings.</param>
    /// <returns>Health score from 0-100, where 100 is optimal.</returns>
    int CalculateHealthScore(List<AuditResult> findings);

    /// <summary>
    /// Generates optimization recommendations based on audit findings.
    /// </summary>
    /// <param name="findings">List of audit findings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Prioritized list of optimization recommendations.</returns>
    Task<List<OptimizationRecommendation>> GenerateRecommendationsAsync(List<AuditResult> findings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an optimization recommendation.
/// </summary>
public class OptimizationRecommendation
{
    /// <summary>
    /// Recommendation identifier.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Recommendation title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the recommendation.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Category of the recommendation.
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Priority level (1-5, where 1 is highest priority).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Expected impact of implementing the recommendation.
    /// </summary>
    public string ExpectedImpact { get; set; } = string.Empty;

    /// <summary>
    /// Estimated effort to implement (Low, Medium, High).
    /// </summary>
    public string ImplementationEffort { get; set; } = string.Empty;

    /// <summary>
    /// Step-by-step implementation instructions.
    /// </summary>
    public List<string> ImplementationSteps { get; set; } = new();

    /// <summary>
    /// Related audit finding identifiers.
    /// </summary>
    public List<string> RelatedFindings { get; set; } = new();

    /// <summary>
    /// Additional metadata for the recommendation.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}
