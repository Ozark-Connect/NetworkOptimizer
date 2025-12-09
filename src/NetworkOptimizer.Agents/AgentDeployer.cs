using Microsoft.Extensions.Logging;
using NetworkOptimizer.Agents.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Diagnostics;
using System.Text;

namespace NetworkOptimizer.Agents;

/// <summary>
/// Deploys monitoring agents to remote systems via SSH
/// </summary>
public class AgentDeployer
{
    private readonly ILogger<AgentDeployer> _logger;
    private readonly ScriptRenderer _scriptRenderer;

    public AgentDeployer(ILogger<AgentDeployer> logger, ScriptRenderer scriptRenderer)
    {
        _logger = logger;
        _scriptRenderer = scriptRenderer;
    }

    /// <summary>
    /// Deploys an agent to a remote system
    /// </summary>
    public async Task<DeploymentResult> DeployAgentAsync(AgentConfiguration config, CancellationToken cancellationToken = default)
    {
        var result = new DeploymentResult
        {
            AgentId = config.AgentId,
            DeviceName = config.DeviceName,
            AgentType = config.AgentType
        };

        try
        {
            _logger.LogInformation("Starting deployment of {AgentType} agent to {Device} ({Host})",
                config.AgentType, config.DeviceName, config.SshCredentials.Host);

            // Step 1: Validate credentials
            await AddStepAsync(result, "Validate Credentials", async () =>
            {
                if (!config.SshCredentials.IsValid())
                {
                    throw new InvalidOperationException("Invalid SSH credentials");
                }
                return "Credentials validated";
            });

            // Step 2: Test SSH connection
            await AddStepAsync(result, "Test SSH Connection", async () =>
            {
                await TestConnectionAsync(config.SshCredentials, cancellationToken);
                return $"Successfully connected to {config.SshCredentials.Host}";
            });

            // Step 3: Render templates
            Dictionary<string, string> renderedScripts = new();
            await AddStepAsync(result, "Render Templates", async () =>
            {
                var templates = _scriptRenderer.GetTemplatesForAgent(config.AgentType);
                foreach (var template in templates)
                {
                    var rendered = await _scriptRenderer.RenderTemplateAsync(template, config);
                    var scriptName = template.Replace(".template", "");
                    renderedScripts[scriptName] = rendered;
                }
                return $"Rendered {renderedScripts.Count} templates";
            });

            // Step 4: Deploy scripts based on agent type
            if (config.AgentType == AgentType.UDM || config.AgentType == AgentType.UCG)
            {
                await DeployUniFiAgentAsync(config, renderedScripts, result, cancellationToken);
            }
            else if (config.AgentType == AgentType.Linux)
            {
                await DeployLinuxAgentAsync(config, renderedScripts, result, cancellationToken);
            }

            // Step 5: Verify deployment
            await AddStepAsync(result, "Verify Deployment", async () =>
            {
                result.Verification = await VerifyDeploymentAsync(config, cancellationToken);
                if (!result.Verification.Passed)
                {
                    throw new InvalidOperationException("Deployment verification failed: " +
                        string.Join(", ", result.Verification.Messages));
                }
                return "Deployment verified successfully";
            });

            result.Success = true;
            result.Message = $"Successfully deployed {config.AgentType} agent to {config.DeviceName}";

            _logger.LogInformation("Successfully deployed agent {AgentId} to {Device}",
                config.AgentId, config.DeviceName);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Deployment failed: {ex.Message}";
            _logger.LogError(ex, "Failed to deploy agent {AgentId} to {Device}",
                config.AgentId, config.DeviceName);
        }

        return result;
    }

