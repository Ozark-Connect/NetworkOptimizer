using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The transfer chain. The status probe measures which transfer binaries an AP's firmware ships,
/// the selector orders the chain from that, and cat-over-exec is always the floor: a present binary
/// is still not proof SSH.NET can drive dropbear's implementation of it, so the chain is attempted
/// in order rather than trusted.
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

    private static ApAgentSshStatus Status(bool sftp, bool scp)
        => new() { SftpAvailable = sftp, ScpAvailable = scp };

    [Theory]
    [InlineData(true, true, new[] { "sftp", "scp", "exec" })]
    [InlineData(true, false, new[] { "sftp", "exec" })]
    [InlineData(false, true, new[] { "scp", "exec" })]
    [InlineData(false, false, new[] { "exec" })]
    public void TheChain_is_ordered_from_what_the_probe_found(bool sftp, bool scp, string[] expected)
    {
        Selector().Resolve(Status(sftp, scp)).Select(t => t.Name).Should().Equal(expected);
    }

    [Fact]
    public void Exec_is_always_last_and_always_present()
    {
        foreach (var sftp in new[] { false, true })
        foreach (var scp in new[] { false, true })
        {
            var chain = Selector().Resolve(Status(sftp, scp));
            chain.Should().NotBeEmpty();
            chain[^1].Name.Should().Be("exec");
        }
    }

    [Fact]
    public void EveryShippedTransfer_appears_in_the_chain_for_some_combination()
    {
        var shipped = new IApAgentBinaryTransfer[]
        {
            new SftpApAgentBinaryTransfer(null!),
            new ScpApAgentBinaryTransfer(null!),
            new ExecApAgentBinaryTransfer(null!),
        };
        var selector = new ApAgentTransferSelector(shipped);

        var reachable = new HashSet<IApAgentBinaryTransfer>();
        foreach (var sftp in new[] { false, true })
        foreach (var scp in new[] { false, true })
        {
            foreach (var transfer in selector.Resolve(Status(sftp, scp)))
                reachable.Add(transfer);
        }

        foreach (var transfer in shipped)
            reachable.Should().Contain(transfer);
    }
}
