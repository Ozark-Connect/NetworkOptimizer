using System.Text.Json;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Acknowledge for ISP Health findings that carry an <see cref="IspHealthIssue.AckKey"/>: hidden from
/// the active list, still scored, listed under Acknowledged with Restore. Site Admin and site-scoped,
/// like the Wi-Fi Optimizer's Acknowledge.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IIspFindingAcknowledgmentService
{
    /// <summary>Keys of every acknowledged finding on this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<HashSet<string>> GetAcknowledgedKeysAsync();

    /// <summary>Acknowledges the finding with this key.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.IspFindingAcknowledged, Category = AuditCategories.Action, TargetType = "isp_finding")]
    Task AcknowledgeAsync(string ackKey);

    /// <summary>Restores the finding with this key to the active list.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.IspFindingRestored, Category = AuditCategories.Action, TargetType = "isp_finding")]
    Task RestoreAsync(string ackKey);
}

/// <inheritdoc cref="IIspFindingAcknowledgmentService" />
public class IspFindingAcknowledgmentService : IIspFindingAcknowledgmentService
{
    /// <summary>The site setting holding the acknowledged keys, as a JSON array.</summary>
    public const string SettingKey = "isp.acknowledged_findings";

    private readonly ISettingsRepository _settings;

    /// <param name="settings">This site's settings store.</param>
    public IspFindingAcknowledgmentService(ISettingsRepository settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetAcknowledgedKeysAsync()
    {
        var raw = await _settings.GetSystemSettingAsync(SettingKey);
        if (string.IsNullOrEmpty(raw)) return new(StringComparer.Ordinal);
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(raw) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public async Task AcknowledgeAsync(string ackKey)
    {
        var keys = await GetAcknowledgedKeysAsync();
        if (keys.Add(ackKey)) await SaveAsync(keys);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string ackKey)
    {
        var keys = await GetAcknowledgedKeysAsync();
        if (keys.Remove(ackKey)) await SaveAsync(keys);
    }

    private Task SaveAsync(HashSet<string> keys) =>
        _settings.SaveSystemSettingAsync(SettingKey,
            keys.Count == 0 ? null : JsonSerializer.Serialize(keys.OrderBy(k => k, StringComparer.Ordinal)));
}
