using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The gated write path for system settings (design doc 06, gate 9). <see cref="ISystemSettingsService"/>
/// stays ungated on purpose: it is a low-level key/value store read from pollers, schedulers, and
/// collectors on every cycle, and putting reads behind the interceptor would demand a caller context on
/// every background tick. The UI writes settings through this interface instead, so every settings
/// change is Admin-gated and lands in the audit log as <c>settings.changed</c>.
/// </summary>
[MutatingService]
public interface ISystemSettingsAdmin
{
    /// <summary>Writes a setting in the current site's database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "setting")]
    Task SetAsync(string key, string? value);

    /// <summary>Writes an integer setting in the current site's database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "setting")]
    Task SetIntAsync(string key, int value);

    /// <summary>Writes an instance-wide setting in the main database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "setting")]
    Task SetGlobalAsync(string key, string? value);

    /// <summary>Writes an instance-wide integer setting in the main database.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "setting")]
    Task SetGlobalIntAsync(string key, int value);

    /// <summary>Saves the iperf3 test preferences as one change.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "iperf3_settings")]
    Task SaveIperf3SettingsAsync(Iperf3Settings settings);
}
