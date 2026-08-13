using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class SshFailureSummaryTests
{
    private const string Host = "192.0.2.10";

    [Fact]
    public void RejectedCredentialsAreNamedAsSuch()
    {
        var message = SshFailureSummary.Describe(
            "Authentication failed: Permission denied (password,keyboard-interactive).", Host);

        Assert.Contains("authentication was rejected", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Host, message);
    }

    [Fact]
    public void AnUnreachableDeviceIsNotReportedAsAnAuthProblem()
    {
        var message = SshFailureSummary.Describe(
            "Connection failed: No connection could be made because the target machine actively refused it.", Host);

        Assert.Contains("Could not reach", message);
        Assert.DoesNotContain("authentication", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimeoutsReadAsTimeouts()
    {
        var message = SshFailureSummary.Describe("Command timed out: the operation has timed out.", Host);
        Assert.Contains("did not answer SSH in time", message);
    }

    [Fact]
    public void MissingCredentialsPointAtConfigurationRatherThanTheNetwork()
    {
        var message = SshFailureSummary.Describe("SSH credentials not configured", Host);
        Assert.Contains("not configured", message);
        Assert.DoesNotContain("Could not reach", message);
    }

    [Fact]
    public void TheAgentMessagePassesThroughUnchanged()
    {
        var message = SshFailureSummary.Describe(UniFiSshServiceMessage, Host);
        Assert.Equal(UniFiSshServiceMessage, message);
    }

    [Fact]
    public void NoOutputAtAllStillSaysSomethingUseful()
    {
        var message = SshFailureSummary.Describe(null, Host);
        Assert.Contains("No response from", message);
        Assert.Contains(Host, message);
    }

    [Fact]
    public void UnrecognizedOutputIsTrimmedToOneLine()
    {
        var message = SshFailureSummary.Describe("qmicli: could not open device\nsecond line\nthird line", Host);
        Assert.Equal("qmicli: could not open device", message);
    }

    [Fact]
    public void AVeryLongLineIsCapped()
    {
        var message = SshFailureSummary.Describe(new string('x', 500), Host);
        Assert.True(message.Length < 200);
        Assert.EndsWith("...", message);
    }

    private const string UniFiSshServiceMessage =
        "Waiting for the on-site agent to connect. This site's devices are reached through its agent, "
        + "and will connect automatically once the agent is online.";
}
