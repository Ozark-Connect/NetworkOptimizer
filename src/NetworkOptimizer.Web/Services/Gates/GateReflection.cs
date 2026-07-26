using System.Reflection;

namespace NetworkOptimizer.Web.Services.Gates;

/// <summary>
/// Shared reflection helpers for the declarative gate, used by both the runtime interceptor and the
/// architecture tests so they agree on what "a gated member" means.
/// </summary>
public static class GateReflection
{
    /// <summary>The property a get/set accessor belongs to, or null when the method is not an accessor.</summary>
    public static PropertyInfo? DeclaringProperty(MethodInfo method)
    {
        if (!method.IsSpecialName)
            return null;

        var name = method.Name;
        if (!name.StartsWith("get_", StringComparison.Ordinal) && !name.StartsWith("set_", StringComparison.Ordinal))
            return null;

        return method.DeclaringType?.GetProperty(name[4..]);
    }

    /// <summary>True when the member (or the property owning the accessor) declares a role gate.</summary>
    public static bool HasGate(MethodInfo method)
    {
        if (method.GetCustomAttribute<RequireRoleAttribute>() is not null
            || method.GetCustomAttribute<RequireSiteRoleAttribute>() is not null)
        {
            return true;
        }

        var property = DeclaringProperty(method);
        return property is not null
            && (property.GetCustomAttribute<RequireRoleAttribute>() is not null
                || property.GetCustomAttribute<RequireSiteRoleAttribute>() is not null);
    }
}
