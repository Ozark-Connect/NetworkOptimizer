using System.Net;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Core SSH client service using SSH.NET library.
/// Provides cross-platform SSH support without external tool dependencies (no sshpass needed).
/// </summary>
public class SshClientService
{
    private readonly ILogger<SshClientService> _logger;
    private readonly AgentTunnelProxyService? _tunnelProxy;

    public SshClientService(ILogger<SshClientService> logger, AgentTunnelProxyService? tunnelProxy = null)
    {
        _logger = logger;
        _tunnelProxy = tunnelProxy;
    }

    /// <summary>
    /// The reason a connection really failed. A host reached through its site's agent is dialed on a
    /// loopback listener, and when the agent cannot open the far side the server simply closes that
    /// socket - which SSH.NET reports as a missing protocol banner. That message describes the
    /// symptom and hides the cause, so the agent's own reason replaces it when there is one.
    /// </summary>
    private string ExplainConnectionFailure(SshConnectionInfo connection, string sshNetMessage)
    {
        if (_tunnelProxy == null || !IPAddress.TryParse(connection.Host, out var ip) || !IPAddress.IsLoopback(ip))
            return sshNetMessage;
        return _tunnelProxy.RecentOpenFailure(connection.Port) is { } reason
            ? $"could not reach {reason}"
            : sshNetMessage;
    }

