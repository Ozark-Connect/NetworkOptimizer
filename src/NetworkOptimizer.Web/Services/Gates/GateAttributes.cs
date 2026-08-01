using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Gates;

/// <summary>
/// Declares that a mutating service method requires a global role (design doc 06, gate 9). Enforced by
/// the <see cref="MethodSecurityInterceptor"/> on the DI-registered interface, and required on every
/// mutating-service method by architecture test A2.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class RequireRoleAttribute : Attribute
{
    public RequireRoleAttribute(string role) => Role = role;

    /// <summary>Required global role (see <see cref="Roles"/>).</summary>
    public string Role { get; }
}

/// <summary>
/// Declares that a mutating service method requires at least a site role on the site identified by the
/// <see cref="SiteSlugAttribute"/>-marked parameter (design doc 06, gate 9).
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class RequireSiteRoleAttribute : Attribute
{
    public RequireSiteRoleAttribute(SiteRole minimum) => Minimum = minimum;

    /// <summary>Least-privileged site role that satisfies the gate.</summary>
    public SiteRole Minimum { get; }
}

/// <summary>
/// Marks the parameter carrying the site slug that a <see cref="RequireSiteRoleAttribute"/> gate (and
/// the audit envelope) authorizes/records against.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class SiteSlugAttribute : Attribute
{
}

/// <summary>
/// Declares that a mutating service method emits an audit event (design doc 06, gate 9). The
/// interceptor writes the envelope (actor/site/outcome); the method enriches detail via
/// <see cref="IAuditContext"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditActionAttribute : Attribute
{
    public AuditActionAttribute(string action) => Action = action;

    /// <summary>Dotted audit action verb (see <see cref="AuditActions"/>).</summary>
    public string Action { get; }

    /// <summary>Audit category (defaults to <see cref="AuditCategories.Action"/>).</summary>
    public string Category { get; set; } = AuditCategories.Action;

    /// <summary>Optional target type recorded on the event (e.g. "wan", "device").</summary>
    public string? TargetType { get; set; }

    /// <summary>
    /// True for an action that belongs to the instance rather than to a site (licensing, the auth
    /// policy). The audit envelope otherwise records the site the caller was working in, which is
    /// right for the site-scoped majority but would file an instance-wide change under whichever
    /// site happened to be open. Set it per method rather than per category: a category can hold
    /// both kinds, and licensing does - activating a key is instance-wide, assigning one to a site
    /// is not.
    /// </summary>
    public bool InstanceScoped { get; set; }
}