    /// <summary>
    /// Tests SSH connection to the remote host
    /// </summary>
    public async Task TestConnectionAsync(SshCredentials credentials, CancellationToken cancellationToken = default)
    {
        using var client = CreateSshClient(credentials);

        try
        {
            await Task.Run(() =>
            {
                client.Connect();
                _logger.LogDebug("SSH connection test successful to {Host}", credentials.Host);
            }, cancellationToken);
        }
        catch (SshAuthenticationException ex)
        {
            _logger.LogError("SSH authentication failed for {Host}: {Error}", credentials.Host, ex.Message);
            throw new InvalidOperationException($"SSH authentication failed: {ex.Message}", ex);
        }
        catch (SshConnectionException ex)
        {
            _logger.LogError("SSH connection failed for {Host}: {Error}", credentials.Host, ex.Message);
            throw new InvalidOperationException($"SSH connection failed: {ex.Message}", ex);
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
    /// Deploys agent to UniFi device (UDM/UCG)
    /// </summary>
    private async Task DeployUniFiAgentAsync(
        AgentConfiguration config,
        Dictionary<string, string> scripts,
        DeploymentResult result,
        CancellationToken cancellationToken)
    {
        using var client = CreateSshClient(config.SshCredentials);
        using var sftp = CreateSftpClient(config.SshCredentials);

        await Task.Run(() =>
        {
            client.Connect();
            sftp.Connect();

            try
            {
                // Create directories
                ExecuteCommand(client, "mkdir -p /data/on_boot.d");
                ExecuteCommand(client, "mkdir -p /data/network-optimizer");

                // Deploy boot script
                if (scripts.TryGetValue("udm-agent-boot.sh", out var bootScript))
                {
                    var bootPath = "/data/on_boot.d/99-network-optimizer.sh";
                    UploadScript(sftp, bootScript, bootPath);
                    ExecuteCommand(client, $"chmod +x {bootPath}");
                    result.DeployedFiles.Add(bootPath);
                    _logger.LogDebug("Deployed boot script to {Path}", bootPath);
                }

                // Deploy metrics collector
                if (scripts.TryGetValue("udm-metrics-collector.sh", out var metricsScript))
                {
                    var metricsPath = "/data/network-optimizer/metrics-collector.sh";
                    UploadScript(sftp, metricsScript, metricsPath);
                    ExecuteCommand(client, $"chmod +x {metricsPath}");
                    result.DeployedFiles.Add(metricsPath);
                    _logger.LogDebug("Deployed metrics collector to {Path}", metricsPath);
                }

                // Run installation script if present
                if (scripts.TryGetValue("install-udm.sh", out var installScript))
                {
                    var installPath = "/tmp/install-network-optimizer.sh";
                    UploadScript(sftp, installScript, installPath);
                    ExecuteCommand(client, $"chmod +x {installPath}");
                    var installOutput = ExecuteCommand(client, $"sh {installPath}");
                    _logger.LogDebug("Installation output: {Output}", installOutput);
                    ExecuteCommand(client, $"rm {installPath}");
                }

                _logger.LogInformation("Successfully deployed UniFi agent scripts");
            }
            finally
            {
                client.Disconnect();
                sftp.Disconnect();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deploys agent to Linux system
    /// </summary>
    private async Task DeployLinuxAgentAsync(
        AgentConfiguration config,
        Dictionary<string, string> scripts,
        DeploymentResult result,
        CancellationToken cancellationToken)
    {
        using var client = CreateSshClient(config.SshCredentials);
        using var sftp = CreateSftpClient(config.SshCredentials);

        await Task.Run(() =>
        {
            client.Connect();
            sftp.Connect();

            try
            {
                // Create directories
                ExecuteCommand(client, "mkdir -p /opt/network-optimizer");
                ExecuteCommand(client, "mkdir -p /var/log/network-optimizer");

                // Deploy agent script
                if (scripts.TryGetValue("linux-agent.sh", out var agentScript))
                {
                    var agentPath = "/opt/network-optimizer/agent.sh";
                    UploadScript(sftp, agentScript, agentPath);
                    ExecuteCommand(client, $"chmod +x {agentPath}");
                    result.DeployedFiles.Add(agentPath);
                    _logger.LogDebug("Deployed agent script to {Path}", agentPath);
                }

                // Deploy systemd service
                if (scripts.TryGetValue("linux-agent.service", out var serviceScript))
                {
                    var servicePath = "/etc/systemd/system/network-optimizer-agent.service";
                    UploadScript(sftp, serviceScript, servicePath);
                    result.DeployedFiles.Add(servicePath);
                    _logger.LogDebug("Deployed systemd service to {Path}", servicePath);

                    // Reload systemd and enable service
                    ExecuteCommand(client, "systemctl daemon-reload");
                    ExecuteCommand(client, "systemctl enable network-optimizer-agent.service");
                    ExecuteCommand(client, "systemctl restart network-optimizer-agent.service");
                }

                // Run installation script if present
                if (scripts.TryGetValue("install-linux.sh", out var installScript))
                {
                    var installPath = "/tmp/install-network-optimizer.sh";
                    UploadScript(sftp, installScript, installPath);
                    ExecuteCommand(client, $"chmod +x {installPath}");
                    var installOutput = ExecuteCommand(client, $"bash {installPath}");
                    _logger.LogDebug("Installation output: {Output}", installOutput);
                    ExecuteCommand(client, $"rm {installPath}");
                }

                _logger.LogInformation("Successfully deployed Linux agent");
            }
            finally
            {
                client.Disconnect();
                sftp.Disconnect();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Verifies that the deployment was successful
    /// </summary>
    private async Task<VerificationResult> VerifyDeploymentAsync(
        AgentConfiguration config,
        CancellationToken cancellationToken)
    {
        var verification = new VerificationResult();

        using var client = CreateSshClient(config.SshCredentials);

        await Task.Run(() =>
        {
            client.Connect();

            try
            {
                if (config.AgentType == AgentType.UDM || config.AgentType == AgentType.UCG)
                {
                    // Verify UniFi agent files
                    var bootScriptExists = FileExists(client, "/data/on_boot.d/99-network-optimizer.sh");
                    var metricsScriptExists = FileExists(client, "/data/network-optimizer/metrics-collector.sh");

                    if (bootScriptExists)
                        verification.VerifiedFiles.Add("/data/on_boot.d/99-network-optimizer.sh");
                    if (metricsScriptExists)
                        verification.VerifiedFiles.Add("/data/network-optimizer/metrics-collector.sh");

                    // Check if metrics collector is running
                    var processCheck = ExecuteCommand(client, "pgrep -f metrics-collector.sh");
                    verification.AgentRunning = !string.IsNullOrWhiteSpace(processCheck);

                    verification.Passed = bootScriptExists && metricsScriptExists;

                    if (!verification.Passed)
                    {
                        if (!bootScriptExists)
                            verification.Messages.Add("Boot script not found");
                        if (!metricsScriptExists)
                            verification.Messages.Add("Metrics collector script not found");
                    }
                }
                else if (config.AgentType == AgentType.Linux)
                {
                    // Verify Linux agent files
                    var agentScriptExists = FileExists(client, "/opt/network-optimizer/agent.sh");
                    var serviceExists = FileExists(client, "/etc/systemd/system/network-optimizer-agent.service");

                    if (agentScriptExists)
                        verification.VerifiedFiles.Add("/opt/network-optimizer/agent.sh");
                    if (serviceExists)
                        verification.VerifiedFiles.Add("/etc/systemd/system/network-optimizer-agent.service");

                    // Check service status
                    var serviceStatus = ExecuteCommand(client, "systemctl is-active network-optimizer-agent.service");
                    verification.ServiceStatus = serviceStatus.Trim();
                    verification.AgentRunning = verification.ServiceStatus == "active";

                    verification.Passed = agentScriptExists && serviceExists && verification.AgentRunning;

                    if (!verification.Passed)
                    {
                        if (!agentScriptExists)
                            verification.Messages.Add("Agent script not found");
                        if (!serviceExists)
                            verification.Messages.Add("Systemd service not found");
                        if (!verification.AgentRunning)
                            verification.Messages.Add($"Service not running (status: {verification.ServiceStatus})");
                    }
                }
            }
            finally
            {
                client.Disconnect();
            }
        }, cancellationToken);

        return verification;
    }

    /// <summary>
    /// Creates an SSH client with the given credentials
    /// </summary>
    private SshClient CreateSshClient(SshCredentials credentials)
    {
        var authMethods = new List<AuthenticationMethod>();

        if (credentials.GetAuthenticationType() == AuthenticationType.PrivateKey)
        {
            var keyFile = credentials.PrivateKeyPassphrase != null
                ? new PrivateKeyFile(credentials.PrivateKeyPath!, credentials.PrivateKeyPassphrase)
                : new PrivateKeyFile(credentials.PrivateKeyPath!);

            authMethods.Add(new PrivateKeyAuthenticationMethod(credentials.Username, keyFile));
        }
        else if (credentials.GetAuthenticationType() == AuthenticationType.Password)
        {
            authMethods.Add(new PasswordAuthenticationMethod(credentials.Username, credentials.Password!));
        }

        var connectionInfo = new ConnectionInfo(
            credentials.Host,
            credentials.Port,
            credentials.Username,
            authMethods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(credentials.TimeoutSeconds)
        };

        return new SshClient(connectionInfo);
    }

    /// <summary>
    /// Creates an SFTP client with the given credentials
    /// </summary>
    private SftpClient CreateSftpClient(SshCredentials credentials)
    {
        var authMethods = new List<AuthenticationMethod>();

        if (credentials.GetAuthenticationType() == AuthenticationType.PrivateKey)
        {
            var keyFile = credentials.PrivateKeyPassphrase != null
                ? new PrivateKeyFile(credentials.PrivateKeyPath!, credentials.PrivateKeyPassphrase)
                : new PrivateKeyFile(credentials.PrivateKeyPath!);

            authMethods.Add(new PrivateKeyAuthenticationMethod(credentials.Username, keyFile));
        }
        else if (credentials.GetAuthenticationType() == AuthenticationType.Password)
        {
            authMethods.Add(new PasswordAuthenticationMethod(credentials.Username, credentials.Password!));
        }

        var connectionInfo = new ConnectionInfo(
            credentials.Host,
            credentials.Port,
            credentials.Username,
            authMethods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(credentials.TimeoutSeconds)
        };

        return new SftpClient(connectionInfo);
    }

    /// <summary>
    /// Executes a command on the remote system
    /// </summary>
    private string ExecuteCommand(SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        var result = cmd.Execute();

        if (cmd.ExitStatus != 0 && !string.IsNullOrEmpty(cmd.Error))
        {
            _logger.LogWarning("Command '{Command}' returned non-zero exit code {ExitCode}: {Error}",
                command, cmd.ExitStatus, cmd.Error);
        }

        return result;
    }

    /// <summary>
    /// Uploads a script to the remote system
    /// </summary>
    private void UploadScript(SftpClient sftp, string content, string remotePath)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        sftp.UploadFile(stream, remotePath, true);
    }

    /// <summary>
    /// Checks if a file exists on the remote system
    /// </summary>
    private bool FileExists(SshClient client, string path)
    {
        var result = ExecuteCommand(client, $"test -f {path} && echo 'exists' || echo 'not found'");
        return result.Trim() == "exists";
    }

    /// <summary>
    /// Helper to add a deployment step with error handling
    /// </summary>
    private async Task AddStepAsync(DeploymentResult result, string stepName, Func<Task<string>> action)
    {
        var step = new DeploymentStep { Name = stepName };
        var sw = Stopwatch.StartNew();

        try
        {
            step.Message = await action();
            step.Success = true;
            _logger.LogDebug("Step '{Step}' completed: {Message}", stepName, step.Message);
        }
        catch (Exception ex)
        {
            step.Success = false;
            step.Message = ex.Message;
            _logger.LogError(ex, "Step '{Step}' failed", stepName);
            throw;
        }
        finally
        {
            sw.Stop();
            step.DurationMs = sw.ElapsedMilliseconds;
            result.Steps.Add(step);
        }
    }
}
