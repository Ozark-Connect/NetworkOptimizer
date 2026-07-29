namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Break-glass recovery: a process-start env override (<c>NETOPT_RECOVERY=1</c>) that enables local
/// Admin login for that boot even when local login is disabled or federation is misconfigured, so a
/// broken IdP config can't permanently brick a self-hosted box (design doc 02). Its use is a loud
/// audit event and surfaces a UI banner. Evaluated once at process start - toggling it requires a
/// restart, which is the intended "recovery boot" semantics.
/// </summary>
public static class BreakGlass
{
    /// <summary>True when the process was started in recovery mode.</summary>
    public static bool IsRecoveryMode { get; } = Evaluate();

    private static bool Evaluate()
    {
        var value = Environment.GetEnvironmentVariable("NETOPT_RECOVERY");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
