using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// The health checks configured on a site's devices: what the Setup page lists, saves, tests and
/// removes. Saving and testing are Site Admin: a check can restart a service or reboot the device,
/// and a test runs an arbitrary command on it.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IHealthCheckService
{
    /// <summary>The checks configured on one device.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<HealthCheckDefinition>> GetForDeviceAsync(string deviceMac);

    /// <summary>Whether any check exists on the site, for the card's discovery nudge.</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> AnyAsync();

    /// <summary>
    /// Templates that fit a device. The device is looked up for its model so a Cloud Gateway
    /// template is withheld from a UXG; <paramref name="fallbackType"/> is used when UniFi does
    /// not currently list the device.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<HealthCheckTemplate>> GetTemplatesAsync(string deviceMac, DeviceType fallbackType);

    /// <summary>Live status of a check, or null before its first run.</summary>
    [RequireRole(Roles.Viewer)]
    Task<HealthCheckLiveStatus?> GetStatusAsync(int checkId);

    /// <summary>Creates or updates a check. Returns the saved row.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.HealthCheckChanged, TargetType = "health_check")]
    Task<HealthCheckDefinition> SaveAsync(HealthCheckDefinition draft);

    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.HealthCheckChanged, TargetType = "health_check")]
    Task<bool> DeleteAsync(int id);

    /// <summary>Runs the check's command once on the device, right now. Never runs the remedy.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.HealthCheckTested, TargetType = "health_check")]
    Task<HealthCheckRunResult> TestRunAsync(HealthCheckDefinition draft);
}

/// <inheritdoc cref="IHealthCheckService" />
public class HealthCheckService : IHealthCheckService
{
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly HealthCheckTemplateService _templates;
    private readonly HealthCheckRegistry _registry;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IUniFiSshService _deviceSsh;
    private readonly UniFiConnectionService _connection;

