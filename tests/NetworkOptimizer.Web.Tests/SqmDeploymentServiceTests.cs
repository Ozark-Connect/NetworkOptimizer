using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class SqmDeploymentServiceTests
{
    [Fact]
    public void DeploymentStatus_ValidatesTheManagedOoklaBuildAndBothParserDependencies()
    {
        var command = SqmDeploymentService.BuildDeploymentStatusCommand();

        command.Should().Contain(ScriptGenerator.ManagedSpeedtestPath);
        command.Should().Contain(ScriptGenerator.ManagedSpeedtestCliVersion);
        command.Should().Contain("sha256sum -c -");
        command.Should().Contain("---BC_CHECK---");
        command.Should().Contain("---JQ_CHECK---");
        command.Should().NotContain("which speedtest");
    }
}
