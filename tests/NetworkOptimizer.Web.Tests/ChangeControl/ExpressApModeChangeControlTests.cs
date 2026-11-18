using System.Text.Json.Nodes;
using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// An Express adopted as an AP reports type "udm" with device_mode_override "mesh". Change Control
/// treats it as an AP, not a gateway, whether the mode sits at the top level or in the device meta.
/// </summary>
public class ExpressApModeChangeControlTests
{
    private static JsonObject Device(string mac, string type, string? modeTop = null, string? modeMeta = null, string? mgmt = null)
    {
        var meta = new JsonObject { ["type"] = type, ["model"] = type == "udm" ? "UX" : "U6", ["state"] = 1 };
        if (modeMeta != null) meta["device_mode_override"] = modeMeta;
        var d = new JsonObject
        {
            ["_id"] = mac.Replace(":", ""), ["mac"] = mac, ["name"] = $"Device {mac[^2..]}", ["type"] = type,
            ["adopted"] = true, [ConfigCollections.DeviceMetaField] = meta,
        };
        if (modeTop != null) d["device_mode_override"] = modeTop;
        if (mgmt != null) d["mgmt_network_id"] = mgmt;
        return d;
    }

    [Fact]
    public void IsGateway_ModeAtTopLevelOrInMeta()
    {
        ConfigCollections.IsGateway(Device("aa:bb:cc:00:00:01", "udm", modeTop: "mesh")).Should().BeFalse();
        ConfigCollections.IsGateway(Device("aa:bb:cc:00:00:01", "udm", modeMeta: "mesh")).Should().BeFalse();
        ConfigCollections.IsGateway(Device("aa:bb:cc:00:00:01", "udm")).Should().BeTrue("a snapshot without the field reads as today");
        ConfigCollections.IsGateway(Device("aa:bb:cc:00:00:01", "udm", modeTop: "managed_shadow_gateway")).Should().BeTrue();
        ConfigCollections.IsGateway(Device("aa:bb:cc:00:00:01", "uap")).Should().BeFalse();
    }

    [Fact]
    public void IsGateway_TypeOnlyInMeta()
    {
        var d = Device("aa:bb:cc:00:00:01", "uxg");
        d.Remove("type");

        ConfigCollections.IsGateway(d).Should().BeTrue();
    }

    [Fact]
    public void HasGateway_SiteWhoseOnlyUdmIsAnExpressAp_HasNone()
    {
        var state = new ConfigState();
        state.Collections["device"] = [Device("aa:bb:cc:00:00:02", "udm", modeTop: "mesh"), Device("aa:bb:cc:00:00:03", "uap")];

        PlanCoordinator.HasGateway(state).Should().BeFalse();
        ChangeControlService.GatewayIds(state).Should().BeEmpty();

        state.Collections["device"].Add(Device("aa:bb:cc:00:00:01", "udm"));
        PlanCoordinator.HasGateway(state).Should().BeTrue();
        ChangeControlService.GatewayIds(state).Should().Equal("aabbcc000001");
    }

    [Fact]
    public void ReplaceDevice_ExpressInApMode_IsAnAp()
    {
        var express = Device("aa:bb:cc:00:00:02", "udm", modeMeta: "mesh");
        var meta = (JsonObject)express[ConfigCollections.DeviceMetaField]!;
        meta["radios"] = new JsonArray(new JsonObject { ["radio"] = "ng" }, new JsonObject { ["radio"] = "na" });
        meta["ports"] = new JsonArray(
            new JsonObject { ["port_idx"] = 1, ["media"] = "GE" },
            new JsonObject { ["port_idx"] = 2, ["media"] = "GE", ["is_uplink"] = true });
        var ap = Device("aa:bb:cc:00:00:03", "uap");
        ((JsonObject)ap[ConfigCollections.DeviceMetaField]!)["radios"] = new JsonArray(new JsonObject { ["radio"] = "na" });
        var context = new ConfigState();
        context.Collections["device"] = [express, ap];

        DeviceReplacement.Propose(express, ap, context).Kind.Should().Be("ap");
    }

    [Fact]
    public void InformPath_BlockFromExpressManagementNetwork_IsFlagged()
    {
        // The Express's management network informs to the Console host; the plan blocks TCP 8080 to it.
        var state = new ConfigState();
        state.Collections["networkconf"] =
        [
            new JsonObject { ["_id"] = "n-ap", ["name"] = "AP Net", ["ip_subnet"] = "192.0.2.1/24" },
            new JsonObject { ["_id"] = "n-srv", ["name"] = "Servers", ["ip_subnet"] = "198.51.100.1/24" },
        ];
        state.Collections["firewall_zone"] =
        [
            new JsonObject { ["_id"] = "z-int", ["zone_key"] = "internal", ["network_ids"] = new JsonArray("n-ap", "n-srv") },
        ];
        state.Collections["device"] = [Device("aa:bb:cc:00:00:02", "udm", modeTop: "mesh", mgmt: "n-ap")];
        var policy = new JsonObject
        {
            ["_id"] = "policy-inform", ["name"] = "Block inform", ["enabled"] = true, ["action"] = "BLOCK", ["protocol"] = "tcp",
            ["source"] = new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "NETWORK", ["network_ids"] = new JsonArray("n-ap"), ["port_matching_type"] = "ANY" },
            ["destination"] = new JsonObject { ["zone_id"] = "z-int", ["matching_target"] = "ANY", ["port_matching_type"] = "SPECIFIC", ["port"] = "8080" },
        };
        // Live allows the path explicitly; the plan replaces the allow with the block.
        var allow = (JsonObject)policy.DeepClone();
        allow["_id"] = "policy-allow";
        allow["action"] = "ALLOW";
        state.Collections["firewall_policy"] = [allow];
        var planned = state.Clone();
        planned.Collections["firewall_policy"] = [policy];
        var doc = new PlanDocument
        {
            Operations = [new PlanOperation
            {
                Key = "firewall_policy:create:inform", Collection = "firewall_policy",
                Action = PlanAction.Create, ObjectName = "Block inform", Body = policy,
            }],
        };

        var findings = LockoutChecker.Check(state, planned, doc, new PlanOptions { InformAddresses = ["198.51.100.10"] });

        findings.Should().Contain(f => f.Title.Contains("lose the UniFi Console"));
    }
}
