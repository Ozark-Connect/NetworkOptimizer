using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <inheritdoc cref="IWiFiInsightRepository" />
public class WiFiInsightRepository : IWiFiInsightRepository
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly Services.SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<WiFiInsightRepository> _logger;
    private readonly string _siteSlug;
    private readonly bool _isDefault;

    public WiFiInsightRepository(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        Services.SiteDbContextFactory siteDbFactory,
        ILogger<WiFiInsightRepository> logger,
        string siteSlug = "",
        bool isDefault = true)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _siteDbFactory = siteDbFactory ?? throw new ArgumentNullException(nameof(siteDbFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _siteSlug = siteSlug ?? string.Empty;
        _isDefault = isDefault;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteDb(CancellationToken ct)
    {
        if (!_isDefault)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetAcknowledgedIssueKeysAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
        var keys = await db.WiFiIssueAcknowledgments.AsNoTracking()
            .Select(a => a.IssueKey)
            .ToListAsync(cancellationToken);
        return new HashSet<string>(keys, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task AcknowledgeIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey)) return;
        await using var db = await CreateSiteDb(cancellationToken);
        if (await db.WiFiIssueAcknowledgments.AnyAsync(a => a.IssueKey == issueKey, cancellationToken))
            return;
        db.WiFiIssueAcknowledgments.Add(new WiFiIssueAcknowledgment { IssueKey = issueKey, AcknowledgedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Acknowledged Wi-Fi issue {IssueKey}", issueKey);
    }

    /// <inheritdoc />
    public async Task RestoreIssueAsync(string issueKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(issueKey)) return;
        await using var db = await CreateSiteDb(cancellationToken);
        var row = await db.WiFiIssueAcknowledgments.FirstOrDefaultAsync(a => a.IssueKey == issueKey, cancellationToken);
        if (row == null) return;
        db.WiFiIssueAcknowledgments.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Restored Wi-Fi issue {IssueKey}", issueKey);
    }

    /// <inheritdoc />
    public async Task<List<(string ApMac, string Band)>> GetKeptRadiosAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await CreateSiteDb(cancellationToken);
        var rows = await db.WiFiRadioPreferences.AsNoTracking()
            .Where(p => p.KeepChannelSince != null)
            .Select(p => new { p.ApMac, p.Band })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.ApMac, r.Band)).ToList();
    }

    /// <inheritdoc />
    public async Task SetKeptAsync(string apMac, string band, bool kept, CancellationToken cancellationToken = default)
    {
        var mac = (apMac ?? string.Empty).Trim().ToLowerInvariant();
        var bandCode = (band ?? string.Empty).Trim().ToLowerInvariant();
        if (mac.Length == 0 || bandCode.Length == 0) return;

        await using var db = await CreateSiteDb(cancellationToken);
        var row = await db.WiFiRadioPreferences.FirstOrDefaultAsync(p => p.ApMac == mac && p.Band == bandCode, cancellationToken);
        var now = DateTime.UtcNow;
        if (row == null)
        {
            if (!kept) return;
            db.WiFiRadioPreferences.Add(new WiFiRadioPreference { ApMac = mac, Band = bandCode, KeepChannelSince = now, UpdatedAt = now });
        }
        else
        {
            row.KeepChannelSince = kept ? (row.KeepChannelSince ?? now) : null;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Radio {ApMac} {Band} {State}", mac, bandCode, kept ? "kept on its channel" : "released");
    }
}
