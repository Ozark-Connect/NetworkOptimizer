using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Registers and refreshes per-provider authentication schemes at runtime so providers can be added,
/// updated, or removed WITHOUT restarting the app (design doc 03). OIDC providers get an
/// <c>oidc:&lt;scheme&gt;</c> scheme backed by the shared <see cref="OpenIdConnectHandler"/>; SAML
/// providers are handled by the SAML SP endpoints, not an auth handler. Call <see cref="SyncAsync"/> at
/// startup and after any provider change.
/// </summary>
public sealed class DynamicSchemeManager
{
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly IOptionsMonitorCache<OpenIdConnectOptions> _oidcCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DynamicSchemeManager> _logger;

    public DynamicSchemeManager(
        IAuthenticationSchemeProvider schemes,
        IOptionsMonitorCache<OpenIdConnectOptions> oidcCache,
        IServiceScopeFactory scopeFactory,
        ILogger<DynamicSchemeManager> logger)
    {
        _schemes = schemes;
        _oidcCache = oidcCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Ensures a scheme exists (and is freshly configured) for every enabled OIDC provider.</summary>
    public async Task SyncAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<IFederationProviderService>();
        var enabled = await providers.GetEnabledAsync();

        foreach (var provider in enabled.Where(p => p.Type == FederationProviderType.Oidc))
        {
            var schemeName = ConfigureOidcOptions.Prefix + provider.Scheme;
            if (await _schemes.GetSchemeAsync(schemeName) is null)
            {
                _schemes.AddScheme(new AuthenticationScheme(
                    schemeName, provider.DisplayName, typeof(OpenIdConnectHandler)));
                _logger.LogInformation("Registered OIDC scheme {Scheme} for provider {Provider}.", schemeName, provider.DisplayName);
            }

            // Drop any cached options so the next challenge reloads current config from the DB.
            _oidcCache.TryRemove(schemeName);
        }
    }

    /// <summary>Removes a provider's scheme + cached options (called when a provider is deleted/disabled).</summary>
    public async Task RemoveAsync(string providerScheme)
    {
        var schemeName = ConfigureOidcOptions.Prefix + providerScheme;
        if (await _schemes.GetSchemeAsync(schemeName) is not null)
            _schemes.RemoveScheme(schemeName);
        _oidcCache.TryRemove(schemeName);
    }
}
