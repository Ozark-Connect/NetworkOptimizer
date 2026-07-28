using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Endpoints;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Architecture tests A1-A4 (design doc 06): the reflection net that fails the build when a gate is
/// forgotten. Each has an explicit allowlist, so opting a surface out of a gate is a reviewed diff
/// rather than an omission nobody notices.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly WebAssembly = typeof(IdentityAdminService).Assembly;

    /// <summary>
    /// A1: every mapped endpoint either carries authorization metadata (a policy, or an explicit
    /// <see cref="IAllowAnonymous"/> decision from the allowlist below) or lives under
    /// <c>/api/public/</c>. <see cref="ApiEndpoints.MapAll"/> is the single registration point the app
    /// itself uses, so an endpoint that is reachable in production is covered here.
    /// </summary>
    [Fact]
    public void A1_EveryEndpointIsAuthorizedOrPublic()
    {
        // Routes that are deliberately reachable without a session. Sign-in surfaces cannot require
        // authentication (nobody is signed in yet); health is polled by container orchestration.
        var anonymousAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/api/health",
            "/api/auth/login",
            "/api/auth/2fa",
            "/api/auth/logout",
            "/api/passkey/request-options",
            "/api/passkey/assert",
            "/login/external/{scheme}",
            "/login/external-callback",
            "/login/saml/{scheme}",
            "/saml/{scheme}/metadata",
            "/saml/{scheme}/acs",
        };

        var ungated = new List<string>();
        foreach (var endpoint in MapAllEndpoints().OfType<RouteEndpoint>())
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
            if (route.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase))
                continue;

            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            if (allowsAnonymous)
            {
                if (!anonymousAllowlist.Contains(route))
                    ungated.Add($"{route} (anonymous but not on the allowlist)");
                continue;
            }

            if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
                ungated.Add($"{route} (no authorization metadata)");
        }

        ungated.Should().BeEmpty(
            "every endpoint must carry authorization metadata, be an allowlisted anonymous route, or "
            + "live under /api/public/ (design doc 06 A1)");
    }

    /// <summary>
    /// A2: every method on a <see cref="MutatingServiceAttribute"/>-marked interface must declare a
    /// <see cref="RequireRoleAttribute"/> or <see cref="RequireSiteRoleAttribute"/> gate. Event
    /// add/remove accessors are subscription plumbing rather than an action, so they are exempt; the
    /// interceptor still refuses them without a caller context.
    /// </summary>
    [Fact]
    public void A2_EveryMutatingServiceMethodHasAGate()
    {
        var ungated = new List<string>();
        foreach (var iface in MutatingInterfaces())
        {
            foreach (var method in iface.GetMethods())
            {
                if (IsEventAccessor(method))
                    continue;
                if (!GateReflection.HasGate(method))
                    ungated.Add($"{iface.Name}.{method.Name}");
            }
        }

        ungated.Should().BeEmpty("every [MutatingService] method must carry a role gate (design doc 06 A2)");
    }

    /// <summary>
    /// A2 (second half): a gate is only enforced if the interface is actually proxied, so every
    /// <see cref="MutatingServiceAttribute"/> interface must be registered through one of the
    /// <c>AddMutatingService</c> overloads in the composition root.
    /// </summary>
    [Fact]
    public void A2_EveryMutatingServiceIsRegisteredThroughTheGate()
    {
        var programSource = CompositionRootSource();
        var unregistered = MutatingInterfaces()
            .Where(i => !programSource.Contains($"AddMutatingService<{i.Name}", StringComparison.Ordinal)
                && !programSource.Contains($"AddMutatingService<{i.Name},", StringComparison.Ordinal)
                && !programSource.Contains($".{i.Name}>", StringComparison.Ordinal))
            .Select(i => i.Name)
            .ToList();

        unregistered.Should().BeEmpty(
            "a [MutatingService] interface is only gated once it is registered via AddMutatingService (design doc 06 A2)");
    }

    /// <summary>
    /// A2 (third part): where a mutating service is reachable ONLY through its gated interface,
    /// nothing may take the implementation class as a constructor dependency - that both skips the
    /// gate and fails at runtime, because the implementation is constructed inside the proxy factory
    /// rather than registered as its own service. Implementations Program.cs still registers directly
    /// (because a background/system path legitimately uses them) are exempt, as is the per-site
    /// registry that owns its instances and hands them to the gated registration as proxy targets.
    /// </summary>
    [Fact]
    public void A2_NothingDependsOnAMutatingServiceImplementation()
    {
        var allow = new HashSet<string>
        {
            "SpeedTestServiceRegistry", // owns the per-site instances the gated registrations proxy
        };

        var programSource = CompositionRootSource();
        var implementations = SafeGetTypes(WebAssembly)
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetInterfaces().Any(i => i.GetCustomAttribute<MutatingServiceAttribute>() is not null))
            .Where(t => !programSource.Contains($"AddScoped<{t.Name}>", StringComparison.Ordinal)
                && !programSource.Contains($"AddSingleton<{t.Name}>", StringComparison.Ordinal))
            .ToHashSet();

        var offenders = new List<string>();
        foreach (var type in SafeGetTypes(WebAssembly).Where(t => t.IsClass && !IsCompilerGenerated(t)))
        {
            if (implementations.Contains(type) || allow.Contains(DeclaringName(type)))
                continue;

            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters().Where(p => implementations.Contains(p.ParameterType)))
                    offenders.Add($"{type.Name}({parameter.ParameterType.Name} {parameter.Name})");
            }
        }

        offenders.Should().BeEmpty(
            "inject the gated interface, not the implementation class (design doc 06 A2)");
    }

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
    /// A4: every routable Blazor page declares an <see cref="AuthorizeAttribute"/>, except the sign-in
    /// pages, which must render while anonymous. The page policies succeed on an install with
    /// authentication disabled, so this gate costs those installs nothing.
    /// </summary>
    [Fact]
    public void A4_EveryAuthenticatedPageDeclaresAuthorize()
    {
        // Error is the exception handler's target: it must render for an anonymous caller, or an
        // unhandled exception turns into a silent redirect to the sign-in page.
        var anonymousPages = new HashSet<string> { "Login", "Login2fa", "Error" };

        var ungated = SafeGetTypes(WebAssembly)
            .Where(t => typeof(IComponent).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes<RouteAttribute>().Any())
            .Where(t => !anonymousPages.Contains(t.Name))
            .Where(t => t.GetCustomAttribute<AuthorizeAttribute>() is null)
            .Select(t => t.Name)
            .ToList();

        ungated.Should().BeEmpty(
            "every routable page outside the sign-in allowlist must declare [Authorize] (design doc 06 A4)");
    }

    private static IEnumerable<Endpoint> MapAllEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        // Mapping builds each handler's parameter binding, which asks the container whether a
        // parameter type is an injected service. The test only reads route metadata, so the container
        // is swapped for one that answers "yes" instead of duplicating hundreds of registrations.
        builder.Host.UseServiceProviderFactory(new StubServiceProviderFactory());
        var app = builder.Build();
        ApiEndpoints.MapAll(app);
        return ((IEndpointRouteBuilder)app).DataSources.SelectMany(ds => ds.Endpoints);
    }

    private sealed class StubServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
    {
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;

        public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
            => new StubServiceProvider(containerBuilder.BuildServiceProvider());
    }

    /// <summary>
    /// The real container except for <see cref="IServiceProviderIsService"/>, which the container
    /// implements itself and therefore cannot be replaced by a registration (see MapAllEndpoints).
    /// </summary>
    private sealed class StubServiceProvider : IServiceProvider, ISupportRequiredService
    {
        private static readonly EverythingIsAService IsService = new();
        private readonly IServiceProvider _inner;

        public StubServiceProvider(IServiceProvider inner) => _inner = inner;

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceProviderIsService) ? IsService : _inner.GetService(serviceType);

        public object GetRequiredService(Type serviceType)
            => GetService(serviceType) ?? throw new InvalidOperationException($"No service for {serviceType}.");
    }

    /// <summary>Treats every non-primitive parameter type as an injected service (see MapAllEndpoints).</summary>
    private sealed class EverythingIsAService : IServiceProviderIsService
    {
        public bool IsService(Type serviceType)
            => !serviceType.IsPrimitive
                && serviceType != typeof(string)
                && !serviceType.IsEnum
                && Nullable.GetUnderlyingType(serviceType) is null
                && serviceType != typeof(DateTime)
                && serviceType != typeof(Guid)
                && serviceType != typeof(decimal);
    }

    /// <summary>
    /// Every file that registers services at startup. Program.cs is the composition root, but the
    /// identity registrations were long enough to live in an extension method Program.cs calls; that is
    /// a file split, not a second container, so the registration checks read both.
    /// </summary>
    private static string CompositionRootSource()
        => ReadRepoFile("src/NetworkOptimizer.Web/Program.cs")
            + ReadRepoFile("src/NetworkOptimizer.Web/Services/Identity/IdentityRegistration.cs");

    private static IEnumerable<Type> MutatingInterfaces()
        => SafeGetTypes(WebAssembly)
            .Where(t => t.IsInterface && t.GetCustomAttribute<MutatingServiceAttribute>() is not null);

    private static bool IsEventAccessor(MethodInfo method)
        => method.IsSpecialName
            && (method.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal));

    /// <summary>Reads a repo-relative source file by walking up from the test binary to the repo root.</summary>
    private static string ReadRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, relativePath)))
            dir = dir.Parent;

        dir.Should().NotBeNull($"the repo root containing {relativePath} should be above the test binary");
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }

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
