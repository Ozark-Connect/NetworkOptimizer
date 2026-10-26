using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

public class AttentionTests
{
    [Fact]
    public void CurrentIssueKeys_ComesFromTheNewestAuditedSnapshot()
    {
        var snapshots = new List<ConfigSnapshot>
        {
            new() { Id = 3, AuditIssuesJson = null },
            new() { Id = 2, AuditIssuesJson = """[{"Key":"isolation:cameras","Title":"Security Network Not Isolated","Severity":"Critical"}]""" },
            new() { Id = 1, AuditIssuesJson = """[{"Key":"dns:iot","Title":"DNS: External DNS Bypass","Severity":"Recommended"}]""" },
        };

        ChangeControlFleetService.CurrentIssueKeys(snapshots).Should().BeEquivalentTo(["isolation:cameras"]);
    }

    [Fact]
    public void CurrentIssueKeys_IsNull_WhenNoSnapshotWasAudited() =>
        ChangeControlFleetService.CurrentIssueKeys([new ConfigSnapshot { Id = 1 }]).Should().BeNull();
}
