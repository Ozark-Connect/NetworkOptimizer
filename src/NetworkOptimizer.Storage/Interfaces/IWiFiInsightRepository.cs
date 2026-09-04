namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Per-site persistence for what the operator has told the Wi-Fi Optimizer: acknowledged
/// issues, and radios kept on their channel. Ungated; the gated services in the web project
/// wrap it for the UI, and the optimizer reads it directly when it builds a score or a plan.
/// </summary>
public interface IWiFiInsightRepository
{
    /// <summary>Keys of every acknowledged issue on this site.</summary>
    Task<HashSet<string>> GetAcknowledgedIssueKeysAsync(CancellationToken cancellationToken = default);

    /// <summary>Acknowledges an issue; a second call for the same key is a no-op.</summary>
    Task AcknowledgeIssueAsync(string issueKey, CancellationToken cancellationToken = default);

    /// <summary>Restores an acknowledged issue to the active list; unknown keys are ignored.</summary>
    Task RestoreIssueAsync(string issueKey, CancellationToken cancellationToken = default);

    /// <summary>Every radio kept on its channel, as (AP MAC lowercase, band code).</summary>
    Task<List<(string ApMac, string Band)>> GetKeptRadiosAsync(CancellationToken cancellationToken = default);

    /// <summary>Keeps or releases one radio.</summary>
    Task SetKeptAsync(string apMac, string band, bool kept, CancellationToken cancellationToken = default);
}
