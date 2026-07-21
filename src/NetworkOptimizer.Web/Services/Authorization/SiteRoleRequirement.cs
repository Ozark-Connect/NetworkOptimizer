using Microsoft.AspNetCore.Authorization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Resource-based requirement: the user must have at least <see cref="Minimum"/> effective role on the
/// site slug supplied as the authorization resource (design doc 04). Handled by
/// <see cref="SiteRoleHandler"/>, which is the single place effective-role computation lives.
/// </summary>
public sealed class SiteRoleRequirement : IAuthorizationRequirement
{
    public SiteRoleRequirement(SiteRole minimum) => Minimum = minimum;

    /// <summary>The least-privileged site role that satisfies this requirement.</summary>
    public SiteRole Minimum { get; }
}