    public HealthCheckService(
        SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        HealthCheckTemplateService templates,
        HealthCheckRegistry registry,
        IGatewaySshService gatewaySsh,
        UniFiSshService deviceSsh,
        UniFiConnectionService connection)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _templates = templates;
        _registry = registry;
        _gatewaySsh = gatewaySsh;
        _deviceSsh = deviceSsh;
        _connection = connection;
    }

    /// <inheritdoc />
    public async Task<List<HealthCheckDefinition>> GetForDeviceAsync(string deviceMac)
    {
        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        return await db.HealthCheckDefinitions.AsNoTracking()
            .Where(c => c.DeviceMac == deviceMac)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> AnyAsync()
    {
        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        return await db.HealthCheckDefinitions.AnyAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthCheckTemplate>> GetTemplatesAsync(string deviceMac, DeviceType fallbackType)
    {
        var device = await FindDeviceAsync(deviceMac);
        // Hardware type, not role: a UDR meshing as an AP is still the UniFi OS console.
        var type = device == null ? fallbackType : HealthCheckRunner.EffectiveType(device);
        return _templates.GetTemplates()
            .Where(t => t.Fits(type, device?.Model, device?.Shortname))
            .ToList();
    }

    /// <inheritdoc />
    public Task<HealthCheckLiveStatus?> GetStatusAsync(int checkId) =>
        Task.FromResult(_registry.GetFor(_siteContext.Slug).GetStatus(checkId));

    /// <inheritdoc />
    public async Task<HealthCheckDefinition> SaveAsync(HealthCheckDefinition draft)
    {
        var device = await FindDeviceAsync(draft.DeviceMac);
        Validate(draft, device == null ? DeviceType.Unknown : HealthCheckRunner.EffectiveType(device));

        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var now = DateTime.UtcNow;

        var duplicate = await db.HealthCheckDefinitions
            .AnyAsync(c => c.DeviceMac == draft.DeviceMac && c.FieldName == draft.FieldName && c.Id != draft.Id);
        if (duplicate)
            throw new InvalidOperationException($"Another check on this device already stores its value as '{draft.FieldName}'.");

        HealthCheckDefinition row;
        if (draft.Id > 0)
        {
            row = await db.HealthCheckDefinitions.FindAsync(draft.Id)
                ?? throw new InvalidOperationException("That check no longer exists.");
        }
        else
        {
            row = new HealthCheckDefinition { CreatedAt = now };
            db.HealthCheckDefinitions.Add(row);
        }

        row.DeviceMac = draft.DeviceMac;
        row.Name = draft.Name.Trim();
        row.FieldName = draft.FieldName;
        row.TemplateId = string.IsNullOrWhiteSpace(draft.TemplateId) ? null : draft.TemplateId;
        row.Enabled = draft.Enabled;
        row.IntervalSeconds = Math.Max(30, draft.IntervalSeconds);
        row.Command = draft.Command.Trim();
        row.TimeoutSeconds = Math.Clamp(draft.TimeoutSeconds, 5, 120);
        row.Parser = draft.Parser;
        row.ParserArg = string.IsNullOrWhiteSpace(draft.ParserArg) ? null : draft.ParserArg.Trim();
        row.Operator = draft.Operator;
        row.Threshold = draft.Threshold;
        row.ConsecutiveSamples = Math.Max(1, draft.ConsecutiveSamples);
        row.NotApplicablePattern = string.IsNullOrWhiteSpace(draft.NotApplicablePattern) ? null : draft.NotApplicablePattern.Trim();
        row.AlertEnabled = draft.AlertEnabled;
        row.AlertSeverity = draft.AlertSeverity;
        row.Remedy = draft.Remedy;
        row.RemedyArg = HealthCheckRemedies.NeedsArgument(draft.Remedy) ? draft.RemedyArg?.Trim() : null;
        row.RemedyCooldownSeconds = Math.Max(60, draft.RemedyCooldownSeconds);
        row.RemedyMaxPerDay = Math.Max(0, draft.RemedyMaxPerDay);
        row.Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim();
        row.UpdatedAt = now;
        if (device != null) HealthCheckTarget.Remember(row, device, now);

        await db.SaveChangesAsync();
        _registry.GetFor(_siteContext.Slug).InvalidateDefinitions();
        return row;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id)
    {
        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var row = await db.HealthCheckDefinitions.FindAsync(id);
        if (row == null) return false;
        db.HealthCheckDefinitions.Remove(row);
        await db.SaveChangesAsync();
        _registry.GetFor(_siteContext.Slug).InvalidateDefinitions();
        return true;
    }

    /// <inheritdoc />
    public async Task<HealthCheckRunResult> TestRunAsync(HealthCheckDefinition draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Command))
            return new HealthCheckRunResult { Ran = false, Error = "Enter a command to run first." };

        var device = await FindDeviceAsync(draft.DeviceMac);
        var target = device != null
            ? HealthCheckTarget.FromDevice(device, draft)
            : await FindLastKnownAsync(draft.DeviceMac);
        if (target == null)
            return new HealthCheckRunResult { Ran = false, Error = "UniFi does not currently list this device, so there is nowhere to run it." };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(draft.TimeoutSeconds, 5, 120) + 15));
        return await HealthCheckExecutor.RunAsync(draft, target.EffectiveType, target.Host, _gatewaySsh, _deviceSsh, cts.Token);
    }

    /// <summary>The last-known device from any saved check on it, for a test while UniFi Network is down.</summary>
    private async Task<HealthCheckTarget?> FindLastKnownAsync(string deviceMac)
    {
        await using var db = _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        var row = await db.HealthCheckDefinitions.AsNoTracking()
            .Where(c => c.DeviceMac == deviceMac && c.LastKnownDeviceType != null)
            .OrderByDescending(c => c.LastKnownAt)
            .FirstOrDefaultAsync();
        return row == null ? null : HealthCheckTarget.FromCheck(row);
    }

    private void Validate(HealthCheckDefinition draft, DeviceType deviceType)
    {
        if (string.IsNullOrWhiteSpace(draft.DeviceMac))
            throw new InvalidOperationException("The check needs a device.");
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new InvalidOperationException("Give the check a name.");
        if (string.IsNullOrWhiteSpace(draft.Command))
            throw new InvalidOperationException("Enter the command to run.");
        if (string.IsNullOrWhiteSpace(draft.FieldName))
            draft.FieldName = HealthCheckEvaluation.SlugifyFieldName(draft.Name);
        if (!HealthCheckEvaluation.FieldNamePattern.IsMatch(draft.FieldName))
            throw new InvalidOperationException("The field name can only use lowercase letters, digits, and underscores.");
        if (draft.Parser is HealthCheckParser.RegexGroup or HealthCheckParser.MatchingLineCount
            && string.IsNullOrWhiteSpace(draft.ParserArg))
            throw new InvalidOperationException("That way of reading the value needs a regular expression.");
        if (HealthCheckRemedies.NeedsArgument(draft.Remedy) && !HealthCheckRemedies.IsValidArgument(draft.RemedyArg))
            throw new InvalidOperationException(draft.Remedy == HealthCheckRemedy.RestartService
                ? "Enter the service name to restart (letters, digits, dots, dashes, and underscores)."
                : "Enter the process name to kill (letters, digits, dots, dashes, and underscores).");
        if (deviceType != DeviceType.Unknown && !HealthCheckRemedies.SupportedOn(draft.Remedy, deviceType))
            throw new InvalidOperationException($"{HealthCheckRemedies.Label(draft.Remedy)} is not available on this device type.");

        var template = _templates.GetTemplate(draft.TemplateId);
        if (template != null && !template.Allows(draft.Remedy))
            throw new InvalidOperationException($"The {template.Name} template does not offer \"{HealthCheckRemedies.Label(draft.Remedy)}\".");
    }

    private async Task<UniFi.DiscoveredDevice?> FindDeviceAsync(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac) || !_connection.IsConnected) return null;
        try
        {
            var wanted = mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
            var devices = await _connection.GetDiscoveredDevicesAsync();
            return devices?.FirstOrDefault(d => !string.IsNullOrEmpty(d.Mac)
                && d.Mac.Replace(":", "").Replace("-", "").ToLowerInvariant() == wanted);
        }
        catch
        {
            return null;
        }
    }
}
