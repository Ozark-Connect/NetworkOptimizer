using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Gateway hardware in AP mode falls back to Gateway SSH only when Device SSH is refused. An
/// unreachable host or a failing command must not trigger it: that would retry with the wrong
/// credentials and mask the real error.
/// </summary>
public class ApAgentCredentialFallbackTests
{
    [Theory]
    [InlineData("Authentication failed: Permission denied (password).")]
    [InlineData("SSH credentials not configured")]
    [InlineData(ApAgentDeploymentService.GatewayCredentialsMissing)]
    public void ARefusedLogin_triggers_the_fallback(string output)
    {
        ApAgentDeploymentService.IsCredentialRefusal(output).Should().BeTrue();
    }

    [Theory]
    [InlineData("Connection failed: No route to host")]
    [InlineData("Command timed out: operation timed out")]
    [InlineData("mkdir: can't create directory '/tmp/netopt-apagent': Read-only file system")]
    [InlineData("")]
    public void AnythingElse_does_not(string output)
    {
        ApAgentDeploymentService.IsCredentialRefusal(output).Should().BeFalse();
    }
}
