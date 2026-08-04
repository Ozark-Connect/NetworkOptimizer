using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Components.Shared;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// This project has no Blazor component-test harness (no bunit), so the WAN context form's rules
/// are covered here through the pure validation function the component calls. The wiring around it
/// - which fields are shown, the interface auto-fill from the selected WAN - still needs manual
/// verification. ValidateContext is exposed internal (see NetworkOptimizer.Web.csproj
/// InternalsVisibleTo).
/// </summary>
public class WanContextsCardTests
{
    private static readonly string[] NoOtherContexts = Array.Empty<string>();

    [Fact]
    public void Validate_SourceIpContext_IsAccepted()
    {
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "192.0.2.10",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().BeNull();
    }

    [Fact]
    public void Validate_AgentWithInterfaceBind_IsAccepted()
    {
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "",
            agentId: 2, interfaceName: "eth8", otherNames: NoOtherContexts);

        error.Should().BeNull();
    }

    [Fact]
    public void Validate_MissingWan_IsRejected()
    {
        // A context with no WAN cannot say which WAN its measurements describe.
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "", sourceIp: "192.0.2.10",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().Contain("WAN");
    }

    [Fact]
    public void Validate_SourceIpAndAgentTogether_IsRejected()
    {
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "192.0.2.10",
            agentId: 2, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().Contain("not both");
    }

    [Fact]
    public void Validate_InterfaceWithoutAgent_IsRejected()
    {
        // Nothing on this server can bind a name only the gateway resolves.
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "",
            agentId: null, interfaceName: "eth8", otherNames: NoOtherContexts);

        error.Should().Contain("agent");
    }

    [Fact]
    public void Validate_MalformedSourceIp_IsRejected()
    {
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "not-an-ip",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().Contain("valid IP address");
    }

    [Fact]
    public void Validate_DuplicateName_IsRejected_CaseInsensitively()
    {
        var error = WanContextsCard.ValidateContext(
            name: "Backup", wanInterface: "wan2", sourceIp: "",
            agentId: 2, interfaceName: "", otherNames: new[] { "backup" });

        error.Should().Contain("already exists");
    }

    [Fact]
    public void Validate_EditingAContextKeepingItsOwnName_IsAccepted()
    {
        // The caller passes the OTHER contexts' names, so a rename to itself is not a clash.
        var error = WanContextsCard.ValidateContext(
            name: "backup", wanInterface: "wan2", sourceIp: "",
            agentId: 2, interfaceName: "eth8", otherNames: new[] { "starlink" });

        error.Should().BeNull();
    }

    [Fact]
    public void Validate_EmptyName_IsRejected()
    {
        var error = WanContextsCard.ValidateContext(
            name: "", wanInterface: "wan2", sourceIp: "",
            agentId: 2, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().Contain("name");
    }

    [Theory]
    [InlineData("wan2")]
    [InlineData("WAN2")]
    [InlineData("wan")]
    [InlineData("wan1")]
    public void Validate_NameThatIsAnotherWansKey_IsRejected(string name)
    {
        // The context's name is written as an Influx wan tag alongside the stable wan key, so a
        // context on wan3 named "wan2" would file its points under WAN2's report and swallow that
        // WAN's measurements.
        var error = WanContextsCard.ValidateContext(
            name: name, wanInterface: "wan3", sourceIp: "192.0.2.10",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().Be("A name that looks like a WAN key must match the context's own WAN.");
    }

    [Theory]
    [InlineData("wan2", "wan2")]
    [InlineData("WAN2", "wan2")]
    [InlineData("wan", "wan")]
    [InlineData("wan1", "wan")]   // the wan1 alias IS the primary's key, not a rival WAN
    [InlineData("wan", "wan1")]
    public void Validate_NameThatIsItsOwnWansKey_IsAccepted(string name, string wanInterface)
    {
        var error = WanContextsCard.ValidateContext(
            name: name, wanInterface: wanInterface, sourceIp: "192.0.2.10",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("starlink")]
    [InlineData("wan backup")]
    [InlineData("wan2-backup")]
    [InlineData("lte-wan2")]
    public void Validate_NameThatMerelyMentionsAWan_IsAccepted(string name)
    {
        // Only a name that IS a bare wan key can be mistaken for one in the tag chain.
        var error = WanContextsCard.ValidateContext(
            name: name, wanInterface: "wan3", sourceIp: "192.0.2.10",
            agentId: null, interfaceName: "", otherNames: NoOtherContexts);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData("wan", 1)]
    [InlineData("wan1", 1)]
    [InlineData("wan2", 2)]
    [InlineData("WAN3", 3)]
    [InlineData("", 0)]
    [InlineData("eth8", 0)]
    public void WanIndexFromKey_FollowsUniFisConvention(string key, int expected)
    {
        GatewayWanHelper.WanIndexFromKey(key).Should().Be(expected);
    }

    [Fact]
    public void WanLabel_EchoesUniFisFriendlyNamePlusGroupConvention()
    {
        // The WAN picker has to read like the one in UniFi Network's policy table so the user can
        // match them up: "Internet 1 WAN1" for a default name, "My ISP WAN2" for a renamed one.
        GatewayWanHelper.FormatWanLabel("Internet 1", GatewayWanHelper.WanIndexFromKey("wan"), null, null)
            .Should().Be("Internet 1 WAN1");
        GatewayWanHelper.FormatWanLabel("My ISP", GatewayWanHelper.WanIndexFromKey("wan2"), null, null)
            .Should().Be("My ISP WAN2");
        GatewayWanHelper.FormatWanLabel(null, GatewayWanHelper.WanIndexFromKey("wan2"), null, null)
            .Should().Be("WAN2");
    }

    [Fact]
    public void ASourceIpContextIsRejectedOnASiteTheServerDoesNotProbe()
    {
        // Source-IP contexts are probed by the server binding that address, and the server only
        // probes the main site. On any other site this would look configured and collect nothing.
        WanContextsCard.ValidateContext(
            "backup", "wan2", "198.51.100.7", agentId: null, interfaceName: null,
            otherNames: Array.Empty<string>(), serverProbesThisSite: false)
            .Should().Be("This site is probed by its agent, so assign one to this WAN.");
    }

    [Fact]
    public void ASourceIpContextIsFineOnTheMainSite()
    {
        WanContextsCard.ValidateContext(
            "backup", "wan2", "198.51.100.7", agentId: null, interfaceName: null,
            otherNames: Array.Empty<string>(), serverProbesThisSite: true)
            .Should().BeNull();
    }

    [Fact]
    public void AnAgentAssignedContextIsFineOnAnySite()
    {
        WanContextsCard.ValidateContext(
            "backup", "wan2", sourceIp: null, agentId: 4, interfaceName: null,
            otherNames: Array.Empty<string>(), serverProbesThisSite: false)
            .Should().BeNull();
    }
}
