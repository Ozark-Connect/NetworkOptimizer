using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Acknowledging a Wi-Fi Optimizer issue: hidden from the active list, still scored, listed
/// under Acknowledged with Restore. Site Admin, like Security Audit's Acknowledge, and
/// site-scoped because the issues are.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWiFiIssueAcknowledgmentService
{
    /// <summary>Keys of every acknowledged issue on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<HashSet<string>> GetAcknowledgedKeysAsync();

    /// <summary>Acknowledges the issue with this key.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiIssueAcknowledged, Category = AuditCategories.Action, TargetType = "wifi_issue")]
    Task AcknowledgeAsync(string issueKey);

    /// <summary>Restores the issue with this key to the active list.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiIssueRestored, Category = AuditCategories.Action, TargetType = "wifi_issue")]
    Task RestoreAsync(string issueKey);
}

/// <inheritdoc cref="IWiFiIssueAcknowledgmentService" />
public class WiFiIssueAcknowledgmentService : IWiFiIssueAcknowledgmentService
{
    private readonly IWiFiInsightRepository _repository;

    /// <param name="repository">This site's insight store.</param>
    public WiFiIssueAcknowledgmentService(IWiFiInsightRepository repository)
    {
        _repository = repository;
    }

    /// <inheritdoc />
    public Task<HashSet<string>> GetAcknowledgedKeysAsync() => _repository.GetAcknowledgedIssueKeysAsync();

    /// <inheritdoc />
    public Task AcknowledgeAsync(string issueKey) => _repository.AcknowledgeIssueAsync(issueKey);

    /// <inheritdoc />
    public Task RestoreAsync(string issueKey) => _repository.RestoreIssueAsync(issueKey);
}

/// <summary>
/// Keep: the operator's answer to the Channel Recommendation engine for one radio. A kept
/// radio is a constraint on the plan, the way a mesh child is; it hides nothing about the
/// score. Site Admin, because it changes what the site is advised to do.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWiFiRadioKeepService
{
    /// <summary>Every kept radio on this site, as (AP MAC lowercase, band code).</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<(string ApMac, string Band)>> GetKeptRadiosAsync();

    /// <summary>Keeps a radio on its current channel.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiRadioKept, Category = AuditCategories.Action, TargetType = "radio")]
    Task KeepAsync(string apMac, RadioBand band);

    /// <summary>Stops keeping a radio.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiRadioReleased, Category = AuditCategories.Action, TargetType = "radio")]
    Task ReleaseAsync(string apMac, RadioBand band);
}

/// <inheritdoc cref="IWiFiRadioKeepService" />
public class WiFiRadioKeepService : IWiFiRadioKeepService
{
    private readonly IWiFiInsightRepository _repository;

    /// <param name="repository">This site's insight store.</param>
    public WiFiRadioKeepService(IWiFiInsightRepository repository)
    {
        _repository = repository;
    }

    /// <inheritdoc />
    public Task<List<(string ApMac, string Band)>> GetKeptRadiosAsync() => _repository.GetKeptRadiosAsync();

    /// <inheritdoc />
    public Task KeepAsync(string apMac, RadioBand band) => _repository.SetKeptAsync(apMac, band.ToUniFiCode(), kept: true);

    /// <inheritdoc />
    public Task ReleaseAsync(string apMac, RadioBand band) => _repository.SetKeptAsync(apMac, band.ToUniFiCode(), kept: false);
}
