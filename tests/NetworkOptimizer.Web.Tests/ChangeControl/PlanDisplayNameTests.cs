using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.ChangeControl;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class PlanDisplayNameTests
{
    private static string Display(PlanOperation operation, PlanDocument doc) =>
        (string)typeof(CcReviewPlan).GetMethod("DisplayObjectName", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [operation, doc])!;

    private static string DisplayWriteOrder(string collection, string name, PlanDocument doc) =>
        (string)typeof(CcReviewPlan).GetMethod("DisplayWriteOrderName", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [collection, name, doc])!;

    private static PlanOperation Zone(string name, string id) => new()
    {
        Key = $"firewall_zone:create:{name}:{id}", ObjectName = name,
        Collection = "firewall_zone", Action = PlanAction.Create,
    };

    [Theory]
    [InlineData("Office LAN")]
    [InlineData("Office, LAN")]
    [InlineData("Office, Guest LAN")]
    public void Resolves_complete_reference_with_legal_zone_name(string name)
    {
        var zone = Zone(name, "new-zone");
        var operation = new PlanOperation { ObjectName = $"Policy order, @ref:{zone.Key} to External", Action = PlanAction.Reorder };
        var doc = new PlanDocument { Operations = [zone, operation] };

        Display(operation, doc).Should().Be($"{name} → External");
    }

    [Fact]
    public void Resolves_multiple_references_using_longest_known_key_and_preserves_plan()
    {
        var prefix = Zone("Office", "zone");
        var longer = Zone("Office", "zone-extension");
        var comma = Zone("Guest, LAN", "guest");
        var operation = new PlanOperation
        {
            ObjectName = $"Policy order, @ref:{longer.Key} to @ref:{comma.Key}, @ref:{prefix.Key}",
            Action = PlanAction.Reorder,
        };
        var doc = new PlanDocument { Operations = [prefix, longer, comma, operation] };
        var original = JsonSerializer.Serialize(doc);

        Display(operation, doc).Should().Be("Office → Guest, LAN, Office");
        JsonSerializer.Serialize(doc).Should().Be(original);
    }

    [Fact]
    public void Write_order_resolves_references_and_formats_policy_order()
    {
        var zone = Zone("Management", "new-zone-management");
        var rawName = $"Policy order, @ref:{zone.Key} to Gateway";
        var reorder = new PlanOperation { Collection = "firewall_policy", ObjectName = rawName, Action = PlanAction.Reorder };
        var doc = new PlanDocument
        {
            Operations = [zone, reorder],
            WriteOrder = [new WriteStep { Groups = [new WriteStepGroup { Collection = "Firewall policies", Names = [rawName] }] }],
        };

        DisplayWriteOrder("Firewall policies", rawName, doc).Should().Be("Management → Gateway");
        doc.WriteOrder[0].Groups[0].Names.Should().ContainSingle().Which.Should().Be(rawName, "display formatting must not alter the stored plan");
    }

    [Theory]
    [InlineData("-unknown")]
    [InlineData(":unknown")]
    public void Preserves_unknown_reference_with_known_key_prefix(string suffix)
    {
        var zone = Zone("Office LAN", "zone");
        var operation = new PlanOperation { ObjectName = $"@ref:{zone.Key}{suffix} to External" };
        var doc = new PlanDocument { Operations = [zone, operation] };

        Display(operation, doc).Should().Be(operation.ObjectName);
    }

    [Theory]
    [InlineData("Plain zone name", PlanAction.Create, "Plain zone name")]
    [InlineData("Policy order, Internal to External", PlanAction.Reorder, "Internal → External")]
    [InlineData("Policy order, Internal to External", PlanAction.Update, "Policy order, Internal to External")]
    [InlineData("@ref:unknown:key to External", PlanAction.Update, "@ref:unknown:key to External")]
    public void Preserves_existing_formats_and_unknown_references(string name, PlanAction action, string expected)
    {
        var operation = new PlanOperation { ObjectName = name, Action = action };
        Display(operation, new PlanDocument { Operations = [operation] }).Should().Be(expected);
    }
}
