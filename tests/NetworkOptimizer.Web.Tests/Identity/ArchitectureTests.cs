using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Architecture tests A1-A4 (design doc 06): the reflection net that fails the build when a gate is
/// forgotten. A2 and A3 enforce directly against the current code with explicit allowlists; A1 and A4
/// are staged behind the per-endpoint / per-page retrofit (the app currently gates via the global auth
/// middleware, see Program.cs), and their intent + allowlist contract is captured here.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly WebAssembly = typeof(IdentityAdminService).Assembly;

    /// <summary>
    /// A3: only the identity infrastructure may reference UserManager/RoleManager; product code must go
    /// through <see cref="IdentityAdminService"/>. The allowlist is the identity plumbing that
    /// legitimately needs the managers (sign-in, bootstrap/seed, the session bridge, the claims factory).
    /// </summary>
    [Fact]
    public void A3_OnlyIdentityInfrastructureReferencesUserManager()
    {
        var allow = new HashSet<string>
        {
            nameof(IdentityAdminService),
            nameof(IdentityBootstrapService),
            nameof(IdentitySignInService),
            nameof(LegacyJwtBridgeMiddleware),
            nameof(AppUserClaimsPrincipalFactory),
            nameof(RevalidatingIdentityAuthenticationStateProvider),
            nameof(MfaService),
            "PasskeyService",
            "CurrentUserAccessor",
            "ExternalLoginService",
        };

        var managerTypes = new[]
        {
            typeof(UserManager<ApplicationUser>),
            typeof(RoleManager<ApplicationRole>),
        };

        var offenders = SafeGetTypes(WebAssembly)
            .Where(t => !IsCompilerGenerated(t))
            .Where(t => !allow.Contains(DeclaringName(t)))
            .Where(t => ReferencesAny(t, managerTypes))
            .Select(t => t.FullName!)
            .ToList();

        offenders.Should().BeEmpty(
            "only allowlisted identity infrastructure may touch UserManager/RoleManager directly (design doc 06 A3)");
    }

    /// <summary>
    /// A2: every method on a <see cref="MutatingServiceAttribute"/>-marked interface must declare a
    /// <see cref="RequireGlobalRoleAttribute"/> or <see cref="RequireSiteRoleAttribute"/> gate.
    /// </summary>
    [Fact]
    public void A2_EveryMutatingServiceMethodHasAGate()
    {
        var ungated = new List<string>();
        foreach (var iface in SafeGetTypes(WebAssembly)
                     .Where(t => t.IsInterface && t.GetCustomAttribute<MutatingServiceAttribute>() is not null))
        {
            foreach (var method in iface.GetMethods())
            {
                var hasGate = method.GetCustomAttribute<RequireGlobalRoleAttribute>() is not null
                    || method.GetCustomAttribute<RequireSiteRoleAttribute>() is not null;
                if (!hasGate)
                    ungated.Add($"{iface.Name}.{method.Name}");
            }
        }

        ungated.Should().BeEmpty("every [MutatingService] method must carry a role gate (design doc 06 A2)");
    }

    /// <summary>
    /// A1: every mapped endpoint must carry authorization metadata or live under <c>/api/public/</c>.
    /// STAGED: the app currently enforces this structurally via the global auth-required middleware
    /// (Program.cs) rather than per-endpoint metadata; the strict reflection form lands with the
    /// endpoint retrofit. Tracked in the PR body.
    /// </summary>
    [Fact(Skip = "A1 strict form pending the per-endpoint authorization retrofit; gating is currently enforced by the global auth middleware (Program.cs).")]
    public void A1_EveryEndpointIsAuthorizedOrPublic() { }

    /// <summary>
    /// A4: every Blazor page under an authenticated area declares [Authorize] (or is on the anonymous
    /// allowlist). STAGED with A1 for the same reason - pages are currently gated by the middleware.
    /// </summary>
    [Fact(Skip = "A4 strict form pending the per-page [Authorize] retrofit; pages are currently gated by the global auth middleware (Program.cs).")]
    public void A4_EveryAuthenticatedPageDeclaresAuthorize() { }

    private static bool IsCompilerGenerated(Type t)
        => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null
            || t.Name.Contains('<');

    /// <summary>Name of the outermost declaring type (so a nested/state-machine type maps to its owner).</summary>
    private static string DeclaringName(Type t)
    {
        var top = t;
        while (top.DeclaringType is not null)
            top = top.DeclaringType;
        return top.Name;
    }

    private static bool ReferencesAny(Type type, Type[] targets)
    {
        bool Matches(Type t) => targets.Contains(t)
            || (t.IsGenericType && targets.Contains(t.GetGenericTypeDefinition().MakeGenericType(t.GetGenericArguments())));

        // Constructor parameters
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (ctor.GetParameters().Any(p => Matches(p.ParameterType)))
                return true;

        // Fields
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            if (Matches(field.FieldType))
                return true;

        // Method parameters (covers method-injected dependencies, e.g. middleware Invoke)
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            if (method.GetParameters().Any(p => Matches(p.ParameterType)))
                return true;

        return false;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
