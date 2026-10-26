using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>What a reachability check found.</summary>
public enum DeviceReach
{
    /// <summary>The device answered: it is passing traffic.</summary>
    Answered,

    /// <summary>The ping ran and nothing answered.</summary>
    Silent,

    /// <summary>No ping ran (no address, no agent online, tunnel failure), so nothing is known.</summary>
    CouldNotRun,
}

/// <summary>
/// Checks a device on the data plane, independent of what UniFi Network reports for it.
/// </summary>
public interface IDeviceReachabilityProbe
{
    /// <summary>Pings the device from the vantage that owns the site's path measurement.</summary>
    Task<DeviceReach> ProbeAsync(string siteSlug, string address, CancellationToken ct = default);
}

/// <summary>
/// Pings from this server, or from the site's On-Site Agent when the agent owns path measurement,
/// the same rule the latency probes follow: a ping from anywhere else describes a different network.
/// </summary>
public sealed class DeviceReachabilityProbe : IDeviceReachabilityProbe
{
    private const int PingCount = 3;
    private static readonly TimeSpan PerPingTimeout = TimeSpan.FromSeconds(1);

    private readonly LocalProbeExecutor _local;
    private readonly AgentProbeService _agentProbe;
    private readonly SiteAgentCoverage _coverage;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DeviceReachabilityProbe> _logger;

    public DeviceReachabilityProbe(LocalProbeExecutor local, AgentProbeService agentProbe, SiteAgentCoverage coverage,
        ILoggerFactory loggerFactory, ILogger<DeviceReachabilityProbe> logger)
    {
        _local = local;
        _agentProbe = agentProbe;
        _coverage = coverage;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DeviceReach> ProbeAsync(string siteSlug, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return DeviceReach.CouldNotRun;
        try
        {
            IProbeExecutor executor = _coverage.AgentOwnsPathMeasurement(siteSlug)
                ? new AgentProbeExecutor(_agentProbe, siteSlug, _loggerFactory.CreateLogger<AgentProbeExecutor>())
                : _local;
            return Classify(await executor.PingAsync(new ProbeTarget(address, ProbeMode.Icmp), PingCount, PerPingTimeout, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reachability probe of {Address} failed to run for site {Site}", address, siteSlug);
            return DeviceReach.CouldNotRun;
        }
    }

    /// <summary>
    /// Any reply is an answer. Sent = 0 is the agent path's "never ran" (no agent online, tunnel
    /// down), which says nothing about the device, so it must not read as a silent one.
    /// </summary>
    internal static DeviceReach Classify(PingProbeResult result) =>
        result.Received > 0 ? DeviceReach.Answered
        : result.Sent == 0 ? DeviceReach.CouldNotRun
        : DeviceReach.Silent;
}
