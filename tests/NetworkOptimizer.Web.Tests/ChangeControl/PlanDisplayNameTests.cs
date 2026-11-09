using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>
/// Review Plan display. Names are resolved when the plan is built (PlanBuilder names a reference to an object the plan
/// creates, see ZoneSetupTests.RehomedUpdate_NamesTheNewZone_NotItsReference); the page only formats them.
/// </summary>
public class PlanDisplayNameTests
{
    private static PlanOperation Zone(string name, string id) => new()
    {
        Key = $"firewall_zone:create:{name}:{id}", ObjectName = name,
        Collection = "firewall_zone", Action = PlanAction.Create,
    };

    [Theory]
    [InlineData("Plain zone name", PlanAction.Create, "Plain zone name")]
    [InlineData("Policy order, Internal to External", PlanAction.Reorder, "Internal → External")]
    [InlineData("Policy order, Office, LAN to Guest LAN", PlanAction.Reorder, "Office, LAN → Guest LAN")]
    [InlineData("Policy order, Internal to External", PlanAction.Update, "Policy order, Internal to External")]
    public void Formats_policy_order_and_leaves_other_names(string name, PlanAction action, string expected) =>
        CcReviewPlan.DisplayObjectName(new PlanOperation { ObjectName = name, Action = action }).Should().Be(expected);

    private static PlanOperation Policy(string name, PlanAction action) => new()
    {
        Key = $"firewall_policy:{action.ToString().ToLowerInvariant()}:{name}:id-{name}", ObjectName = name, Collection = "firewall_policy", Action = action,
    };

    [Fact]
    public void Write_order_groups_by_collection_in_order_with_resolved_names()
    {
        var zone = Zone("Management", "new-zone-management");
        var create = Policy("Block SNMP (Management)", PlanAction.Create);
        var update = Policy("[IoT] Allow Phone Inbound Access", PlanAction.Update);
        var reorder = new PlanOperation
        {
            Key = "firewall_policy:reorder:mgmt-gw", Collection = "firewall_policy", Action = PlanAction.Reorder,
            ObjectName = "Policy order, Management to Gateway",
        };
        var doc = new PlanDocument { Operations = [zone, create, update, reorder] };
        var step = new WriteStep
        {
            OperationKeys = [zone.Key, create.Key, update.Key, reorder.Key],
            Groups = [new WriteStepGroup { Collection = "Firewall zones", Names = ["Management"] }],
        };
        var original = JsonSerializer.Serialize(doc);

        var groups = CcReviewPlan.WriteGroups(step, doc);

        groups.Select(g => g.Collection).Should().Equal("Firewall zones", "Firewall policies");
        groups[1].Items.Should().Equal(
            new CcReviewPlan.WriteItem(PlanAction.Create, "Block SNMP (Management)"),
            new CcReviewPlan.WriteItem(PlanAction.Update, "[IoT] Allow Phone Inbound Access"),
            new CcReviewPlan.WriteItem(PlanAction.Reorder, "Management → Gateway"));
        JsonSerializer.Serialize(doc).Should().Be(original, "display formatting must not alter the stored plan");
    }

    [Fact]
    public void Write_order_skips_keys_the_plan_no_longer_has()
    {
        var create = Policy("A", PlanAction.Create);
        var doc = new PlanDocument { Operations = [create] };

        var groups = CcReviewPlan.WriteGroups(new WriteStep { OperationKeys = ["gone", create.Key] }, doc);

        groups.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.Name.Should().Be("A");
    }

    [Fact]
    public void Write_order_of_a_plan_built_before_operation_keys_falls_back_to_names()
    {
        var rawName = "Policy order, Management to Gateway";
        var reorder = new PlanOperation { Collection = "firewall_policy", ObjectName = rawName, Action = PlanAction.Reorder };
        var doc = new PlanDocument { Operations = [reorder] };
        var step = new WriteStep { Groups = [new WriteStepGroup { Collection = "Firewall policies", Names = [rawName, "Not in the plan"] }] };

        var groups = CcReviewPlan.WriteGroups(step, doc);

        groups.Should().ContainSingle().Which.Items.Should().Equal(
            new CcReviewPlan.WriteItem(PlanAction.Reorder, "Management → Gateway"),
            new CcReviewPlan.WriteItem(PlanAction.Update, "Not in the plan"));
        step.Groups[0].Names[0].Should().Be(rawName);
    }

    [Fact]
    public void Write_summary_counts_each_action_in_a_fixed_order()
    {
        var items = new List<CcReviewPlan.WriteItem>
        {
            new(PlanAction.Reorder, "r1"), new(PlanAction.Update, "u1"), new(PlanAction.Create, "c1"),
            new(PlanAction.Create, "c2"), new(PlanAction.Delete, "d1"), new(PlanAction.Reorder, "r2"),
        };

        CcReviewPlan.WriteSummary(items).Should().Be("2 creates, 1 update, 2 reorders, 1 delete");
        CcReviewPlan.WriteSummary([new(PlanAction.Update, "u")]).Should().Be("1 update");
        CcReviewPlan.WriteSummary([]).Should().BeEmpty();
    }

    [Fact]
    public void Identity_context_with_adds_names_and_wins_on_a_clash()
    {
        var ctx = new ConfigIdentityContext(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["z-int"] = "Internal", ["n1"] = "Old" });

        var more = ctx.With([("@ref:firewall_zone:create:IoT:new-zone-iot", "IoT"), ("N1", "New")]);

        more.NameOf("@ref:firewall_zone:create:IoT:new-zone-iot").Should().Be("IoT");
        more.NameOf("z-int").Should().Be("Internal");
        more.NameOf("n1").Should().Be("New", "IDs compare without case, as NamesById does");
        ctx.NameOf("@ref:firewall_zone:create:IoT:new-zone-iot").Should().Be("@ref:firewall_zone:create:IoT:new-zone-iot", "the original is unchanged");
    }
}
