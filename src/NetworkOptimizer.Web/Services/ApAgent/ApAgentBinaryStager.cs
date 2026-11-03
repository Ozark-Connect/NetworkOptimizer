using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Copies a new agent build to <see cref="ApAgentPaths.StagedBinaryPath"/> and verifies it, while the
/// old agent keeps running. It never touches the live binary or the process: the deploy stops the
/// agent and promotes the staged file only after this succeeds, so an interrupted copy (a server
/// restart mid-transfer) leaves the AP on its old, working agent.
/// </summary>
internal static class ApAgentBinaryStager
{
    /// <summary>Runs one shell command on the AP.</summary>
    internal delegate Task<(bool success, string output)> RunCommand(string command, CancellationToken ct);

    /// <summary>What staging found.</summary>
    /// <param name="Method">The transfer that uploaded and verified, or null when none did.</param>
    /// <param name="Failures">Why each attempted transfer failed, by method name.</param>
    /// <param name="SetupError">A failure outside the transfers themselves, which ends staging at once.</param>
    internal sealed record Result(string? Method, IReadOnlyDictionary<string, string> Failures, string? SetupError)
    {
        public bool Success => Method != null;
    }

    /// <summary>Attempts each transfer in order until one uploads a file whose md5 matches.</summary>
    public static async Task<Result> StageAsync(
        RunCommand run,
        IReadOnlyList<IApAgentBinaryTransfer> chain,
        SshConnectionInfo connection,
        string localPath,
        string localMd5,
        ILogger logger,
        CancellationToken ct)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var prepare = await run(ApAgentScripts.PrepareStagingCommand(), ct);
        if (!prepare.success)
            return new Result(null, failures, $"Could not create the install directory: {prepare.output}");

        foreach (var transfer in chain)
        {
            string? error = null;
            try
            {
                await transfer.UploadAsync(connection, localPath, ApAgentPaths.StagedBinaryPath, ct);

                var check = await run(ApAgentScripts.StagedMd5Command(), ct);
                if (!check.success || !string.Equals(check.output.Trim(), localMd5, StringComparison.OrdinalIgnoreCase))
                    error = "The copied file did not match the original (md5 mismatch).";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (error != null)
            {
                failures[transfer.Name] = error;
                logger.LogDebug("AP Agent transfer to {Host} over {Method} failed: {Error}", connection.Host, transfer.Name, error);
                continue;
            }

            var chmod = await run(ApAgentScripts.MakeStagedExecutableCommand(), ct);
            if (!chmod.success)
                return new Result(null, failures, $"Could not make the agent executable: {chmod.output}");

            return new Result(transfer.Name, failures, null);
        }

        return new Result(null, failures, null);
    }
}
