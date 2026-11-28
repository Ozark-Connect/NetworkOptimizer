using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The per-AP opt-in. An opt-out written before any deploy used to leave a bare MAC row (name, architecture and
/// error all empty) that nothing on the AP's table row pointed back from.
/// </summary>
public class ApAgentOptOutTests
{
    [Fact]
    public void OptingOutANewRow_NamesIt()
    {
        var record = new ApAgentDeployment { DeviceMac = "aa:bb:cc:dd:ee:01" };

        ApAgentDeploymentService.ApplyEnabled(record, enabled: false, "AP Long Range");

        record.Enabled.Should().BeFalse();
        record.DeviceName.Should().Be("AP Long Range");
    }

    [Fact]
    public void IncludingAnOptedOutRow_EnablesIt()
    {
        var record = new ApAgentDeployment { DeviceMac = "aa:bb:cc:dd:ee:01", Enabled = false };

        ApAgentDeploymentService.ApplyEnabled(record, enabled: true, "AP Long Range");

        record.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ANamedRowKeepsItsName()
    {
        var record = new ApAgentDeployment { DeviceMac = "aa:bb:cc:dd:ee:01", DeviceName = "Office AP" };

        ApAgentDeploymentService.ApplyEnabled(record, enabled: false, "Renamed AP");

        record.DeviceName.Should().Be("Office AP");
    }

    [Fact]
    public void NoNameGiven_LeavesTheRowAsItWas()
    {
        var record = new ApAgentDeployment { DeviceMac = "aa:bb:cc:dd:ee:01" };

        ApAgentDeploymentService.ApplyEnabled(record, enabled: false, null);

        record.Enabled.Should().BeFalse();
        record.DeviceName.Should().BeNullOrEmpty();
    }
}
