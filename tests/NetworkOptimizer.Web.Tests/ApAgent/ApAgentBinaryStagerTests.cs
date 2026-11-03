using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// Staging a new build while the old agent keeps running. Nothing staging does may touch the live
/// binary or the process: a copy cut off partway (a server restart mid-transfer) must leave the AP
/// on its old, working agent instead of a truncated binary and no agent.
/// </summary>
public class ApAgentBinaryStagerTests
{
    private const string LocalMd5 = "0123456789abcdef0123456789abcdef";
    private const string LivePath = ApAgentPaths.RemoteDir + "/" + ApAgentPaths.BinaryPrefix + "arm";

    private static readonly SshConnectionInfo Connection = new() { Host = "192.0.2.10", Username = "admin" };

    /// <summary>Records every command and upload, and answers md5 with whatever the test set.</summary>
    private sealed class FakeAp
    {
        public List<string> Commands { get; } = [];
        public List<(string Method, string Path)> Uploads { get; } = [];
        public string StagedMd5 { get; set; } = LocalMd5;
        public bool PrepareFails { get; set; }

        public Task<(bool success, string output)> Run(string command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command == ApAgentScripts.PrepareStagingCommand() && PrepareFails)
                return Task.FromResult((false, "read-only file system"));
            if (command == ApAgentScripts.StagedMd5Command())
                return Task.FromResult((true, StagedMd5 + "\n"));
            return Task.FromResult((true, ""));
        }

        public IEnumerable<string> EverythingSent
            => Commands.Concat(Uploads.Select(u => u.Path));
    }

    private sealed class FakeTransfer(FakeAp ap, string name, Func<CancellationToken, Task>? upload = null) : IApAgentBinaryTransfer
    {
        public string Name { get; } = name;

        public async Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default)
        {
            ap.Uploads.Add((Name, remotePath));
            if (upload != null) await upload(ct);
        }
    }

    private static Task<ApAgentBinaryStager.Result> Stage(FakeAp ap, params IApAgentBinaryTransfer[] chain)
        => Stage(ap, CancellationToken.None, chain);

    private static Task<ApAgentBinaryStager.Result> Stage(FakeAp ap, CancellationToken ct, params IApAgentBinaryTransfer[] chain)
        => ApAgentBinaryStager.StageAsync(ap.Run, chain, Connection, "local-file", LocalMd5, NullLogger.Instance, ct);

    private static void AssertLiveAgentUntouched(FakeAp ap)
    {
        ap.EverythingSent.Should().NotContain(s => s.Contains(LivePath), "staging never writes the live binary");
        ap.Commands.Should().NotContain(c => c.Contains("pkill") || c.Contains(" stop"), "staging never stops the agent");
    }

    [Fact]
    public async Task ASuccessful_stage_uploads_to_the_staging_path_and_leaves_the_live_agent_alone()
    {
        var ap = new FakeAp();

        var result = await Stage(ap, new FakeTransfer(ap, "sftp"));

        result.Success.Should().BeTrue();
        result.Method.Should().Be("sftp");
        ap.Uploads.Should().Equal(("sftp", ApAgentPaths.StagedBinaryPath));
        ap.Commands.Should().Equal(
            ApAgentScripts.PrepareStagingCommand(),
            ApAgentScripts.StagedMd5Command(),
            ApAgentScripts.MakeStagedExecutableCommand());
        AssertLiveAgentUntouched(ap);
    }

    [Fact]
    public async Task AnMd5_mismatch_falls_through_to_the_next_method()
    {
        var ap = new FakeAp();
        var sftp = new FakeTransfer(ap, "sftp", _ => { ap.StagedMd5 = "truncated"; return Task.CompletedTask; });
        var exec = new FakeTransfer(ap, "exec", _ => { ap.StagedMd5 = LocalMd5; return Task.CompletedTask; });

        var result = await Stage(ap, sftp, exec);

        result.Method.Should().Be("exec");
        result.Failures.Should().ContainKey("sftp").WhoseValue.Should().Contain("md5 mismatch");
        AssertLiveAgentUntouched(ap);
    }

    [Fact]
    public async Task EveryMethod_failing_reports_each_and_never_marks_the_file_executable()
    {
        var ap = new FakeAp { StagedMd5 = "truncated" };
        var sftp = new FakeTransfer(ap, "sftp", _ => throw new IOException("subsystem refused"));
        var exec = new FakeTransfer(ap, "exec");

        var result = await Stage(ap, sftp, exec);

        result.Success.Should().BeFalse();
        result.SetupError.Should().BeNull();
        result.Failures.Keys.Should().BeEquivalentTo("sftp", "exec");
        result.Failures["sftp"].Should().Be("subsystem refused");
        ap.Commands.Should().NotContain(ApAgentScripts.MakeStagedExecutableCommand());
        AssertLiveAgentUntouched(ap);
    }

    [Fact]
    public async Task ACopy_cut_off_by_shutdown_leaves_the_live_agent_running()
    {
        var ap = new FakeAp();
        using var cts = new CancellationTokenSource();
        var cutOff = new FakeTransfer(ap, "sftp", ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        var exec = new FakeTransfer(ap, "exec");

        var act = () => Stage(ap, cts.Token, cutOff, exec);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ap.Uploads.Select(u => u.Method).Should().Equal("sftp");
        AssertLiveAgentUntouched(ap);
    }

    [Fact]
    public async Task APrepare_failure_ends_staging_before_any_upload()
    {
        var ap = new FakeAp { PrepareFails = true };

        var result = await Stage(ap, new FakeTransfer(ap, "sftp"));

        result.Success.Should().BeFalse();
        result.SetupError.Should().StartWith("Could not create the install directory");
        ap.Uploads.Should().BeEmpty();
    }
}
