using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// How the agent binary crosses the wire to an access point.
///
/// This is a seam rather than a call because the method is not settled. SFTP, SCP, and streaming
/// into <c>cat</c> were all measured working on a real AP (dropbear v2025.89), but with the OpenSSH
/// CLI - not with SSH.NET, which is what actually runs here. SFTP is the default; if SSH.NET trips
/// on dropbear, SCP is the drop-in and cat-over-exec is the floor, and swapping is a setting rather
/// than a code change at every call site.
/// </summary>
public interface IApAgentBinaryTransfer
{
    /// <summary>Setting value that selects this implementation.</summary>
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
/// Picks the transfer implementation. The default is SFTP; a site sets
/// <see cref="TransferMethodSettingKey"/> to "scp" or "exec" to swap it without a code change.
/// </summary>
public sealed class ApAgentTransferSelector(IEnumerable<IApAgentBinaryTransfer> transfers)
{
    /// <summary>Per-site setting key naming the transfer method.</summary>
    public const string TransferMethodSettingKey = "ap_agent.transfer";

    /// <summary>The method used when nothing has been configured.</summary>
    public const string DefaultMethod = "sftp";

    private readonly IReadOnlyList<IApAgentBinaryTransfer> _transfers = transfers.ToList();

    /// <summary>The transfer for a configured method name, falling back to the default.</summary>
    public IApAgentBinaryTransfer Resolve(string? method)
    {
        var wanted = string.IsNullOrWhiteSpace(method) ? DefaultMethod : method.Trim();
        return _transfers.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? _transfers.First(t => t.Name == DefaultMethod);
    }
}
