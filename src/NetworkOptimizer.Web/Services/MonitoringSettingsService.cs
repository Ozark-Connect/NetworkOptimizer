using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class MonitoringSettingsService : IMonitoringSettingsService
{
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly IAuditContext _audit;

    public MonitoringSettingsService(SiteDbContextFactory siteDb, SiteContextService siteContext, IAuditContext audit)
    {
        _siteDb = siteDb;
        _siteContext = siteContext;
        _audit = audit;
    }

    /// <summary>A non-positive temperature threshold means "use the default", stored as null.</summary>
    private static double? NormalizeTemp(double? value) => value is > 0 ? value : null;

    /// <inheritdoc />
    public Task<MonitoringSettings> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        SaveAsync(ct, s =>
        {
            var before = s.Enabled;
            s.Enabled = enabled;
            return before == enabled ? null : new { field = "Enabled", from = before, to = enabled };
        });

    /// <inheritdoc />
    public async Task ResetInfluxSetupAsync(CancellationToken ct = default)
    {
        await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
        if (settings == null)
        {
            _audit.SuppressNoChange();
            return;
        }

        settings.Enabled = false;
        settings.InfluxDbToken = "";
        settings.InfluxDbUrl = "";
        settings.InfluxDbOrg = "";
        settings.InfluxDbBucket = "";
        settings.InfluxDbLongtermBucket = "";
        settings.InfluxDbReachable = null;
        settings.LastInfluxDbError = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // No values in the detail: the cleared fields include the token.
        _audit.SetDetails(new { influxSetupReset = true });
    }

    /// <inheritdoc />
    public Task<MonitoringSettings> SaveTempThresholdsAsync(double? switchHighC, double? gatewayHighC,
        CancellationToken ct = default) =>
        SaveAsync(ct, s =>
        {
            var before = (s.SwitchTempHighC, s.GatewayTempHighC);
            s.SwitchTempHighC = NormalizeTemp(switchHighC);
            s.GatewayTempHighC = NormalizeTemp(gatewayHighC);
            var after = (s.SwitchTempHighC, s.GatewayTempHighC);
            return before == after ? null : new
            {
                switchTempHighC = new { from = before.SwitchTempHighC, to = after.SwitchTempHighC },
                gatewayTempHighC = new { from = before.GatewayTempHighC, to = after.GatewayTempHighC }
            };
        });

    /// <inheritdoc />
    public Task<MonitoringSettings> SaveSfpThresholdsAsync(SfpThresholdEdit edit, CancellationToken ct = default) =>
        SaveAsync(ct, s =>
        {
            var before = Snapshot(s);
            // Temperature must be positive; power thresholds may legitimately be
            // negative (RX low) so a blank field (null) is the only "use default".
            s.PonTempHighC = NormalizeTemp(edit.PonTempHighC);
            s.PonRxPowerLowDbm = edit.PonRxPowerLowDbm;
            s.PonTxPowerHighDbm = edit.PonTxPowerHighDbm;
            s.AeTempHighC = NormalizeTemp(edit.AeTempHighC);
            s.AeRxPowerLowDbm = edit.AeRxPowerLowDbm;
            s.AeTxPowerHighDbm = edit.AeTxPowerHighDbm;
            s.SfpTempHighGenericC = NormalizeTemp(edit.SfpTempHighGenericC);
            var after = Snapshot(s);
            return before == after ? null : new { from = before, to = after };
        });

    /// <inheritdoc />
    public Task<MonitoringSettings> SaveOntThresholdsAsync(double? ponTempHighC, double? ponRxPowerLowDbm,
        CancellationToken ct = default) =>
        SaveAsync(ct, s =>
        {
            var before = (s.PonTempHighC, s.PonRxPowerLowDbm);
            // Temperature must be positive; a blank RX field (null) means "use default".
            s.PonTempHighC = NormalizeTemp(ponTempHighC);
            s.PonRxPowerLowDbm = ponRxPowerLowDbm;
            var after = (s.PonTempHighC, s.PonRxPowerLowDbm);
            return before == after ? null : new
            {
                ponTempHighC = new { from = before.PonTempHighC, to = after.PonTempHighC },
                ponRxPowerLowDbm = new { from = before.PonRxPowerLowDbm, to = after.PonRxPowerLowDbm }
            };
        });

    private static (double?, double?, double?, double?, double?, double?, double?) Snapshot(MonitoringSettings s) =>
        (s.PonTempHighC, s.PonRxPowerLowDbm, s.PonTxPowerHighDbm,
         s.AeTempHighC, s.AeRxPowerLowDbm, s.AeTxPowerHighDbm, s.SfpTempHighGenericC);

    /// <summary>
    /// Loads the site's settings row (creating it on first save), applies an edit, and records the
    /// diff. A mutate that returns null changed nothing, so the row is left alone and the event is
    /// suppressed - saving a form whose values are already stored should not read as a
    /// configuration change.
    /// </summary>
    private async Task<MonitoringSettings> SaveAsync(CancellationToken ct, Func<MonitoringSettings, object?> mutate)
    {
        await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct) ?? new MonitoringSettings();
        if (settings.Id == 0) db.MonitoringSettings.Add(settings);

        var change = mutate(settings);
        if (change == null && settings.Id != 0)
        {
            _audit.SuppressNoChange();
            return settings;
        }

        settings.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _audit.SetDetails(change ?? new { created = true });
        return settings;
    }
}
