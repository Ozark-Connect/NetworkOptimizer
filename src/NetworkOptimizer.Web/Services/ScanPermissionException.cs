namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Thrown by <see cref="WiFiOptimizerService.RunQuickScansAsync"/> when the UniFi account lacks the
/// permission needed to trigger an RF spectrum scan. Carries a user-facing message the UI can show
/// directly, keeping the underlying UniFi exception type out of the Blazor components.
/// </summary>
public class ScanPermissionException : Exception
{
    public ScanPermissionException(string message)
        : base(message)
    {
    }
}
