using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.DeviceRestart;

/// <summary>A device's restart state for its card: an open request, or one that failed in the last day.</summary>
/// <param name="Mac">Normalized device MAC.</param>
/// <param name="Status">Where the request is.</param>
/// <param name="Mode">When it was asked to run.</param>
/// <param name="DueAtUtc">When it runs.</param>
/// <param name="Reason">Why it waits or failed.</param>
public sealed record DeviceRestartBadge(string Mac, DeviceRestartStatus Status, DeviceRestartMode Mode, DateTime DueAtUtc, string? Reason);

/// <summary>An open request, as the restart sheet shows it.</summary>
public sealed record DeviceRestartPending(
    DeviceRestartMode Mode, DateTime DueAtUtc, DeviceRestartStatus Status, string? Reason,
    string? RequestedBy, DeviceRestartMethod RequestedMethod, string? RunsWith);

/// <summary>Everything the restart sheet shows for one device.</summary>
public sealed class DeviceRestartSheetState
{
    public string Mac { get; init; } = "";
    public string Name { get; init; } = "";
    public DeviceType Type { get; init; }
    public bool IsOnline { get; init; }

    /// <summary>Why the device may not restart now, or null.</summary>
    public string? Lockout { get; init; }

    /// <summary>True when SSH credentials cover the route this device takes.</summary>
    public bool HasSsh { get; init; }

    /// <summary>Devices below it in the uplink tree.</summary>
    public int DevicesBelow { get; init; }

    /// <summary>Clients on it or below it; null when the console's client list did not load.</summary>
    public int? ClientsAffected { get; init; }

    /// <summary>The PoE port that powers it, when one does.</summary>
    public PoePort? PoePort { get; init; }

    /// <summary>The open request for it, or null.</summary>
    public DeviceRestartPending? Pending { get; init; }

    /// <summary>Restarts this app completed for it in the last 30 days.</summary>
    public int RestartsLast30Days { get; init; }

    /// <summary>The console's timezone id, for showing and reading times in the site's hours.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>Null when the console answered; otherwise why the sheet has nothing to show.</summary>
    public string? Error { get; init; }
}

/// <summary>A quiet-hour proposal for one device.</summary>
public sealed record DeviceRestartQuietHour(DateTime StartUtc, string Basis, string? TimeZoneId);

