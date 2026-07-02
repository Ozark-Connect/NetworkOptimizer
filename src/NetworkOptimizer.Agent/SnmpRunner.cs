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
                    var interfaces = await poller.GetInterfaceMetricsAsync(ip, device.Name);
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
                    var metrics = await poller.GetDeviceMetricsAsync(ip, device.Name);
                    if (!metrics.IsReachable) return;

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

                    if (sample.HasCpuPercent || sample.HasMemoryUsedPercent || sample.HasTemperatureC || sample.HasUptimeSeconds)
                    {
                        var batch = new SnmpResultBatch();
                        batch.Health.Add(sample);
                        await _tunnel.SendAsync(new AgentMessage { SnmpResults = batch }, ct);
                    }
                }, ct);
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, config?.MediumIntervalSeconds ?? 30)), ct);
        }
    }

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
