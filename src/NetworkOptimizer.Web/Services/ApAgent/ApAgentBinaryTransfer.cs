using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// How the agent binary crosses the wire to an access point.
///
/// Capability varies by firmware - dropbear's scp comes free as a multi-call symlink while
/// sftp-server is a separate optional binary - so the status probe tests for each and
/// <see cref="ApAgentTransferSelector"/> orders the chain from what the AP actually has, ending at
/// cat-over-exec, which needs nothing but a shell. A present binary is still not proof SSH.NET can
/// drive dropbear's implementation of it (all three were measured working with the OpenSSH CLI,
/// not SSH.NET), so a failed attempt falls through to the next method rather than failing the deploy.
/// </summary>
public interface IApAgentBinaryTransfer
{
    /// <summary>Method name, matched when ordering the chain and named in logs.</summary>
    string Name { get; }

    /// <summary>Uploads a local file to the AP.</summary>
    Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default);
}

/// <summary>SFTP transfer, the default. Dropbear serves the subsystem from /usr/libexec/sftp-server.</summary>
public sealed class SftpApAgentBinaryTransfer(SshClientService sshClient) : IApAgentBinaryTransfer
{
    /// <inheritdoc />
    public string Name => "sftp";

    /// <inheritdoc />
    public Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default)
        => sshClient.UploadBinaryAsync(connection, localFilePath, remotePath, ct);
}

/// <summary>SCP transfer. The AP carries /usr/sbin/scp.</summary>
public sealed class ScpApAgentBinaryTransfer(SshClientService sshClient) : IApAgentBinaryTransfer
{
    /// <inheritdoc />
    public string Name => "scp";

    /// <inheritdoc />
    public Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default)
        => sshClient.UploadBinaryViaScpAsync(connection, localFilePath, remotePath, ct);
}

/// <summary>Streams the file into <c>cat</c> over an exec channel, needing no subsystem on the AP.</summary>
public sealed class ExecApAgentBinaryTransfer(SshClientService sshClient) : IApAgentBinaryTransfer
{
    /// <inheritdoc />
    public string Name => "exec";

    /// <inheritdoc />
    public Task UploadAsync(SshConnectionInfo connection, string localFilePath, string remotePath, CancellationToken ct = default)
        => sshClient.UploadBinaryViaExecAsync(connection, localFilePath, remotePath, ct);
}

/// <summary>
/// Orders the transfer chain from what the status probe measured on the access point: SFTP when
/// the firmware ships sftp-server, then SCP when it ships scp, and cat-over-exec always last.
/// Resolution is per-AP from the probe rather than a stored preference, which is what lets a
/// mixed fleet put each AP on the fastest path its firmware supports.
/// </summary>
public sealed class ApAgentTransferSelector(IEnumerable<IApAgentBinaryTransfer> transfers)
{
    /// <summary>The SFTP transfer, the fast default when the firmware supports it.</summary>
    public const string SftpMethod = "sftp";

    /// <summary>The SCP transfer.</summary>
    public const string ScpMethod = "scp";

    /// <summary>The cat-over-exec transfer, the floor that needs nothing but a shell.</summary>
    public const string ExecMethod = "exec";

    private readonly IReadOnlyList<IApAgentBinaryTransfer> _transfers = transfers.ToList();

    /// <summary>The transfers to attempt in order for what the probe found, always ending in exec.</summary>
    public IReadOnlyList<IApAgentBinaryTransfer> Resolve(ApAgentSshStatus status)
    {
        var chain = new List<IApAgentBinaryTransfer>(3);
        if (status.SftpAvailable) chain.Add(ByName(SftpMethod));
        if (status.ScpAvailable) chain.Add(ByName(ScpMethod));
        chain.Add(ByName(ExecMethod));
        return chain;
    }

    private IApAgentBinaryTransfer ByName(string name)
        => _transfers.First(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
