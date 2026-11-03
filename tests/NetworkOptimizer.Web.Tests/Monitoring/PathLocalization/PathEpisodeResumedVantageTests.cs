using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// The vantage a resumed episode target traces from: its last trace's, else its context's, bound the
/// way the context's agent is pushed.
/// </summary>
public class PathEpisodeResumedVantageTests
{
    private static PathEpisodeTrace Trace(string vantage, string? bind) => new() { Vantage = vantage, SourceInterface = bind };

    private static WanContext Context(int? agentId, string? interfaceName = null, string? sourceIp = null) =>
        new() { AgentId = agentId, InterfaceName = interfaceName, ProbeSourceIp = sourceIp };

    [Fact]
    public void A_traced_target_keeps_its_traces_vantage()
    {
        PathEpisodeWatcher.ResumedVantage(Trace("agent-3", "ppp0"), Context(9, sourceIp: "198.51.100.2"))
            .Should().Be(new PathVantage(3, "ppp0"));
    }

    [Fact]
    public void A_traced_server_target_keeps_server()
    {
        PathEpisodeWatcher.ResumedVantage(Trace("server", null), Context(9))
            .Should().Be(new PathVantage(null, null));
    }

    [Fact]
    public void An_untraced_target_on_an_on_gateway_agent_binds_its_interface()
    {
        PathEpisodeWatcher.ResumedVantage(null, Context(5, interfaceName: "ppp0"))
            .Should().Be(new PathVantage(5, "ppp0"));
    }

    [Fact]
    public void An_untraced_target_on_a_policy_routed_agent_binds_its_source_ip()
    {
        PathEpisodeWatcher.ResumedVantage(null, Context(5, sourceIp: "198.51.100.2"))
            .Should().Be(new PathVantage(5, "198.51.100.2"));
    }

    [Fact]
    public void The_interface_wins_over_the_source_ip()
    {
        PathEpisodeWatcher.ResumedVantage(null, Context(5, "ppp0", "198.51.100.2"))
            .Should().Be(new PathVantage(5, "ppp0"));
    }

    [Fact]
    public void An_untraced_server_context_target_binds_the_source_ip()
    {
        PathEpisodeWatcher.ResumedVantage(null, Context(null, sourceIp: "198.51.100.2"))
            .Should().Be(new PathVantage(null, "198.51.100.2"));
    }

    [Fact]
    public void An_untraced_target_with_no_context_has_no_bind()
    {
        PathEpisodeWatcher.ResumedVantage(null, null)
            .Should().Be(new PathVantage(null, null));
    }

    [Fact]
    public void The_fallback_bind_is_what_the_agent_is_pushed()
    {
        var context = Context(5, "ppp0", "198.51.100.2");
        PathEpisodeWatcher.ResumedVantage(null, context).SourceInterface
            .Should().Be(AgentProbeResultSink.ResolveSpecSourceIp(context, 5));
    }

    [Fact]
    public void FromLabel_reads_an_agent_label()
    {
        PathVantage.FromLabel("agent-12", "eth4").Should().Be(new PathVantage(12, "eth4"));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("agent-x")]
    [InlineData("")]
    public void FromLabel_reads_server_and_garbage_as_server(string label)
    {
        PathVantage.FromLabel(label, null).AgentId.Should().BeNull();
    }
}
