using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The site's SNMP poller. The server pushes the site's SNMP credentials and
/// device list over the tunnel; this runner polls interface counters (fast
/// cadence) and device health (medium cadence) locally and streams the raw
/// samples back. Rate computation and storage happen server-side, mirroring
/// the central collection agent's split. Lives and dies with its tunnel
/// connection, like the probe runner.
/// </summary>
public sealed class SnmpRunner
{
    private readonly TunnelClient _tunnel;
    private volatile SnmpConfig? _config;
    private SnmpPoller? _poller;
    private string _pollerKey = "";

    // Failure counting + temporary exclusion, same tracker (and thresholds) the
    // server's collection loops use, so a rebooting device isn't hammered from
    // the agent either. Keyed by device MAC.
    private readonly SnmpFailureTracker _failures = new();

    public SnmpRunner(TunnelClient tunnel)
    {
        _tunnel = tunnel;
    }

    public void UpdateConfig(SnmpConfig config)
    {
        _config = config;
        Console.WriteLine(config.Enabled
            ? $"Received SNMP config: {config.Devices.Count} device(s), every {config.FastIntervalSeconds}s"
            : "SNMP monitoring disabled by server config");
    }

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(FastLoopAsync(ct), MediumLoopAsync(ct));

    /// <summary>
    /// Handles the server's on-demand "Test OID" request: GET the single OID once against a
    /// site-local device and return the raw value (correlated by request id).
    /// </summary>
    public async Task HandleOidQueryAsync(SnmpOidQuery query, CancellationToken ct)
    {
        Console.WriteLine($"OID test request {query.RequestId}: ip={query.DeviceIp} oid={query.Oid}");
        var result = new SnmpOidResult { RequestId = query.RequestId };
        try
        {
            var poller = GetPoller(_config);
            if (poller == null)
                result.Error = "SNMP is not configured on this site.";
            else if (!IPAddress.TryParse(query.DeviceIp, out var ip))
                result.Error = $"Invalid IP address: {query.DeviceIp}";
            else
            {
                var value = await poller.GetAsync<string>(ip, query.Oid);
                if (value == null)
                    result.Error = "No response (OID may not exist on this device).";
                else
                {
                    result.Success = true;
                    result.Value = value;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = $"SNMP error: {ex.Message}";
        }

        Console.WriteLine($"OID test request {query.RequestId}: success={result.Success} value={result.Value} error={result.Error}");
        try { await _tunnel.SendAsync(new AgentMessage { SnmpOidResult = result }, ct); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to send OID test result {query.RequestId}: {ex.Message}"); }
    }

    /// <summary>Interface counters on the fast cadence.</summary>
    private async Task FastLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config;
            var poller = GetPoller(config);
            if (poller != null && config != null)
            {
                await ForEachDeviceAsync(config, async device =>
                {
                    if (!IPAddress.TryParse(device.Ip, out var ip)) return;
                    if (IsExcluded(device)) return;
                    var interfaces = await PollCountingFailures(device,
                        () => poller.GetInterfaceMetricsAsync(ip, device.Name));
                    if (interfaces.Count == 0)
                    {
                        NoteFailure(device);
                        return;
                    }
                    _failures.NoteSuccess(device.Mac);
                    var batch = new SnmpResultBatch();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var iface in interfaces.Where(i => i.ShouldMonitor()))
                    {
                        var ifName = iface.MonitoredName;
                        if (string.IsNullOrEmpty(ifName)) continue;
                        batch.Interfaces.Add(new SnmpInterfaceSample
                        {
                            DeviceMac = device.Mac,
                            IfName = ifName,
                            IfDescr = iface.Description ?? "",
                            PortId = iface.PortId ?? "",
                            InOctets = iface.InOctets,
                            OutOctets = iface.OutOctets,
                            SpeedBps = iface.ResolvedSpeedBps,
                            HcCounters = iface.UsesHcCounters,
                            OperStatus = iface.OperStatus,
                            ErrorsIn = iface.InErrors,
                            ErrorsOut = iface.OutErrors,
                            DiscardsIn = iface.InDiscards,
                            DiscardsOut = iface.OutDiscards,
                            UcastPktsIn = iface.InUcastPkts,
                            UcastPktsOut = iface.OutUcastPkts,
                            McastPktsIn = iface.InMulticastPkts,
                            McastPktsOut = iface.OutMulticastPkts,
                            BcastPktsIn = iface.InBroadcastPkts,
                            BcastPktsOut = iface.OutBroadcastPkts,
                            TimestampUnixMs = now,
                        });
                    }
                    if (batch.Interfaces.Count > 0)
                        await _tunnel.SendAsync(new AgentMessage { SnmpResults = batch }, ct);
                }, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, config?.FastIntervalSeconds ?? 5)), ct);
        }
    }

    /// <summary>Device health (CPU, memory, temperature, uptime) on the medium cadence.</summary>
    private async Task MediumLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var config = _config;
            var poller = GetPoller(config);
            if (poller != null && config != null)
            {
                await ForEachDeviceAsync(config, async device =>
                {
                    if (!IPAddress.TryParse(device.Ip, out var ip)) return;
                    if (IsExcluded(device)) return;
                    var metrics = await PollCountingFailures(device,
                        () => poller.GetDeviceMetricsAsync(ip, device.Name));
                    if (!metrics.IsReachable)
                    {
                        NoteFailure(device);
                        return;
                    }
                    _failures.NoteSuccess(device.Mac);

                    var sample = new SnmpDeviceHealthSample
                    {
                        DeviceMac = device.Mac,
                        DeviceType = device.DeviceType,
                        TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    if (metrics.CpuUsage > 0) sample.CpuPercent = metrics.CpuUsage;
                    if (metrics.MemoryUsage > 0) sample.MemoryUsedPercent = metrics.MemoryUsage;
                    if (metrics.TotalMemory > 0) sample.MemoryTotalKb = metrics.TotalMemory / 1024;
                    if (metrics.UsedMemory > 0) sample.MemoryUsedKb = metrics.UsedMemory / 1024;
                    if (metrics.Temperature > 0) sample.TemperatureC = metrics.Temperature!.Value;
                    if (metrics.Uptime > 0) sample.UptimeSeconds = metrics.Uptime / 100;

                    var batch = new SnmpResultBatch();
                    if (sample.HasCpuPercent || sample.HasMemoryUsedPercent || sample.HasTemperatureC || sample.HasUptimeSeconds)
                        batch.Health.Add(sample);

                    await PollCustomOidsAsync(poller, ip, device, config, batch);

                    if (batch.Health.Count > 0 || batch.CustomOids.Count > 0)
                        await _tunnel.SendAsync(new AgentMessage { SnmpResults = batch }, ct);
                }, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, config?.MediumIntervalSeconds ?? 30)), ct);
        }
    }

    /// <summary>
    /// Polls the device's configured custom OIDs and appends raw results to the batch. Scalar
    /// (device-level) OIDs are SNMP-GET; indexed (interface-level) OIDs are walked and returned
    /// keyed by ifIndex. The server parses the values and resolves interface names, mirroring the
    /// directly-monitored medium tier's PollCustomOidsAsync.
    /// </summary>
    private static async Task PollCustomOidsAsync(
        SnmpPoller poller, IPAddress ip, SnmpDeviceSpec device, SnmpConfig config, SnmpResultBatch batch)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var oid in config.CustomOids)
        {
            if (!string.Equals(oid.DeviceMac, device.Mac, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (oid.Scope == 0) // DeviceLevel (scalar)
                {
                    var value = await poller.GetAsync<string>(ip, oid.Oid);
                    if (value != null)
                        batch.CustomOids.Add(new SnmpCustomOidResult
                        {
                            DeviceMac = device.Mac,
                            FieldName = oid.FieldName,
                            ValueType = oid.ValueType,
                            Scope = oid.Scope,
                            Value = value,
                            TimestampUnixMs = now,
                        });
                }
                else // InterfaceLevel (walked, keyed by ifIndex)
                {
                    var walked = await poller.BulkWalkAsync(ip, oid.Oid);
                    var result = new SnmpCustomOidResult
                    {
                        DeviceMac = device.Mac,
                        FieldName = oid.FieldName,
                        ValueType = oid.ValueType,
                        Scope = oid.Scope,
                        TimestampUnixMs = now,
                    };
                    var prefix = oid.Oid + ".";
                    foreach (var v in walked)
                    {
                        var walkedOid = v.Id.ToString();
                        if (!walkedOid.StartsWith(prefix)) continue;
                        result.InterfaceValues[walkedOid.Substring(prefix.Length)] = v.Data.ToString() ?? "";
                    }
                    if (result.InterfaceValues.Count > 0)
                        batch.CustomOids.Add(result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Custom OID poll failed for {device.Mac} OID={oid.Oid}: {ex.Message}");
            }
        }
    }

    /// <summary>Runs a poll call, counting any thrown failure before rethrowing.</summary>
    private async Task<T> PollCountingFailures<T>(SnmpDeviceSpec device, Func<Task<T>> poll)
    {
        try
        {
            return await poll();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NoteFailure(device);
            throw;
        }
    }

    private bool IsExcluded(SnmpDeviceSpec device)
    {
        var excluded = _failures.IsExcluded(device.Mac, out var justExpired);
        if (justExpired)
            Console.WriteLine($"SNMP exclusion expired for {Label(device)}, resuming polling");
        return excluded;
    }

    private void NoteFailure(SnmpDeviceSpec device)
    {
        if (_failures.NoteFailure(device.Mac))
            Console.WriteLine($"Excluding {Label(device)} from SNMP polling for {SnmpFailureTracker.DefaultExclusionDuration.TotalMinutes:0} min after repeated failures");
    }

    private static string Label(SnmpDeviceSpec device) =>
        string.IsNullOrEmpty(device.Name) ? device.Mac : device.Name;

    private static async Task ForEachDeviceAsync(SnmpConfig config, Func<SnmpDeviceSpec, Task> body, CancellationToken ct)
    {
        using var concurrency = new SemaphoreSlim(4);
        var tasks = config.Devices.Select(async device =>
        {
            await concurrency.WaitAsync(ct);
            try
            {
                await body(device);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"SNMP poll of {device.Ip} failed: {ex.Message}");
            }
            finally
            {
                concurrency.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    private SnmpPoller? GetPoller(SnmpConfig? config)
    {
        if (config == null || !config.Enabled || config.Devices.Count == 0)
            return null;

        var key = $"{config.Version}|{config.Community}|{config.Username}|{config.AuthPassword}";
        if (_poller != null && key == _pollerKey)
            return _poller;

        var cfg = new SnmpConfiguration();
        if (string.Equals(config.Version, "v2c", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(config.Community)) return null;
            cfg.Version = SnmpVersion.V2c;
            cfg.Community = config.Community;
        }
        else
        {
            if (string.IsNullOrEmpty(config.Username)) return null;
            cfg.Version = SnmpVersion.V3;
            cfg.Username = config.Username;
            cfg.AuthenticationPassword = config.AuthPassword;
        }

        _poller = new SnmpPoller(cfg, NullLogger<SnmpPoller>.Instance);
        _pollerKey = key;
        return _poller;
    }
}
