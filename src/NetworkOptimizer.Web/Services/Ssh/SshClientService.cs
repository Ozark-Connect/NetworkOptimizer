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

    public SshClientService(ILogger<SshClientService> logger)
    {
        _logger = logger;
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
            _logger.LogError("SSH connection failed for {Host}: {Error}", connection.Host, ex.Message);
            return new SshCommandResult
            {
                Success = false,
                ExitCode = -1,
                Error = $"Connection failed: {ex.Message}"
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
            _logger.LogWarning("SSH connection failed for {Host}: {Error}", connection.Host, ex.Message);
            return (false, $"Connection failed: {ex.Message}");
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
    {
        var authMethods = CreateAuthMethods(connection);

        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
            connection.Host,
            connection.Port,
            connection.Username,
            authMethods.ToArray())
        {
            Timeout = connection.Timeout
        };

        return new SshClient(sshConnectionInfo);
    }

    /// <summary>
    /// Create an SFTP client with the given connection info.
    /// </summary>
    private SftpClient CreateSftpClient(SshConnectionInfo connection)
    {
        var authMethods = CreateAuthMethods(connection);

        var sshConnectionInfo = new Renci.SshNet.ConnectionInfo(
            connection.Host,
            connection.Port,
            connection.Username,
            authMethods.ToArray())
        {
            Timeout = connection.Timeout
        };

        return new SftpClient(sshConnectionInfo);
    }

    /// <summary>
    /// Create authentication methods based on connection credentials.
    /// </summary>
    private List<AuthenticationMethod> CreateAuthMethods(SshConnectionInfo connection)
    {
        var authMethods = new List<AuthenticationMethod>();

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
                _logger.LogWarning("Failed to load private key from {Path}: {Error}",
                    connection.PrivateKeyPath, ex.Message);
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
            throw new InvalidOperationException(
                $"No authentication method available for {connection.Username}@{connection.Host}");
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