/// <summary>The outcome of a request: null error on success.</summary>
public sealed record DeviceRestartResult(string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Restart a UniFi device from its Dashboard card: now, at a time, or at the next quiet hour.
/// Requests go to the database; <see cref="DeviceRestartExecutor"/> runs them.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IDeviceRestartService
{
    /// <summary>Restart state per device for the cards: open requests and failures from the last day.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<string, DeviceRestartBadge>> GetBadgesAsync(CancellationToken ct = default);

    /// <summary>What the restart sheet shows for a device.</summary>
    [RequireRole(Roles.Admin)]
    Task<DeviceRestartSheetState> GetSheetAsync(string mac, CancellationToken ct = default);

    /// <summary>The next quiet hour for a device and everything below it.</summary>
    [RequireRole(Roles.Admin)]
    Task<DeviceRestartQuietHour?> ProposeQuietAsync(string mac, CancellationToken ct = default);

    /// <summary>Requests a restart. Replaces the device's open request, if it has one.</summary>
    /// <param name="mac">The device.</param>
    /// <param name="mode">Now, at <paramref name="atUtc"/>, or at the next quiet hour.</param>
    /// <param name="atUtc">The chosen time for <see cref="DeviceRestartMode.At"/>.</param>
    /// <param name="powerCycle">Power-cycle its PoE port instead of restarting it.</param>
    /// <param name="ct">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.DeviceRestartRequested, TargetType = "device")]
    Task<DeviceRestartResult> RequestAsync(string mac, DeviceRestartMode mode, DateTime? atUtc, bool powerCycle, CancellationToken ct = default);

    /// <summary>Cancels the device's open request.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.DeviceRestartCancelled, TargetType = "device")]
    Task<DeviceRestartResult> CancelAsync(string mac, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class DeviceRestartService : IDeviceRestartService
{
    private static readonly TimeSpan BadgeFailureWindow = TimeSpan.FromHours(24);

    private readonly DeviceRestartExecutor _executor;
    private readonly DeviceRestartSiteReader _reader;
    private readonly ICallerContext _caller;
    private readonly IAuditContext _audit;
    private readonly ILogger<DeviceRestartService> _logger;

    public DeviceRestartService(
        DeviceRestartExecutor executor,
        SiteContextService site,
        ICallerContext caller,
        IAuditContext audit,
        ILogger<DeviceRestartService> logger)
    {
        _executor = executor;
        _reader = executor.ReaderFor(site.Slug);
        _caller = caller;
        _audit = audit;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DeviceRestartBadge>> GetBadgesAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - BadgeFailureWindow;
        await using var db = await _reader.OpenDbAsync(ct);
        var rows = await db.DeviceRestartRequests.AsNoTracking()
            .Where(r => r.Status == DeviceRestartStatus.Pending || r.Status == DeviceRestartStatus.Waiting
                || r.Status == DeviceRestartStatus.Running
                || (r.Status == DeviceRestartStatus.Failed && r.CompletedAt >= since))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // An open request outranks an older failure for the same device.
        var badges = new Dictionary<string, DeviceRestartBadge>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows.OrderBy(r => r.IsOpen).ThenBy(r => r.Id))
            badges[MacNormalizer.Normalize(r.DeviceMac)] = new(MacNormalizer.Normalize(r.DeviceMac), r.Status, r.Mode, r.DueAtUtc, r.StatusReason);
        return badges;
    }

    /// <inheritdoc />
    public async Task<DeviceRestartSheetState> GetSheetAsync(string mac, CancellationToken ct = default)
    {
        var key = MacNormalizer.Normalize(mac);
        var read = await _reader.ReadTopologyAsync(ct);
        if (read is not { } snapshot || snapshot.Topology.Find(key) is not { } node)
            return new DeviceRestartSheetState { Mac = key, Error = "The UniFi Console is not answering, so the device can't be read right now." };

        var topology = snapshot.Topology;
        var device = snapshot.Devices.First(d => MacNormalizer.Normalize(d.Mac) == key);
        var below = topology.Subtree(key);
        var site = await _reader.ReadSiteStateAsync(ct);

        await using var db = await _reader.OpenDbAsync(ct);
        var open = await db.DeviceRestartRequests.AsNoTracking()
            .Where(r => r.Status == DeviceRestartStatus.Pending || r.Status == DeviceRestartStatus.Waiting || r.Status == DeviceRestartStatus.Running)
            .ToListAsync(ct);
        var since = DateTime.UtcNow.AddDays(-30);
        var restarts = await db.DeviceRestartRequests.AsNoTracking()
            .CountAsync(r => r.DeviceMac == key && r.Status == DeviceRestartStatus.Done && r.CompletedAt >= since, ct);

        var mine = open.FirstOrDefault(r => r.DeviceMac == key);
        DeviceRestartPending? pending = null;
        if (mine != null)
        {
            // Another open request close in time above or below it runs in the same pass, root first.
            var with = open.FirstOrDefault(r => r != mine
                && (topology.IsAbove(r.DeviceMac, key) || topology.IsAbove(key, r.DeviceMac))
                && (r.DueAtUtc - mine.DueAtUtc).Duration() <= RestartRules.CoalescingWindow);
            pending = new DeviceRestartPending(mine.Mode, mine.DueAtUtc, mine.Status, mine.StatusReason,
                mine.RequestedByName, mine.RequestedMethod, with?.DeviceName);
        }

        return new DeviceRestartSheetState
        {
            Mac = key,
            Name = node.Name,
            Type = node.Type,
            IsOnline = node.IsOnline,
            Lockout = RestartRules.Lockout(topology, key, site, DateTime.UtcNow),
            HasSsh = await _reader.HasSshAsync(device, ct),
            DevicesBelow = below.Count,
            ClientsAffected = await ClientsAffectedAsync(node, below, ct),
            PoePort = _reader.PoweringPort(topology, key),
            Pending = pending,
            RestartsLast30Days = restarts,
            TimeZoneId = await _reader.ConsoleTimeZoneAsync(ct),
        };
    }

    /// <summary>Clients attached to the device or anything below it; every client for a gateway.</summary>
    private async Task<int?> ClientsAffectedAsync(RestartNode node, IReadOnlyList<RestartNode> below, CancellationToken ct)
    {
        try
        {
            if (_reader.Connection.Client is not { } client) return null;
            var clients = await client.GetClientsAsync(ct);
            if (node.Type == DeviceType.Gateway) return clients.Count;

            var macs = below.Select(n => n.Mac).Append(node.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return clients.Count(c =>
                (!string.IsNullOrEmpty(c.ApMac) && macs.Contains(MacNormalizer.Normalize(c.ApMac)))
                || (!string.IsNullOrEmpty(c.SwMac) && macs.Contains(MacNormalizer.Normalize(c.SwMac))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart: could not count the clients on {Mac}", node.Mac);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<DeviceRestartQuietHour?> ProposeQuietAsync(string mac, CancellationToken ct = default)
    {
        var read = await _reader.ReadTopologyAsync(ct);
        if (read is not { } snapshot || snapshot.Topology.Find(mac) == null) return null;
        var proposal = await _reader.ProposeQuietAsync(snapshot.Topology, snapshot.Devices, mac, ct);
        return new DeviceRestartQuietHour(proposal.StartUtc, proposal.Basis, proposal.TimeZoneId);
    }

    /// <inheritdoc />
    public async Task<DeviceRestartResult> RequestAsync(string mac, DeviceRestartMode mode, DateTime? atUtc, bool powerCycle, CancellationToken ct = default)
    {
        var key = MacNormalizer.Normalize(mac);
        var now = DateTime.UtcNow;
        var read = await _reader.ReadTopologyAsync(ct);
        if (read is not { } snapshot || snapshot.Topology.Find(key) is not { } node)
            return Refuse("The UniFi Console is not answering, so the restart was not scheduled. Try again in a minute.");

        var topology = snapshot.Topology;
        _audit.SetTarget(key, node.Name);
        var poe = _reader.PoweringPort(topology, key);

        // An offline device answers neither SSH nor the console: its PoE port is the only way in.
        if (mode == DeviceRestartMode.Now && !node.IsOnline)
        {
            if (poe == null) return Refuse($"Not restarted: {node.Name} is offline, and not powered from a switch port.");
            powerCycle = true;
        }
        if (powerCycle && poe == null)
            return Refuse($"{node.Name} is not powered from a switch port, so it can't be power-cycled.");

        if (mode == DeviceRestartMode.Now && RestartRules.Lockout(topology, key, await _reader.ReadSiteStateAsync(ct), now) is { } lockout)
            return Refuse($"Not restarted: {lockout} Schedule it instead, and it runs after.");

        DateTime due;
        string? basis = null;
        switch (mode)
        {
            case DeviceRestartMode.At:
                if (atUtc is not { } at) return Refuse("Choose a time for the restart.");
                if (at < now.AddMinutes(-1)) return Refuse("That time has passed. Choose a later one.");
                due = DateTime.SpecifyKind(at, DateTimeKind.Utc);
                break;
            case DeviceRestartMode.Quiet:
                var proposal = await _reader.ProposeQuietAsync(topology, snapshot.Devices, key, ct);
                due = proposal.StartUtc;
                basis = proposal.Basis;
                break;
            default:
                due = now;
                break;
        }

        await using var db = await _reader.OpenDbAsync(ct);
        var existing = await db.DeviceRestartRequests
            .Where(r => r.DeviceMac == key && (r.Status == DeviceRestartStatus.Pending || r.Status == DeviceRestartStatus.Waiting || r.Status == DeviceRestartStatus.Running))
            .ToListAsync(ct);
        if (existing.Any(r => r.LastCommandAt != null))
            return Refuse($"{node.Name} is restarting now. Wait for it to come back first.");
        foreach (var r in existing)
        {
            r.Status = DeviceRestartStatus.Cancelled;
            r.StatusReason = "Replaced by a newer request.";
            r.CompletedAt = now;
        }

        var caller = _caller.Current;
        db.DeviceRestartRequests.Add(new DeviceRestartRequest
        {
            DeviceMac = key,
            DeviceName = node.Name,
            DeviceType = node.Type,
            Mode = mode,
            DueAtUtc = due,
            QuietBasis = basis,
            RequestedMethod = powerCycle ? DeviceRestartMethod.PoeCycle : DeviceRestartMethod.Auto,
            Status = DeviceRestartStatus.Pending,
            RequestedByUserId = caller?.UserId,
            RequestedByName = caller?.ActorName,
            CreatedAt = now,
        });
        if (!await SaveAsync(db, ct))
            return Refuse("The restart changed while this was being saved. Open it again and retry.");

        _audit.SetDetails(new
        {
            mode = mode.ToString(),
            dueAt = due,
            quietBasis = basis,
            method = powerCycle ? "PoeCycle" : "Auto",
            poeSwitch = powerCycle ? poe?.SwitchMac : null,
            poePort = powerCycle ? poe?.PortIdx : null,
            replaced = existing.Count,
        });
        if (mode == DeviceRestartMode.Now) _executor.Nudge();
        return new((string?)null);
    }

    /// <inheritdoc />
    public async Task<DeviceRestartResult> CancelAsync(string mac, CancellationToken ct = default)
    {
        var key = MacNormalizer.Normalize(mac);
        await using var db = await _reader.OpenDbAsync(ct);
        var open = await db.DeviceRestartRequests
            .Where(r => r.DeviceMac == key && (r.Status == DeviceRestartStatus.Pending || r.Status == DeviceRestartStatus.Waiting || r.Status == DeviceRestartStatus.Running))
            .ToListAsync(ct);
        if (open.Count == 0)
        {
            return Refuse("There is no restart to cancel.");
        }
        if (open.Any(r => r.LastCommandAt != null))
            return Refuse("The restart command has already gone out, so it can't be cancelled.");

        var now = DateTime.UtcNow;
        foreach (var r in open)
        {
            r.Status = DeviceRestartStatus.Cancelled;
            r.StatusReason = "Cancelled.";
            r.CompletedAt = now;
        }
        if (!await SaveAsync(db, ct))
            return Refuse("The restart started while this was being saved, so it was not cancelled.");
        _audit.SetTarget(key, open[0].DeviceName);
        _audit.SetDetails(new { dueAt = open[0].DueAtUtc, mode = open[0].Mode.ToString() });
        return new((string?)null);
    }

    /// <summary>A refusal changed nothing, so it writes no audit entry.</summary>
    private DeviceRestartResult Refuse(string message)
    {
        _audit.SuppressNoChange();
        return new(message);
    }

    /// <summary>Saves, or false when the executor changed the same request first (Status is a concurrency token).</summary>
    private static async Task<bool> SaveAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}