using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The transfer seam. SFTP, SCP, and cat-over-exec were all measured working on a real AP with the
/// OpenSSH CLI, but not with SSH.NET, so the method has to be swappable without touching callers.
/// </summary>
public class ApAgentTransferSelectorTests
{
    private sealed class StubTransfer(string name) : IApAgentBinaryTransfer
    {
        public string Name { get; } = name;

        public Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static ApAgentTransferSelector Selector() => new(
    [
        new StubTransfer("sftp"),
        new StubTransfer("scp"),
        new StubTransfer("exec"),
    ]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingConfigured_uses_sftp(string? configured)
    {
        Selector().Resolve(configured).Name.Should().Be("sftp");
    }

    [Theory]
    [InlineData("scp", "scp")]
    [InlineData("exec", "exec")]
    [InlineData("SCP", "scp")]
    [InlineData(" exec ", "exec")]
    public void AConfiguredMethod_wins(string configured, string expected)
    {
        Selector().Resolve(configured).Name.Should().Be(expected);
    }

    [Fact]
    public void AnUnknownMethod_falls_back_rather_than_failing_a_deploy()
    {
        Selector().Resolve("rsync").Name.Should().Be("sftp");
    }

    [Fact]
    public void EveryShippedTransfer_is_reachable_by_name()
    {
        var shipped = new IApAgentBinaryTransfer[]
        {
            new SftpApAgentBinaryTransfer(null!),
            new ScpApAgentBinaryTransfer(null!),
            new ExecApAgentBinaryTransfer(null!),
        };
        var selector = new ApAgentTransferSelector(shipped);

        foreach (var transfer in shipped)
            selector.Resolve(transfer.Name).Should().BeSameAs(transfer);
    }
}
