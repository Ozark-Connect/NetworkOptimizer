using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Threats;
using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Blocking;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Collection, Automatic Blocking, and the gateway agent service reach each other through factory
/// registrations the container cannot check for cycles; one shipped and hung startup. Builds them as the
/// app does and fails on a cycle or a hang.
/// </summary>
public class ThreatBlockingRegistrationTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new AgentTunnelOptions(true, 0));
        services.AddSingleton<AgentTunnelRegistry>();
        services.AddSingleton(Mock.Of<IAlertEventBus>());
        services.AddSingleton(Mock.Of<IUniFiClientAccessor>());
        services.AddSingleton<ThreatEventNormalizer>();
        services.AddSingleton<GeoEnrichmentService>();
        services.AddSingleton<KillChainClassifier>();
        services.AddSingleton<ThreatPatternAnalyzer>();
        services.AddThreatBlockingCore();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(typeof(ThreatCollectionService))]
    [InlineData(typeof(ThreatBlockingService))]
    [InlineData(typeof(IPublicListLookup))]
    [InlineData(typeof(IGatewayAgentBlocklist))]
    [InlineData(typeof(GatewayAgentBlocklist))]
    public async Task Resolves_WithoutACycle(Type service)
    {
        using var provider = Build();
        // Generous: a busy build machine must not read as a hang; a real cycle never returns (WaitAsync throws).
        var resolved = await Task.Run(() => provider.GetRequiredService(service)).WaitAsync(TimeSpan.FromSeconds(60));
        resolved.Should().NotBeNull();
    }

    [Fact]
    public async Task HostedServices_AllResolve()
    {
        using var provider = Build();
        var hosted = await Task.Run(() => provider.GetServices<IHostedService>().ToList()).WaitAsync(TimeSpan.FromSeconds(60));
        hosted.Should().HaveCount(2);
    }
}
