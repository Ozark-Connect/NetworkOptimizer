using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// DI wiring for ASP.NET Core Identity on the dedicated main-DB <see cref="AuthDbContext"/>.
/// </summary>
/// <remarks>
/// This method is intentionally additive: it registers the user/role stores, managers, password
/// hasher, and the bootstrap seeder, but does NOT change the authentication pipeline (cookie schemes,
/// default scheme, sign-in) - that swap is applied separately during the cutover so the identity
/// data model and admin seed can be verified before the live auth artifact changes (design doc 02).
/// </remarks>
public static class IdentityRegistration
{
    /// <summary>
    /// Registers the Identity core (UserManager/RoleManager stores on <see cref="AuthDbContext"/>),
    /// the 600k-iteration password hasher with the legacy-format fallback, and the startup bootstrap
    /// seeder. Both a scoped context (for the managers) and a singleton factory (for background/startup
    /// work) are registered against the MAIN database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="mainDbPath">Absolute path to the main SQLite database (registry + default site).</param>
    public static IServiceCollection AddNetOptIdentityCore(this IServiceCollection services, string mainDbPath)
    {
        // Scoped context for the Identity managers. Always the MAIN db, with a dedicated
        // migrations-history table so it coexists with the site-routed product context.
        services.AddDbContext<AuthDbContext>(options =>
            options.UseSqlite(
                $"Data Source={mainDbPath}",
                x => x.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable)));

        // Singleton factory for the audit writer, startup migration, and break-glass - services that
        // can't take a scoped context. Owns its own options instance (see AuthDbContextFactory).
        var factoryOptions = new DbContextOptionsBuilder<AuthDbContext>()
            .UseSqlite(
                $"Data Source={mainDbPath}",
                x => x.MigrationsHistoryTable(AuthDbContext.MigrationsHistoryTable))
            .Options;
        services.AddSingleton<IDbContextFactory<AuthDbContext>>(new AuthDbContextFactory(factoryOptions));

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Match the app's historical local-account policy (>= 8 chars, letter + digit).
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Email is optional on appliance installs; usernames are the primary identifier.
                options.User.RequireUniqueEmail = false;
                options.SignIn.RequireConfirmedAccount = false;

                // Identity's default lockout (5 fails / 5 min), tunable later via Settings.
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AuthDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        // Keep new hashes at the app's historical strength (framework default 100k is weaker).
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 600_000);

        // One-release belt-and-suspenders: verify any still-legacy-format hash and flag it for rehash.
        services.AddScoped<IPasswordHasher<ApplicationUser>, LegacyFallbackPasswordHasher>();

        services.AddScoped<IIdentityBootstrapService, IdentityBootstrapService>();
        services.AddScoped<IIdentitySignInService, IdentitySignInService>();
        services.AddScoped<IAuthPolicyOptions, AuthPolicyOptions>();

        services.AddScoped<IIdentityAdminService, IdentityAdminService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IPasskeyService, PasskeyService>();
        services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
        services.AddScoped<IExternalLoginService, ExternalLoginService>();
        services.AddScoped<IFederationProviderService, FederationProviderService>();
        services.AddScoped<ICanonicalOrigin, CanonicalOrigin>();
        services.AddScoped<IIdentityConfigLoader, IdentityConfigLoader>();
        services.AddHttpClient();
        services.AddScoped<ISamlServiceProvider, SamlServiceProvider>();

        // Ambient caller context (user vs system) for authorization + audit attribution.
        services.AddScoped<ICallerContext, CallerContext>();
        // Hands enrollment recovery codes from the endpoint to the page across the redirect.
        services.AddSingleton<MfaEnrollmentCodes>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CallerContextCircuitHandler>();

        // Append-only audit sink + background writer + SIEM forwarding + query (design doc 05).
        services.AddSingleton(new AuditRetentionOptions());
        services.AddSingleton<IAuditForwardingConfig, AuditForwardingConfig>();
        services.AddSingleton<IAuditForwarder, AuditForwarder>();
        services.AddSingleton<AuditWriterService>();
        services.AddSingleton<IAuditLogger>(sp => sp.GetRequiredService<AuditWriterService>());
        services.AddHostedService(sp => sp.GetRequiredService<AuditWriterService>());
        services.AddScoped<IAuditQueryService, AuditQueryService>();

        return services;
    }

    /// <summary>
    /// Configures the interactive authentication pipeline: Identity application cookie (replacing the
    /// legacy self-issued JWT-in-cookie), security-stamp revalidation, and the Blazor Server
    /// revalidating auth-state provider. This is the cutover step - it sets the cookie schemes as the
    /// default, so it must run in place of the old JWT scheme registration (design docs 02, 06).
    /// </summary>
    public static IServiceCollection AddNetOptIdentityAuthentication(this IServiceCollection services)
    {
        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        authBuilder.AddIdentityCookies();

        // Register the shared OpenID Connect handler infrastructure once (via a placeholder scheme);
        // per-provider schemes are added at runtime by DynamicSchemeManager and configured by
        // ConfigureOidcOptions (design doc 03 - dynamic providers, no restart).
        authBuilder.AddOpenIdConnect(ConfigureOidcOptions.Prefix + "__template__", _ => { });
        services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions>, ConfigureOidcOptions>();
        services.AddSingleton<DynamicSchemeManager>();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "netopt_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // Secure only over HTTPS - LAN installs on plain http://host:8042 must still work.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.LoginPath = "/login";
            options.LogoutPath = "/api/auth/logout";
            // NOT the login page: a policy refusing an authenticated user is not a failure to
            // authenticate, and sending them to /login reads as being signed out of a session that is
            // perfectly good. Switching to a site you do not administer while on Settings did exactly
            // that.
            options.AccessDeniedPath = "/denied";

            // Preserve the pre-Identity behaviour: API calls get 401 (not a login redirect), and the
            // tab's ?site= pin is carried through the login redirect so re-auth lands on the same site.
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                var site = context.Request.Query[SiteContextService.SiteQueryParam].ToString();
                context.Response.Redirect(string.IsNullOrEmpty(site)
                    ? "/login"
                    : $"/login?{SiteContextService.SiteQueryParam}={Uri.EscapeDataString(site)}");
                return Task.CompletedTask;
            };

            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
                context.Response.Redirect(options.AccessDeniedPath);
                return Task.CompletedTask;
            };
        });

        // Rotate/revoke sessions within ~5 min of a stamp change (role/password/MFA/disable).
        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.FromMinutes(5);
        });

        // Blazor Server circuit revalidation (security stamp + IsEnabled + membership version).
        services.AddScoped<AuthenticationStateProvider, RevalidatingIdentityAuthenticationStateProvider>();

        return services;
    }
}
