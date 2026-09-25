using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// How an alert's severity renders in an <c>.alert-item</c> row, shared by the Alerts page and the
/// Dashboard's Active Alerts card so both read the same.
/// </summary>
public static class AlertSeverityDisplay
{
    /// <summary>
    /// The <c>.alert-item</c> modifier class (border and icon color) for a severity. Never the bare
    /// <c>alert-warning</c> / <c>alert-info</c>: those are the global banner classes and restyle the row.
    /// </summary>
    public static string GetItemClass(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "alert-item-critical",
        AlertSeverity.Error => "alert-item-critical",
        AlertSeverity.Warning => "alert-item-warning",
        _ => "alert-item-info"
    };

    /// <summary>The inline SVG icon for a severity, rendered inside <c>.alert-icon</c>.</summary>
    public static string GetIcon(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical or AlertSeverity.Error =>
            "<svg viewBox=\"0 0 24 24\" fill=\"currentColor\" width=\"24\" height=\"24\"><path d=\"M12 2L3 7v6c0 5.25 3.85 10.15 9 11 5.15-.85 9-5.75 9-11V7l-9-5zm-1 14h2v2h-2v-2zm0-8h2v6h-2V8z\" opacity=\"0.2\"/><path d=\"M12 2L3 7v6c0 5.25 3.85 10.15 9 11 5.15-.85 9-5.75 9-11V7l-9-5zm0 2.18l7 3.89v5.43c0 4.14-3.01 8.05-7 8.88-3.99-.83-7-4.74-7-8.88V8.07l7-3.89zM11 8h2v6h-2V8zm0 8h2v2h-2v-2z\"/></svg>",
        AlertSeverity.Warning =>
            "<svg viewBox=\"0 0 24 24\" fill=\"currentColor\" width=\"24\" height=\"24\"><path d=\"M1 21h22L12 2 1 21z\" opacity=\"0.2\"/><path d=\"M12 5.99L19.53 19H4.47L12 5.99M12 2L1 21h22L12 2zm1 14h-2v2h2v-2zm0-6h-2v4h2v-4z\"/></svg>",
        _ =>
            "<svg viewBox=\"0 0 24 24\" fill=\"currentColor\" width=\"24\" height=\"24\"><circle cx=\"12\" cy=\"12\" r=\"10\" opacity=\"0.2\"/><path d=\"M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z\"/><rect x=\"11\" y=\"11\" width=\"2\" height=\"6\"/><rect x=\"11\" y=\"7\" width=\"2\" height=\"2\"/></svg>"
    };
}
