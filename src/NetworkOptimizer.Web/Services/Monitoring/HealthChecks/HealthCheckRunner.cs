using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// Live state of one check, for the Setup table and the alert messages.
/// </summary>
public sealed class HealthCheckLiveStatus
{
    public DateTime? LastRunAt { get; set; }
    public double? LastValue { get; set; }
    public bool? LastFailing { get; set; }
    /// <summary>What the last run came to, in a few words: "ok", "failing", "not applicable", or the error.</summary>
    public string? Outcome { get; set; }
    public bool Tripped { get; set; }
    public int ConsecutiveFailing { get; set; }
    public DateTime? LastRemedyAt { get; set; }
    public int RemediesToday { get; set; }
}

/// <summary>
/// Runs one site's health checks on their own cadence: executes each due check over SSH, writes
/// the sample to Influx, trips and clears alerts, and runs the remedy under its guards. Own loop
/// rather than a collection tier, so an SSH stall never holds SNMP polling.
/// </summary>
public sealed class HealthCheckRunner
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefinitionsCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DevicesCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>A tripped check re-notifies this often while it stays failing.</summary>
    private static readonly TimeSpan RenotifyInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A remedy that takes the device or its console away is registered as expected for this long,
    /// refreshed once partway so a slow reboot stays covered.
    /// </summary>
    private static readonly TimeSpan ExpectedOutageRefresh = TimeSpan.FromMinutes(4);

    private sealed class CheckState
    {
        public HealthCheckLiveStatus Status { get; } = new();
        public DateTime? LastFailedAlertAt { get; set; }
        public DateTime RemedyDayUtc { get; set; }
        /// <summary>The first sample after a remedy is thrown away: it describes the run that was just ended.</summary>
        public bool DiscardNext { get; set; }
        public int InFlight;
    }

    private readonly string _siteSlug;
    private readonly bool _isDefault;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IUniFiSshService _deviceSsh;
    private readonly UniFiConnectionService _connection;
    private readonly MonitoringInfluxClient _influx;
    private readonly IAlertEventBus _eventBus;
    private readonly RolloutSuppressionRegistry _suppression;
    private readonly IAuditLogger _audit;
    private readonly ILogger<HealthCheckRunner> _logger;
    private readonly string _siteSuffix;

    private readonly ConcurrentDictionary<int, CheckState> _states = new();
    private readonly SemaphoreSlim _sshGate = new(2);

    private List<HealthCheckDefinition> _definitions = new();
    private DateTime _definitionsLoadedAt = DateTime.MinValue;
    private List<DiscoveredDevice> _devices = new();
    private DateTime _devicesLoadedAt = DateTime.MinValue;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public HealthCheckRunner(
        string siteSlug,
        SiteDbContextFactory siteDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        IGatewaySshService gatewaySsh,
        IUniFiSshService deviceSsh,
        UniFiConnectionService connection,
        MonitoringInfluxClient influx,
        IAlertEventBus eventBus,
        RolloutSuppressionRegistry suppression,
        IAuditLogger audit,
        ILogger<HealthCheckRunner> logger)
    {
        _siteSlug = siteSlug;
        _isDefault = siteSlug == SiteManagementService.DefaultSiteSlug;
        _siteDbFactory = siteDbFactory;
        _mainDbFactory = mainDbFactory;
        _gatewaySsh = gatewaySsh;
        _deviceSsh = deviceSsh;
        _connection = connection;
        _influx = influx;
        _eventBus = eventBus;
        _suppression = suppression;
        _audit = audit;
        _logger = logger;
        _siteSuffix = _isDefault ? "" : $" (site {siteSlug})";
    }

    /// <summary>Starts the loop. Safe to call twice.</summary>
    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Stops the loop and waits briefly for it.</summary>
    public async Task StopAsync()
    {
        if (_cts == null || _loop == null) return;
        _cts.Cancel();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) { /* cancelled or timed out; the loop is done either way */ }
        _loop = null;
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Forgets the cached definitions so an edit takes effect on the next tick.</summary>
    public void InvalidateDefinitions() => _definitionsLoadedAt = DateTime.MinValue;

    /// <summary>The live status of a check, or null when it has not run since startup.</summary>
    public HealthCheckLiveStatus? GetStatus(int checkId) =>
        _states.TryGetValue(checkId, out var state) ? state.Status : null;

    private async Task LoopAsync(CancellationToken ct)
    {
        // Let the consoles connect and SSH settings load before the first pass.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health check tick failed for site {Site}", _siteSlug);
            }

            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var definitions = await LoadDefinitionsAsync(ct);
        if (definitions.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var check in definitions)
        {
            if (!check.Enabled) continue;
            var state = _states.GetOrAdd(check.Id, _ => new CheckState());
            var interval = TimeSpan.FromSeconds(Math.Max(30, check.IntervalSeconds));
            if (state.Status.LastRunAt is { } last && now - last < interval) continue;
            if (Interlocked.CompareExchange(ref state.InFlight, 1, 0) != 0) continue;

            // Stamped before the run so a slow SSH round trip cannot queue a second run behind it.
            state.Status.LastRunAt = now;
            _ = RunOneAsync(check, state, ct);
        }
    }

    private async Task RunOneAsync(HealthCheckDefinition check, CheckState state, CancellationToken ct)
    {
        try
        {
            var device = await FindDeviceAsync(check.DeviceMac, ct);
            if (device == null)
            {
                state.Status.Outcome = "device not found in UniFi";
                return;
            }

            HealthCheckRunResult result;
            await _sshGate.WaitAsync(ct);
            try
            {
                result = await HealthCheckExecutor.RunAsync(check, EffectiveType(device), device.DisplayIpAddress, _gatewaySsh, _deviceSsh, ct);
            }
            finally
            {
                _sshGate.Release();
            }

            await ApplyResultAsync(check, state, device, result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            state.Status.Outcome = ex.Message;
            _logger.LogDebug(ex, "Health check {Check} failed to run on {Mac}", check.Name, check.DeviceMac);
        }
        finally
        {
            Interlocked.Exchange(ref state.InFlight, 0);
        }
    }

    private async Task ApplyResultAsync(
        HealthCheckDefinition check, CheckState state, DiscoveredDevice device, HealthCheckRunResult result, CancellationToken ct)
    {
        var status = state.Status;

        if (!result.Ran)
        {
            status.Outcome = result.Error ?? "did not run";
            _logger.LogDebug("Health check {Check} on {Device}: {Error}", check.Name, device.Name, result.Error);
            return;
        }

        if (result.NotApplicable)
        {
            status.Outcome = "not applicable on this device";
            status.LastValue = null;
            status.LastFailing = null;
            return;
        }

        if (result.Value is null)
        {
            status.Outcome = "no value found in the output";
            status.LastValue = null;
            status.LastFailing = null;
            return;
        }

        if (state.DiscardNext)
        {
            // This sample still describes the process the remedy just ended.
            state.DiscardNext = false;
            status.Outcome = "settling after the last action";
            return;
        }

        var value = result.Value.Value;
        var deviceType = MonitoringCollectionAgent.DescribeDeviceType(device.Type);
        _ = _influx.WriteCustomFieldsAsync("device_health", check.DeviceMac, new Dictionary<string, object>
        {
            [HealthCheckEvaluation.ValueField(check.FieldName)] = value,
            [HealthCheckEvaluation.FailingField(check.FieldName)] = result.Failing ? 1L : 0L,
        }, deviceType, null, null, result.At);

        status.LastValue = value;
        status.LastFailing = result.Failing;

        if (!result.Failing)
        {
            status.ConsecutiveFailing = 0;
            status.Outcome = "ok";
            if (status.Tripped)
            {
                status.Tripped = false;
                state.LastFailedAlertAt = null;
                await PublishRecoveredAsync(check, device, value, ct);
            }
            return;
        }

        status.ConsecutiveFailing++;
        var needed = Math.Max(1, check.ConsecutiveSamples);
        if (status.ConsecutiveFailing < needed)
        {
            status.Outcome = $"failing ({status.ConsecutiveFailing} of {needed})";
            return;
        }

        status.Outcome = "failing";
        var now = DateTime.UtcNow;
        var justTripped = !status.Tripped;
        status.Tripped = true;

        if (justTripped || state.LastFailedAlertAt is null || now - state.LastFailedAlertAt >= RenotifyInterval)
        {
            state.LastFailedAlertAt = now;
            await PublishFailedAsync(check, device, value, ct);
        }

        if (check.Remedy != HealthCheckRemedy.None)
            await MaybeRunRemedyAsync(check, state, device, value, ct);
    }

    private async Task MaybeRunRemedyAsync(
        HealthCheckDefinition check, CheckState state, DiscoveredDevice device, double value, CancellationToken ct)
    {
        var status = state.Status;
        var now = DateTime.UtcNow;

        var type = EffectiveType(device);
        if (!HealthCheckRemedies.SupportedOn(check.Remedy, type))
        {
            _logger.LogDebug("Health check {Check}: {Remedy} is not supported on a {Type}", check.Name, check.Remedy, type);
            return;
        }

        var command = HealthCheckRemedies.BuildCommand(check.Remedy, check.RemedyArg);
        if (command == null)
        {
            _logger.LogWarning("Health check {Check}: remedy argument '{Arg}' is not valid; not running it", check.Name, check.RemedyArg);
            return;
        }

        if (_suppression.IsSiteActiveRollout(_siteSlug, now) || _suppression.IsOsCycling(_siteSlug, now))
        {
            _logger.LogInformation("Health check {Check} on {Device}: holding the remedy, a firmware rollout is in progress", check.Name, device.Name);
            return;
        }

        if (status.LastRemedyAt is { } last && now - last < TimeSpan.FromSeconds(Math.Max(60, check.RemedyCooldownSeconds)))
            return;

        if (state.RemedyDayUtc != now.Date)
        {
            state.RemedyDayUtc = now.Date;
            status.RemediesToday = 0;
        }
        if (check.RemedyMaxPerDay > 0 && status.RemediesToday >= check.RemedyMaxPerDay)
        {
            _logger.LogInformation("Health check {Check} on {Device}: daily remedy cap of {Cap} reached", check.Name, device.Name, check.RemedyMaxPerDay);
            return;
        }

        status.LastRemedyAt = now;
        status.RemediesToday++;
        state.DiscardNext = true;

        RegisterExpectedOutage(check, device, now);

        _logger.LogInformation("Health check {Check} on {Device}: value {Value}, running remedy: {Command}",
            check.Name, device.Name, value, command);

        var (ok, output) = await HealthCheckExecutor.RunRemedyAsync(command, type, device.DisplayIpAddress, _gatewaySsh, _deviceSsh, ct);

        // A reboot drops the session, which the SSH layer reports as a failure that is not one.
        if (check.Remedy == HealthCheckRemedy.RebootDevice) ok = true;

        var did = HealthCheckRemedies.Describe(check.Remedy, check.RemedyArg);
        var detail = ok
            ? $"{check.Name} was {HealthCheckEvaluation.DescribeCondition(check.Operator, check.Threshold)} (read {value:0.##}), so Network Optimizer {did}."
            : $"{check.Name} was {HealthCheckEvaluation.DescribeCondition(check.Operator, check.Threshold)} (read {value:0.##}). Network Optimizer tried to run \"{command}\" and it failed: {Trim(output)}";

        _ = _influx.WriteHealthCheckEventAsync(
            check.DeviceMac, MonitoringCollectionAgent.DescribeDeviceType(device.Type), check.Name, check.FieldName,
            check.Remedy.ToString(), ok ? "warning" : "critical", detail, value, now);

        var audit = AuditEventBuilder.From(
            null, AuditCategories.Action, AuditActions.HealthCheckRemedyRun,
            outcome: ok ? AuditOutcomes.Success : AuditOutcomes.Failure,
            targetType: "device", targetId: check.DeviceMac, targetName: device.Name,
            siteSlug: _isDefault ? null : _siteSlug,
            details: new { check = check.Name, remedy = check.Remedy.ToString(), argument = check.RemedyArg, value, command });
        audit.ActorName = "Network Optimizer";
        audit.ActorAuthMethod = "system";
        _audit.Log(audit);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = HealthCheckAlertTypes.Action,
            Source = "monitoring",
            Severity = ok ? AlertSeverity.Info : AlertSeverity.Error,
            Title = ok
                ? $"{check.Name}: {Capitalize(did)} on {device.Name}{_siteSuffix}"
                : $"{check.Name}: action failed on {device.Name}{_siteSuffix}",
            Message = detail,
            DeviceId = check.DeviceMac,
            DeviceName = device.Name,
            DeviceIp = device.DisplayIpAddress,
            MetricValue = value,
            ThresholdValue = check.Threshold,
            SourceUrl = MonitoringLinks.DeviceStats(check.DeviceMac, MonitoringLinks.NowMs()),
            Tags = ["monitoring", "health-check"],
            Context = Context(check, value, check.Remedy.ToString()),
        }, ct);
    }

    /// <summary>
    /// Tells the offline and reboot alerting that what is about to happen was asked for. A console
    /// restart takes every device dark for a minute or two; a gateway reboot takes the WAN too.
    /// </summary>
    private void RegisterExpectedOutage(HealthCheckDefinition check, DiscoveredDevice device, DateTime now)
    {
        void Refresh(DateTime at)
        {
            switch (check.Remedy)
            {
                case HealthCheckRemedy.RebootDevice when EffectiveType(device) == DeviceType.Gateway:
                    _suppression.RefreshOsCycle(_siteSlug, at);
                    _suppression.RefreshConsoleCycle(_siteSlug, at);
                    break;
                case HealthCheckRemedy.RebootDevice:
                    _suppression.Refresh(_siteSlug, check.DeviceMac, at);
                    break;
                case HealthCheckRemedy.RestartService when EffectiveType(device) == DeviceType.Gateway:
                    _suppression.RefreshConsoleCycle(_siteSlug, at);
                    break;
            }
        }

        if (check.Remedy is not (HealthCheckRemedy.RebootDevice or HealthCheckRemedy.RestartService)) return;

        Refresh(now);
        // Windows lapse after RolloutSuppressionRegistry.WindowFreshness; one refresh partway
        // covers a reboot that runs long. Fire-and-forget: nothing waits on it.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ExpectedOutageRefresh);
                Refresh(DateTime.UtcNow);
            }
            catch (Exception) { }
        });
    }

    private async Task PublishFailedAsync(HealthCheckDefinition check, DiscoveredDevice device, double value, CancellationToken ct)
    {
        if (!check.AlertEnabled) return;
        var severity = Enum.IsDefined(typeof(AlertSeverity), check.AlertSeverity) ? (AlertSeverity)check.AlertSeverity : AlertSeverity.Warning;
        var remedyNote = check.Remedy == HealthCheckRemedy.None
            ? ""
            : $" The check is set to {HealthCheckRemedies.Label(check.Remedy).ToLowerInvariant()}{(HealthCheckRemedies.NeedsArgument(check.Remedy) ? $" ({check.RemedyArg})" : "")} when this happens.";

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = HealthCheckAlertTypes.Failed,
            Source = "monitoring",
            Severity = severity,
            Title = $"{check.Name} on {device.Name}{_siteSuffix}",
            Message = $"{check.Name} read {value:0.##} on {device.Name}, {HealthCheckEvaluation.DescribeCondition(check.Operator, check.Threshold)} "
                + $"for {Math.Max(1, check.ConsecutiveSamples)} consecutive sample(s).{remedyNote}",
            DeviceId = check.DeviceMac,
            DeviceName = device.Name,
            DeviceIp = device.DisplayIpAddress,
            MetricValue = value,
            ThresholdValue = check.Threshold,
            SourceUrl = MonitoringLinks.DeviceStats(check.DeviceMac, MonitoringLinks.NowMs()),
            Tags = ["monitoring", "health-check"],
            Context = Context(check, value, null),
        }, ct);
    }

    private async Task PublishRecoveredAsync(HealthCheckDefinition check, DiscoveredDevice device, double value, CancellationToken ct)
    {
        if (!check.AlertEnabled) return;
        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = HealthCheckAlertTypes.Recovered,
            Source = "monitoring",
            Severity = AlertSeverity.Info,
            Title = $"{check.Name} recovered on {device.Name}{_siteSuffix}",
            Message = $"{check.Name} read {value:0.##} on {device.Name}, no longer {HealthCheckEvaluation.DescribeCondition(check.Operator, check.Threshold)}.",
            DeviceId = check.DeviceMac,
            DeviceName = device.Name,
            DeviceIp = device.DisplayIpAddress,
            MetricValue = value,
            ThresholdValue = check.Threshold,
            SourceUrl = MonitoringLinks.DeviceStats(check.DeviceMac, MonitoringLinks.NowMs()),
            Tags = ["monitoring", "health-check"],
            Context = Context(check, value, null),
        }, ct);
    }

    private static Dictionary<string, string> Context(HealthCheckDefinition check, double value, string? remedy) => new()
    {
        ["device_mac"] = check.DeviceMac,
        ["check_id"] = check.Id.ToString(),
        ["check"] = check.Name,
        ["field"] = check.FieldName,
        ["value"] = value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        ["threshold"] = check.Threshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
        ["operator"] = HealthCheckEvaluation.OperatorSymbol(check.Operator),
        ["remedy"] = remedy ?? "",
    };

    private async Task<List<HealthCheckDefinition>> LoadDefinitionsAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _definitionsLoadedAt < DefinitionsCacheTtl) return _definitions;
        try
        {
            await using var db = _isDefault
                ? await _mainDbFactory.CreateDbContextAsync(ct)
                : _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
            _definitions = await db.HealthCheckDefinitions.AsNoTracking().ToListAsync(ct);
            _definitionsLoadedAt = DateTime.UtcNow;

            // Drop state for checks that no longer exist.
            var live = _definitions.Select(d => d.Id).ToHashSet();
            foreach (var id in _states.Keys.Where(k => !live.Contains(k)).ToList())
                _states.TryRemove(id, out _);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load health checks for site {Site}", _siteSlug);
        }
        return _definitions;
    }

    private async Task<DiscoveredDevice?> FindDeviceAsync(string mac, CancellationToken ct)
    {
        if (DateTime.UtcNow - _devicesLoadedAt >= DevicesCacheTtl)
        {
            if (!_connection.IsConnected) return null;
            try
            {
                _devices = await _connection.GetDiscoveredDevicesAsync(ct) ?? new List<DiscoveredDevice>();
                _devicesLoadedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not list devices for health checks on site {Site}", _siteSlug);
                return null;
            }
        }

        var wanted = Normalize(mac);
        return _devices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Mac) && Normalize(d.Mac) == wanted);
    }

    /// <summary>
    /// Gateway hardware is a gateway for SSH credentials and remedies whatever role it is in: a
    /// UDR meshing as an access point still runs UniFi OS and still takes the console credentials.
    /// </summary>
    internal static DeviceType EffectiveType(DiscoveredDevice device) =>
        device.HardwareType == DeviceType.Gateway ? DeviceType.Gateway : device.Type;

    private static string Normalize(string mac) => mac.Replace(":", "").Replace("-", "").ToLowerInvariant();

    private static string Trim(string? text)
    {
        var flat = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "...";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
