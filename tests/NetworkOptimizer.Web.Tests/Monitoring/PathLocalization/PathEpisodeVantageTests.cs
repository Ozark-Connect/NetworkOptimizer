using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// Which executor a vantage runs on. A vantage with no agent is the "server" vantage and resolves as
/// ProbeExecutorFactory.GetServer does, so a resumed target with no stored trace keeps the site's vantage.
/// </summary>
public class PathEpisodeVantageTests
{
    private static PathEpisodeWatcher Watcher(string slug, bool withCoverage) => new(
        slug, null!, null!, null!, null!, null!,
        new LocalProbeExecutor(NullLogger<LocalProbeExecutor>.Instance), null!, null!, NullLoggerFactory.Instance,
        withCoverage ? new SiteAgentCoverage(new ServiceCollection().BuildServiceProvider()) : null);

    [Fact]
    public void A_named_agent_runs_on_that_agent()
    {
        Watcher("branch", withCoverage: true).ExecutorFor(new PathVantage(7, null))
            .Should().BeOfType<AgentProbeExecutor>();
    }

    [Fact]
    public void The_server_vantage_of_a_secondary_site_runs_on_its_agent()
    {
        Watcher("branch", withCoverage: true).ExecutorFor(new PathVantage(null, null))
            .Should().BeOfType<AgentProbeExecutor>();
    }

    [Fact]
    public void The_server_vantage_of_an_uncovered_default_site_runs_here()
    {
        Watcher(SiteManagementService.DefaultSiteSlug, withCoverage: true).ExecutorFor(new PathVantage(null, null))
            .Should().BeOfType<LocalProbeExecutor>();
    }
}