    /// <summary>
    /// Execute a command over SSH and return the result.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="command">Command to execute</param>
    /// <param name="timeout">Command timeout (default 30 seconds)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Command result with output, error, and exit code</returns>
    public async Task<SshCommandResult> ExecuteCommandAsync(
        SshConnectionInfo connection,
        string command,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        timeout ??= TimeSpan.FromSeconds(30);

        using var client = CreateSshClient(connection);

        try
        {
            await Task.Run(() => client.Connect(), cancellationToken);

            using var cmd = client.CreateCommand(command);
            cmd.CommandTimeout = timeout.Value;

            var output = await Task.Run(() => cmd.Execute(), cancellationToken);
            var error = cmd.Error ?? "";

            _logger.LogDebug("SSH command to {Host}: '{Command}' -> exit {ExitCode}",
                connection.Host, TruncateForLog(command), cmd.ExitStatus);

            return new SshCommandResult
            {
                Success = cmd.ExitStatus == 0,
                ExitCode = cmd.ExitStatus ?? -1,
                Output = output,
                Error = error
            };
        }
        catch (SshAuthenticationException ex)
        {
            _logger.LogError("SSH authentication failed for {Host}: {Error}", connection.Host, ex.Message);
            return new SshCommandResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Authentication failed: {ex.Message}"
            };
        }
        catch (SshConnectionException ex)
        {
            var explained = ExplainConnectionFailure(connection, ex.Message);
            _logger.LogError("SSH connection failed for {Host}: {Error}", connection.Host, explained);
            return new SshCommandResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Connection failed: {explained}"
            };
        }
        catch (SshOperationTimeoutException ex)
        {
            _logger.LogError("SSH command timed out for {Host}: {Error}", connection.Host, ex.Message);
            return new SshCommandResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Command timed out: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SSH error executing command on {Host}", connection.Host);
            return new SshCommandResult
            {
                Success = false,
                ExitCode = -1,
                Error = ex.Message
            };
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    /// <summary>
    /// Test SSH connection to the host.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection successful, false otherwise</returns>
    public async Task<(bool success, string message)> TestConnectionAsync(
        SshConnectionInfo connection,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateSshClient(connection);

        try
        {
            await Task.Run(() => client.Connect(), cancellationToken);

            if (client.IsConnected)
            {
                _logger.LogDebug("SSH connection test successful for {Host}", connection.Host);
                return (true, "Connection successful");
            }

            return (false, "Connection failed - not connected after Connect()");
        }
        catch (SshAuthenticationException ex)
        {
            _logger.LogWarning("SSH authentication failed for {Host}: {Error}", connection.Host, ex.Message);
            return (false, $"Authentication failed: {ex.Message}");
        }
        catch (SshConnectionException ex)
        {
            var explained = ExplainConnectionFailure(connection, ex.Message);
            _logger.LogWarning("SSH connection failed for {Host}: {Error}", connection.Host, explained);
            return (false, $"Connection failed: {explained}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH connection test failed for {Host}", connection.Host);
            return (false, $"Error: {ex.Message}");
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    /// <summary>
    /// Upload content to a file on the remote host via SFTP.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="content">File content to upload</param>
    /// <param name="remotePath">Destination path on remote host</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UploadFileAsync(
        SshConnectionInfo connection,
        string content,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        using var sftp = CreateSftpClient(connection);

        try
        {
            await Task.Run(() => sftp.Connect(), cancellationToken);

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            await Task.Run(() => sftp.UploadFile(stream, remotePath, true), cancellationToken);

            _logger.LogDebug("Uploaded file to {Host}:{Path} ({Bytes} bytes)",
                connection.Host, remotePath, content.Length);
        }
        finally
        {
            if (sftp.IsConnected)
            {
                sftp.Disconnect();
            }
        }
    }

    /// <summary>
    /// Upload a binary file to the remote host via SFTP.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="localFilePath">Local file path to upload</param>
    /// <param name="remotePath">Destination path on remote host</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UploadBinaryAsync(
        SshConnectionInfo connection,
        string localFilePath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        using var sftp = CreateSftpClient(connection);

        try
        {
            await Task.Run(() => sftp.Connect(), cancellationToken);

            using var stream = File.OpenRead(localFilePath);
            await Task.Run(() => sftp.UploadFile(stream, remotePath, true), cancellationToken);

            _logger.LogDebug("Uploaded binary to {Host}:{Path} ({Bytes} bytes)",
                connection.Host, remotePath, new FileInfo(localFilePath).Length);
        }
        finally
        {
            if (sftp.IsConnected)
            {
                sftp.Disconnect();
            }
        }
    }

    /// <summary>
    /// Upload a binary file to the remote host via SCP.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="localFilePath">Local file path to upload</param>
    /// <param name="remotePath">Destination path on remote host</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UploadBinaryViaScpAsync(
        SshConnectionInfo connection,
        string localFilePath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        // SCP passes the remote path through the far side's shell, so the transformation is not
        // optional: it is what stops a path becoming a command there.
        using var scp = new ScpClient(BuildConnectionInfo(connection), RemotePathTransformation.ShellQuote);

        try
        {
            await Task.Run(() => scp.Connect(), cancellationToken);

            using var stream = File.OpenRead(localFilePath);
            await Task.Run(() => scp.Upload(stream, remotePath), cancellationToken);

            _logger.LogDebug("Uploaded binary via SCP to {Host}:{Path} ({Bytes} bytes)",
                connection.Host, remotePath, new FileInfo(localFilePath).Length);
        }
        finally
        {
            if (scp.IsConnected)
            {
                scp.Disconnect();
            }
        }
    }

    /// <summary>
    /// Upload a binary file by streaming it into <c>cat &gt; path</c> over an exec channel. Needs no
    /// file-transfer subsystem on the far side at all, which makes it the floor when a device
    /// supports neither SFTP nor SCP.
    /// </summary>
    /// <param name="connection">SSH connection information</param>
    /// <param name="localFilePath">Local file path to upload</param>
    /// <param name="remotePath">Destination path on remote host</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task UploadBinaryViaExecAsync(
        SshConnectionInfo connection,
        string localFilePath,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateSshClient(connection);

        try
        {
            await Task.Run(() => client.Connect(), cancellationToken);

            using var command = client.CreateCommand($"cat > \"{remotePath}\"");
            var execute = command.ExecuteAsync(cancellationToken);

            // The input stream must be disposed before the command completes, or the far side never
            // sees end-of-input and cat runs forever.
            await using (var input = command.CreateInputStream())
            await using (var file = File.OpenRead(localFilePath))
            {
                await file.CopyToAsync(input, cancellationToken);
            }

            await execute;

            if (command.ExitStatus is not 0)
                throw new IOException($"Remote write of {remotePath} exited {command.ExitStatus}: {command.Error}");

            _logger.LogDebug("Uploaded binary via exec to {Host}:{Path} ({Bytes} bytes)",
                connection.Host, remotePath, new FileInfo(localFilePath).Length);
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    /// <summary>
    /// Check if a file exists on the remote host.
    /// </summary>
    public async Task<bool> FileExistsAsync(
        SshConnectionInfo connection,
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteCommandAsync(
            connection,
            $"test -f \"{remotePath}\" && echo 'exists' || echo 'not found'",
            TimeSpan.FromSeconds(10),
            cancellationToken);

        return result.Success && result.Output.Trim() == "exists";
    }

    /// <summary>
    /// Create an SSH client with the given connection info.
    /// </summary>
    private SshClient CreateSshClient(SshConnectionInfo connection)
        => new(BuildConnectionInfo(connection));

    /// <summary>
    /// Create an SFTP client with the given connection info.
    /// </summary>
    private SftpClient CreateSftpClient(SshConnectionInfo connection)
        => new(BuildConnectionInfo(connection));

    /// <summary>
    /// Build the SSH.NET connection info every client type shares, so a new transport (SCP here)
    /// authenticates exactly the way commands and SFTP already do.
    /// </summary>
    private Renci.SshNet.ConnectionInfo BuildConnectionInfo(SshConnectionInfo connection)
        => new(
            connection.Host,
            connection.Port,
            connection.Username,
            CreateAuthMethods(connection).ToArray())
        {
            Timeout = connection.Timeout
        };

    /// <summary>
    /// Create authentication methods based on connection credentials.
    /// </summary>
    private List<AuthenticationMethod> CreateAuthMethods(SshConnectionInfo connection)
    {
        var authMethods = new List<AuthenticationMethod>();

        // The site's stored key, decrypted into a stream so it is never written to the filesystem.
        // Offered ahead of a key file only because it is the one the app manages; both are additive,
        // and password auth below stays a working fallback either way.
        if (!string.IsNullOrEmpty(connection.StoredPrivateKeyPem))
        {
            try
            {
                using var pem = new MemoryStream(Encoding.UTF8.GetBytes(connection.StoredPrivateKeyPem));
                var storedKey = !string.IsNullOrEmpty(connection.StoredPrivateKeyPassphrase)
                    ? new PrivateKeyFile(pem, connection.StoredPrivateKeyPassphrase)
                    : new PrivateKeyFile(pem);

                authMethods.Add(new PrivateKeyAuthenticationMethod(connection.Username, storedKey));
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to load the site's stored SSH key: {Error}", ex.Message);
            }
        }

        // Prefer key-based auth if configured
        if (!string.IsNullOrEmpty(connection.PrivateKeyPath))
        {
            try
            {
                var keyFile = !string.IsNullOrEmpty(connection.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(connection.PrivateKeyPath, connection.PrivateKeyPassphrase)
                    : new PrivateKeyFile(connection.PrivateKeyPath);

                authMethods.Add(new PrivateKeyAuthenticationMethod(connection.Username, keyFile));
            }
            catch (Exception ex)
            {
                // The path is logged at Debug rather than Warning: it is a global-Admin-only value, and
                // the warning line is read by anyone who can see logs.
                _logger.LogWarning("Failed to load the configured private key file: {Error}", ex.Message);
                _logger.LogDebug("Private key file that failed to load: {Path}", connection.PrivateKeyPath);
            }
        }

        // Password-based auth: try both methods since devices vary
        // - UniFi Gateways use keyboard-interactive
        // - UniFi Switches/APs use standard password auth
        if (!string.IsNullOrEmpty(connection.Password))
        {
            // Standard password authentication
            authMethods.Add(new PasswordAuthenticationMethod(connection.Username, connection.Password));

            // Keyboard-interactive authentication (for UniFi Gateways)
            var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(connection.Username);
            keyboardInteractive.AuthenticationPrompt += (sender, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    // Respond to password prompts
                    if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    {
                        prompt.Response = connection.Password;
                    }
                }
            };
            authMethods.Add(keyboardInteractive);
        }

        if (authMethods.Count == 0)
        {
            var hint = !string.IsNullOrEmpty(connection.PrivateKeyPath)
                    || !string.IsNullOrEmpty(connection.StoredPrivateKeyPem)
                ? " (private key may be invalid or unreadable)"
                : " (no password or private key configured)";
            throw new InvalidOperationException(
                $"No authentication method available for {connection.Username}@{connection.Host}{hint}");
        }

        return authMethods;
    }

    /// <summary>
    /// Truncate command for logging (avoid logging sensitive data or very long commands).
    /// </summary>
    private static string TruncateForLog(string command)
    {
        const int maxLength = 100;
        if (command.Length <= maxLength) return command;
        return command[..maxLength] + "...";
    }
}
