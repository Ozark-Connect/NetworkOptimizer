namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Turns an SSH failure into one short line the reader can act on.
/// <para>
/// SSH.NET names the socket or the protocol - "no identification string", "an established
/// connection was aborted" - which does not tell anyone whether the device is unreachable or
/// the credentials are wrong, and those are the only two things they can do anything about.
/// The original text still goes to the log; this is what the UI says.
/// </para>
/// </summary>
public static class SshFailureSummary
{
    /// <summary>
    /// Describe an SSH failure in one sentence.
    /// </summary>
    /// <param name="sshOutput">The combined output or error text from the SSH layer.</param>
    /// <param name="host">The device address, for naming what could not be reached.</param>
    public static string Describe(string? sshOutput, string host)
    {
        var text = (sshOutput ?? string.Empty).Trim();
        var where = string.IsNullOrWhiteSpace(host) ? "the device" : host;

        if (text.Length == 0)
            return $"No response from {where} over SSH.";

        // Already a written-for-the-reader message; passing it through keeps the one place that
        // knows why an agent site is dark from being paraphrased into something less useful.
        if (text.Contains("on-site agent", StringComparison.OrdinalIgnoreCase))
            return text;

        if (text.Contains("credentials not configured", StringComparison.OrdinalIgnoreCase))
            return "SSH credentials are not configured for this device.";

        if (text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No authentication method", StringComparison.OrdinalIgnoreCase)
            || text.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
            return $"SSH authentication was rejected by {where}. Check the SSH username and password.";

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return $"{where} did not answer SSH in time. Check connectivity or firewall rules.";

        if (text.Contains("Connection failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("could not reach", StringComparison.OrdinalIgnoreCase)
            || text.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return $"Could not reach {where} over SSH.";

        return FirstLine(text);
    }

    /// <summary>
    /// The first line of a message, capped. Command output can run to pages, and the UI shows
    /// this inline.
    /// </summary>
    private static string FirstLine(string text)
    {
        const int maxLength = 160;
        var line = text.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? text;
        return line.Length <= maxLength ? line : line[..maxLength] + "...";
    }
}
