namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Reasons a step settles as <c>AbortedSku</c> without anything failing. A model abort after a
/// failed build carries its own "Dropped after ..." reason instead, so the page can tell the two apart.
/// </summary>
public static class RolloutSkipReasons
{
    /// <summary>The device stayed offline through its turn and was never commanded.</summary>
    public const string OfflineAtTurn = "Offline when its turn came";

    /// <summary>The model's canary was offline at its turn, so its held peers do not run in this plan.</summary>
    public const string NoOnlineCanary = "No online canary for this model";

    /// <summary>The model's canary settled without passing, so its held peers do not run in this plan.</summary>
    public const string NoCanaryPassed = "No canary passed for this model";

    /// <summary>Commanding it would have left the site with no access point online.</summary>
    public const string LastApOnline = "Skipped: it was the last access point still online";

    /// <summary>
    /// The device already ran the target at its turn. Settles as <c>SkippedExcluded</c>, not
    /// <c>AbortedSku</c>, and a canary skipped this way still releases its held peers.
    /// </summary>
    public const string AlreadyOnTarget = "Nothing newer to install on the planned channel.";

    /// <summary>Whether a step's error is one of the <c>AbortedSku</c> skips rather than a model abort.</summary>
    public static bool IsSkip(string? error) =>
        error is OfflineAtTurn or NoOnlineCanary or NoCanaryPassed or LastApOnline;
}
