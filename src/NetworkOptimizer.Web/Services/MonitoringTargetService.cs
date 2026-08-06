using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class MonitoringTargetService : IMonitoringTargetService
{
    private static readonly Regex HostnamePattern = new(@"^[a-zA-Z0-9][a-zA-Z0-9.\-]*$", RegexOptions.Compiled);
    private const int MaxFieldLength = 200;

    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly AsnResolutionService _asnResolution;
    private readonly ProbeExecutorFactory _executorFactory;
    private readonly IAuditContext _audit;
    private readonly ILogger<MonitoringTargetService> _logger;

    public MonitoringTargetService(
        SiteDbContextFactory siteDb,
        SiteContextService siteContext,
        AsnResolutionService asnResolution,
        ProbeExecutorFactory executorFactory,
        IAuditContext audit,
        ILogger<MonitoringTargetService> logger)
    {
        _siteDb = siteDb;
        _siteContext = siteContext;
        _asnResolution = asnResolution;
        _executorFactory = executorFactory;
        _audit = audit;
        _logger = logger;
    }

    private NetworkOptimizerDbContext CreateDb() => _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <inheritdoc />
    public async Task<MonitoringTarget> AddAsync(NewMonitoringTarget spec, CancellationToken ct = default)
    {
        var name = (spec.Name ?? "").Trim();
        var address = (spec.Address ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address))
            throw new MonitoringTargetValidationException("Name and address are required.");
        if (name.Length > MaxFieldLength || address.Length > MaxFieldLength)
            throw new MonitoringTargetValidationException($"Name and address must be under {MaxFieldLength} characters.");
        if (!IPAddress.TryParse(address, out _) && !HostnamePattern.IsMatch(address))
            throw new MonitoringTargetValidationException("Address must be a valid IP or hostname.");

        var (asnNumber, asnName) = await ResolveAsnAsync(spec.TargetType, address);
        // Settled once, here, while the user is waiting anyway. A failure leaves it unresolved
        // rather than guessed - the sweep will try again, and readers fall back meanwhile.
        var isLocal = await Monitoring.LocalTargetResolver.ResolveAsync(address, ct);

        // Manual Transit/Access ISP adds are user-provided upstream hops the tracer didn't
        // find. Tag them UserProvided so upstream change-detection treats them as curated
        // (never flags them as "removed", never re-suggests them as "added") while ISP
        // Health still grades them by TargetType. Generic customs stay unmethoded (null).
        var discoveryMethod = spec.TargetType is MonitoringTargetType.Transit or MonitoringTargetType.AccessIsp
            ? (DiscoveryMethod?)DiscoveryMethod.UserProvided
            : null;

        var entity = new MonitoringTarget
        {
            TargetId = $"custom-{Guid.NewGuid():N}"[..24],
            Name = name,
            Address = address,
            TargetType = spec.TargetType,
            ProbeMode = spec.ProbeMode,
            Port = spec.ProbeMode == ProbeMode.Tcp ? spec.Port : null,
            PollIntervalSeconds = spec.PollIntervalSeconds,
            PingCount = 5,
            Enabled = true,
            AutoDiscovered = false,
            DiscoveryMethod = discoveryMethod,
            VantagePoint = "server",
            CreatedAt = DateTime.UtcNow,
            AsnNumber = asnNumber,
            AsnName = asnName,
            IsLocal = isLocal
        };

        // Same stamping the reassign path uses, so a target carries both keys from its first poll:
        // the context that routes the probe and the WAN the readings are filed under. Added against
        // no context, it is unpinned and says so.
        string? contextWanInterface = null;
        if (spec.WanContextId is int newContextId)
        {
            await using var contextDb = CreateDb();
            var context = await contextDb.WanContexts.FindAsync(new object?[] { newContextId }, ct);
            if (context == null)
                throw new MonitoringTargetValidationException("That WAN context no longer exists.");
            contextWanInterface = context.WanInterface;
        }
        Monitoring.WanContextTargetStamping.ApplyAssignment(entity, spec.WanContextId, contextWanInterface);

        await using (var db = CreateDb())
        {
            db.MonitoringTargets.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        _audit.SetTarget(entity.TargetId, entity.Name);
        _audit.SetDetails(new
        {
            address = entity.Address,
            targetType = entity.TargetType.ToString(),
            probeMode = entity.ProbeMode.ToString(),
            entity.Port,
            entity.PollIntervalSeconds,
            entity.AsnNumber,
            entity.WanContextId
        });

        // Trace-on-save: an Internet/Custom target only absolves the ISP/transit hops it crosses
        // once it has trace ancestry. Kick off an immediate trace so it can act as a witness now,
        // instead of waiting for the next upstream discovery cycle. Resolve the "server" vantage
        // executor here (synchronously, current site context) so external agent sites trace over
        // the on-site agent tunnel and there's no scoped-context race in the fire-and-forget.
        if (spec.TargetType is MonitoringTargetType.InternetService or MonitoringTargetType.Custom)
            _ = TraceOnSaveForAncestryAsync(_executorFactory.GetServer(), entity.Id, address,
                _siteContext.Slug, _siteContext.IsDefault);

        return entity;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = CreateDb();
        var row = await db.MonitoringTargets.FindAsync(new object?[] { id }, ct);
        if (row == null)
        {
            _audit.SuppressNoChange();
            return false;
        }

        _audit.SetTarget(row.TargetId, row.Name);
        _audit.SetDetails(new { deleted = true, address = row.Address, targetType = row.TargetType.ToString() });

        db.MonitoringTargets.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default) =>
        UpdateAsync(id, ct, row =>
        {
            if (row.Enabled == enabled) return null;
            var before = row.Enabled;
            row.Enabled = enabled;
            return new { field = "Enabled", from = before, to = enabled };
        });

    /// <inheritdoc />
    public Task<bool> SetPollIntervalAsync(int id, int seconds, CancellationToken ct = default) =>
        UpdateAsync(id, ct, row =>
        {
            if (row.PollIntervalSeconds == seconds) return null;
            var before = row.PollIntervalSeconds;
            row.PollIntervalSeconds = seconds;
            return new { field = "PollIntervalSeconds", from = before, to = seconds };
        });

    /// <inheritdoc />
    public Task<bool> DismissLanFlakyHintAsync(int id, CancellationToken ct = default) =>
        UpdateAsync(id, ct, row =>
        {
            if (row.LanFlakyHintDismissedAt != null) return null;
            row.LanFlakyHintDismissedAt = DateTime.UtcNow;
            return new { field = "LanFlakyHintDismissedAt", dismissed = true };
        });

    /// <inheritdoc />
    public async Task<bool> SetWanContextAsync(int id, int? wanContextId, CancellationToken ct = default)
    {
        // The context's WAN rides along with the assignment: WanContextId routes the probes and
        // WanInterface says which WAN the data describes, and every per-WAN reader scopes on the
        // latter - an assignment that moved only the routing would keep grading the data under
        // the old WAN. Moving off a context makes it unpinned (see WanContextTargetStamping).
        string? contextWanInterface = null;
        if (wanContextId is int contextId)
        {
            await using var db = CreateDb();
            contextWanInterface = (await db.WanContexts.FindAsync(new object?[] { contextId }, ct))?.WanInterface;
        }
        return await UpdateAsync(id, ct, row =>
        {
            if (row.WanContextId == wanContextId) return null;
            var before = row.WanContextId;
            Monitoring.WanContextTargetStamping.ApplyAssignment(row, wanContextId, contextWanInterface);
            return new { field = "WanContextId", from = before, to = wanContextId };
        });
    }

    /// <summary>
    /// Applies a single-field edit and records what actually changed. A mutate that returns null
    /// made no change, so nothing is saved and the event is suppressed outright - re-selecting the
    /// interval a target already has is not a configuration change, and an Audit Log full of
    /// entries that changed nothing is the thing that makes the real ones hard to find.
    /// </summary>
    private async Task<bool> UpdateAsync(int id, CancellationToken ct, Func<MonitoringTarget, object?> mutate)
    {
        await using var db = CreateDb();
        var row = await db.MonitoringTargets.FindAsync(new object?[] { id }, ct);
        if (row == null)
        {
            _audit.SuppressNoChange();
            return false;
        }

        var change = mutate(row);
        if (change == null)
        {
            _audit.SuppressNoChange();
            return true;
        }

        await db.SaveChangesAsync(ct);
        _audit.SetTarget(row.TargetId, row.Name);
        _audit.SetDetails(change);
        return true;
    }

    private async Task<(int? AsnNumber, string? AsnName)> ResolveAsnAsync(MonitoringTargetType targetType, string address)
    {
        if (targetType is not (MonitoringTargetType.Transit or MonitoringTargetType.AccessIsp))
            return (null, null);

        var ip = address;
        if (!IPAddress.TryParse(address, out _))
        {
            try
            {
                var entries = await Dns.GetHostAddressesAsync(address);
                ip = entries.FirstOrDefault()?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not resolve {Address} for ASN lookup; adding the target without one", address);
                ip = null;
            }
        }
        if (ip == null) return (null, null);

        var asn = await _asnResolution.ResolveAsync(ip);
        if (asn == null) return (null, null);

        // Apply the same industry-suffix cleanup auto-discovery uses
        // (UpstreamTracerService.CleanAsnName), so a manually-added transit hop
        // stores "Level 3", not "Level 3 Parent" / "Lumen", matching discovery.
        return (asn.Asn, NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName(asn.Name));
    }

    /// <summary>
    /// Traces a just-added Internet/Custom target over the site's server/agent vantage and persists
    /// its hop ancestry as an UpstreamDiscovery row, so ISP Health can immediately use it as a
    /// routes-through witness. Best-effort; fire-and-forget (reads no request state).
    /// </summary>
    private async Task TraceOnSaveForAncestryAsync(NetworkOptimizer.Monitoring.Probes.IProbeExecutor executor,
        int targetId, string address, string slug, bool isDefault)
    {
        try
        {
            await using var db = _siteDb.CreateForSite(slug, isDefault);
            await TargetAncestry.TraceAndPersistAsync(executor, db, targetId, address);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trace-on-save for target {TargetId} failed; ancestry will fill in on the next discovery", targetId);
        }
    }
}
