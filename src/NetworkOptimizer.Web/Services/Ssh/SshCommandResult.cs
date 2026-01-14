namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Result of an SSH command execution.
/// </summary>
public class SshCommandResult
{
    /// <summary>Whether the command completed successfully (exit code 0)</summary>
    public bool Success { get; set; }

    /// <summary>Exit code of the command</summary>
    public int ExitCode { get; set; }

    /// <summary>Standard output from the command</summary>
    public string Output { get; set; } = "";

    /// <summary>Standard error from the command</summary>
    public string Error { get; set; } = "";

    /// <summary>Combined output (stdout + stderr)</summary>
    public string CombinedOutput => string.IsNullOrEmpty(Error) ? Output : $"{Output}\n{Error}".Trim();
}
