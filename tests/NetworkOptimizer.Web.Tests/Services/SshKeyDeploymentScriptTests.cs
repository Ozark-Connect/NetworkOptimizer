using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;
using Paths = NetworkOptimizer.Web.Services.Ssh.SshKeyDeploymentService.GatewayPaths;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Runs the real Deploy, boot, and Remove shell commands against a temp authorized_keys. The rule under
/// test: Remove takes out only a key Network Optimizer placed, so an operator who manages their own key
/// with it never loses their access. Needs a POSIX sh (Linux CI, or Git for Windows); skipped without one.
/// </summary>
public sealed class SshKeyDeploymentScriptTests : IDisposable
{
    private const string Blob = "AAAAC3NzaC1lZDI1NTE5AAAAIManagedKeyBlobForTestsOnly0000000000000";
    private const string ManagedLine = "ssh-ed25519 " + Blob + " network-optimizer";
    private const string OwnLine = "ssh-ed25519 " + Blob + " user1@laptop";
    private const string OtherLine = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOtherKeyBlob1111111111111111111111111 admin@desk";

    private static readonly string? Shell = FindShell();
    private readonly string _root;
    private readonly Paths _p;

    public SshKeyDeploymentScriptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "netopt-sshkey-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        Directory.CreateDirectory(_root + "/data/on_boot.d");
        _p = new Paths(_root + "/data/on_boot.d", _root + "/data/netopt/ssh-key.pub",
            _root + "/data/netopt/ssh-key.preexisting", _root + "/root/.ssh/authorized_keys");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void FreshInstall_AppendsOnce_AndRemoveTakesItOut()
    {
        if (Shell is null) return;
        WriteAuth(OtherLine);

        Run(Deploy()).Should().Contain("DEPLOYED");
        AuthLines().Should().Equal(OtherLine, ManagedLine);
        File.Exists(_p.PreexistingPath).Should().BeFalse();

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("REMOVED");
        AuthLines().Should().Equal(OtherLine);
        File.Exists(_p.ScriptPath).Should().BeFalse();
        File.Exists(_p.KeyPath).Should().BeFalse();
    }

    [Fact]
    public void KeyAlreadyPresent_KeepsTheOwnersLine_AndRemoveLeavesIt()
    {
        if (Shell is null) return;
        WriteAuth(OwnLine, OtherLine);

        Run(Deploy()).Should().Contain("DEPLOYED");
        AuthLines().Should().Equal(OwnLine, OtherLine);
        File.Exists(_p.PreexistingPath).Should().BeTrue();

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("KEPT_PREEXISTING");
        AuthLines().Should().Equal(OwnLine, OtherLine);
        File.Exists(_p.ScriptPath).Should().BeFalse();
        File.Exists(_p.KeyPath).Should().BeFalse();
        File.Exists(_p.PreexistingPath).Should().BeFalse();
    }

    [Fact]
    public void Redeploy_KeepsTheFirstPlacementsRecord()
    {
        if (Shell is null) return;
        WriteAuth(OtherLine);

        Run(Deploy());
        Run(Deploy()).Should().Contain("DEPLOYED");
        AuthLines().Should().Equal(OtherLine, ManagedLine);
        File.Exists(_p.PreexistingPath).Should().BeFalse("our own placement must not read as the operator's");

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("REMOVED");
        AuthLines().Should().Equal(OtherLine);
    }

    [Fact]
    public void OlderDeployment_WithoutARecord_RemovesAsBefore()
    {
        if (Shell is null) return;
        // What an older version leaves: the stored copy and our line, and no record.
        WriteFile(_p.KeyPath, ManagedLine + "\n");
        WriteAuth(OtherLine, ManagedLine);

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("REMOVED");
        AuthLines().Should().Equal(OtherLine);
    }

    [Fact]
    public void NeverPlaced_RemoveLeavesAuthorizedKeysAlone()
    {
        if (Shell is null) return;
        WriteAuth(OwnLine, OtherLine);

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("NOT_PLACED");
        AuthLines().Should().Equal(OwnLine, OtherLine);
    }

    [Fact]
    public void Preexisting_StaysTheOperatorsAfterAWipeAndBoot()
    {
        if (Shell is null) return;
        WriteAuth(OwnLine);
        Run(Deploy());

        // A firmware upgrade empties authorized_keys; the boot script puts the key back.
        WriteAuth();
        Run($"'{_p.ScriptPath}'");
        AuthLines().Should().Equal(ManagedLine);

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("KEPT_PREEXISTING");
        AuthLines().Should().Equal(ManagedLine);
    }

    [Fact]
    public void BootScript_IsIdempotent_AndStartsANewLine()
    {
        if (Shell is null) return;
        WriteFile(_p.AuthPath, OtherLine); // no trailing newline
        Run(Deploy());
        Run($"'{_p.ScriptPath}'");

        AuthLines().Should().Equal(OtherLine, ManagedLine);
    }

    [Fact]
    public void NewKey_StartsAFreshRecord()
    {
        if (Shell is null) return;
        WriteAuth(OwnLine);
        Run(Deploy());
        File.Exists(_p.PreexistingPath).Should().BeTrue();

        const string newBlob = "AAAAC3NzaC1lZDI1NTE5AAAAINewKeyBlob222222222222222222222222222";
        Run(SshKeyDeploymentService.DeployCommand(_p, $"ssh-ed25519 {newBlob} network-optimizer", newBlob))
            .Should().Contain("DEPLOYED");
        File.Exists(_p.PreexistingPath).Should().BeFalse();

        Run(SshKeyDeploymentService.RemoveCommand(_p)).Should().Contain("REMOVED");
        AuthLines().Should().Equal(OwnLine);
    }

    private string Deploy() => SshKeyDeploymentService.DeployCommand(_p, ManagedLine, Blob);

    private void WriteAuth(params string[] lines) =>
        WriteFile(_p.AuthPath, lines.Length == 0 ? "" : string.Join("\n", lines) + "\n");

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string[] AuthLines() =>
        File.ReadAllText(_p.AuthPath).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string Run(string command)
    {
        var psi = new ProcessStartInfo(Shell!) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    private static string? FindShell()
    {
        if (!OperatingSystem.IsWindows())
            return File.Exists("/bin/sh") ? "/bin/sh" : null;
        const string gitSh = @"C:\Program Files\Git\bin\sh.exe";
        return File.Exists(gitSh) ? gitSh : null;
    }
}
