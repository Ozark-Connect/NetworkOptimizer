using System.Reflection;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Guards the per-site registries' ActivatorUtilities.CreateInstance calls.
/// ActivatorUtilities binds constructors at RUNTIME: an explicit argument the target
/// constructor cannot consume compiles cleanly and passes every other test, then throws
/// "a suitable constructor could not be located" the first time the site bundle is
/// built (GatewayWanSpeedTestService was missing its IAlertEventBus parameter and the
/// WAN speed test page 500'd). Each entry below mirrors one CreateInstance call site's
/// explicit arguments, using the RUNTIME type of what the registry actually passes.
/// When a registry call gains or loses an explicit argument, update its entry here.
/// </summary>
public class RegistryConstructionTests
{
    public static TheoryData<Type, Type[]> Constructions => new()
    {
        // SiteConnectionRegistry
        { typeof(UniFiConnectionService), new[] { typeof(string) } },
        // GatewaySshRegistry
        { typeof(GatewaySshService), new[] { typeof(string) } },
        // UniFiSshRegistry
        { typeof(UniFiSshService), new[] { typeof(string) } },
        // ChannelMemoryRegistry / ChannelMemoryCollectionService / Program.cs
        { typeof(ChannelMemoryCollectionService), new[] { typeof(string) } },
        { typeof(ChannelMemoryRepository), new[] { typeof(string), typeof(bool) } },
        // ModemMonitorRegistry
        { typeof(QmicliModemProvider), new[] { typeof(UniFiSshService) } },
        { typeof(NetgearNighthawkHotspotProvider), Array.Empty<Type>() },
        { typeof(QuectelAtModemProvider), Array.Empty<Type>() },
        { typeof(CableModemMonitorService), new[] { typeof(string) } },
        { typeof(OntMonitorService), new[] { typeof(string) } },
        { typeof(CellularModemService), new[] { typeof(string), typeof(UniFiSshService), typeof(List<ICellularModemProvider>) } },
        // IspHealthRegistry
        { typeof(PhysicalLinkResolver), new[] { typeof(string) } },
        { typeof(IspHealthService), new[] { typeof(string), typeof(PhysicalLinkResolver) } },
        // MonitoringAlertRegistry (bus is the slug-stamping wrapper)
        { typeof(MonitoringAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        { typeof(DeviceHealthAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        { typeof(SfpAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        { typeof(CableModemAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        { typeof(OntAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        { typeof(CellularAlertEvaluator), new[] { typeof(string), typeof(SiteAlertEventBus) } },
        // MonitoringCollectionRegistry
        { typeof(MonitoringCollectionAgent), new[] { typeof(string) } },
        // MonitoringInfluxRegistry / MonitoringLiveStatsRegistry
        { typeof(NetworkOptimizer.Storage.Services.MonitoringInfluxClient), new[] { typeof(string) } },
        { typeof(MonitoringLiveStats), new[] { typeof(string) } },
        // SpeedTestServiceRegistry
        { typeof(TopologySnapshotService), new[] { typeof(UniFiConnectionService), typeof(NetworkPathAnalyzer) } },
        { typeof(ClientSpeedTestService), new[] { typeof(string), typeof(NetworkPathAnalyzer), typeof(TopologySnapshotService), typeof(SiteAlertEventBus) } },
        { typeof(GatewayWanSpeedTestService), new[] { typeof(string), typeof(NetworkPathAnalyzer), typeof(SiteAlertEventBus) } },
        { typeof(UwnSpeedTestService), new[] { typeof(string), typeof(NetworkPathAnalyzer), typeof(SiteAlertEventBus) } },
        { typeof(Iperf3SpeedTestService), new[] { typeof(string), typeof(NetworkPathAnalyzer), typeof(TopologySnapshotService), typeof(SiteAlertEventBus) } },
        // WanDataUsageRegistry
        { typeof(WanDataUsageService), new[] { typeof(string), typeof(SiteAlertEventBus) } },
    };

    [Theory]
    [MemberData(nameof(Constructions))]
    public void RegistryTargets_HaveConstructorAcceptingExplicitArguments(Type target, Type[] explicitArgs)
    {
        var compatible = target.GetConstructors().Any(ctor => CanConsumeAll(ctor, explicitArgs));
        Assert.True(compatible,
            $"{target.Name} has no public constructor that can consume the explicit ActivatorUtilities " +
            $"arguments [{string.Join(", ", explicitArgs.Select(t => t.Name))}]. This throws at runtime " +
            "when the registry builds the site's instance.");
    }

    /// <summary>
    /// Mirrors ActivatorUtilities' necessary condition: every explicit argument must be
    /// assignable to a distinct parameter of the constructor (order-independent here;
    /// none of the registries pass two arguments assignable to the same parameter type).
    /// </summary>
    private static bool CanConsumeAll(ConstructorInfo ctor, Type[] argTypes)
    {
        var parameters = ctor.GetParameters();
        var used = new bool[parameters.Length];
        foreach (var argType in argTypes)
        {
            var matched = false;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (used[i] || !parameters[i].ParameterType.IsAssignableFrom(argType))
                    continue;
                used[i] = true;
                matched = true;
                break;
            }
            if (!matched)
                return false;
        }
        return true;
    }
}
