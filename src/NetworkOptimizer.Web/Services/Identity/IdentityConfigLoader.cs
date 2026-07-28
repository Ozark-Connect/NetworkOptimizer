using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Applies IaC-declared federation providers on boot (design doc 03): a mounted JSON file
/// (default <c>/app/config/identity.json</c>, override with <c>NETOPT_IDENTITY_CONFIG</c>) is upserted
/// by provider scheme, with each managed provider flagged <see cref="FederationProvider.ManagedByConfigFile"/>
/// so the UI shows "managed by config file". The DB remains the source of truth; the file just stamps
/// fleet config. A per-provider client secret may also come from an env var
/// (<c>NETOPT_FED_{SCHEME}_SECRET</c>) so secrets need not sit in the mounted file.
/// </summary>
public interface IIdentityConfigLoader
{
    Task ApplyAsync();
}

/// <inheritdoc />
public sealed class IdentityConfigLoader : IIdentityConfigLoader
{
    private readonly IFederationProviderService _providers;
    private readonly ILogger<IdentityConfigLoader> _logger;

    public IdentityConfigLoader(IFederationProviderService providers, ILogger<IdentityConfigLoader> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task ApplyAsync()
    {
        var path = Environment.GetEnvironmentVariable("NETOPT_IDENTITY_CONFIG") ?? "/app/config/identity.json";
        if (!File.Exists(path))
            return;

        IdentityConfigFile? config;
        try
        {
            config = JsonSerializer.Deserialize<IdentityConfigFile>(
                await File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse identity config file at {Path}.", path);
            return;
        }

        if (config?.Providers is null)
            return;

        foreach (var p in config.Providers)
        {
            if (string.IsNullOrEmpty(p.Scheme))
                continue;

            var existing = await _providers.GetBySchemeAsync(p.Scheme);
            var provider = existing ?? new FederationProvider();
            provider.Scheme = p.Scheme;
            provider.Type = Enum.TryParse<FederationProviderType>(p.Type, ignoreCase: true, out var t) ? t : FederationProviderType.Oidc;
            provider.DisplayName = p.DisplayName ?? p.Scheme;
            provider.ButtonLabel = string.IsNullOrWhiteSpace(p.ButtonLabel)
                ? $"Sign in with {provider.DisplayName}"
                : p.ButtonLabel;
            provider.Enabled = p.Enabled ?? true;
            provider.Authority = p.Authority;
            provider.ClientId = p.ClientId;
            provider.Scopes = p.Scopes;
            provider.SubjectClaim = p.SubjectClaim;
            provider.UsernameClaim = p.UsernameClaim;
            provider.DisplayNameClaim = p.DisplayNameClaim;
            provider.EmailClaim = p.EmailClaim;
            provider.GroupsClaim = p.GroupsClaim;
            provider.JitProvisioning = Enum.TryParse<JitProvisioningMode>(p.JitProvisioning, ignoreCase: true, out var j) ? j : provider.JitProvisioning;
            provider.RoleMappingMode = Enum.TryParse<RoleMappingMode>(p.RoleMappingMode, ignoreCase: true, out var r) ? r : provider.RoleMappingMode;
            provider.ManagedByConfigFile = true;

            // Secret precedence: env var, then the file (so secrets can stay out of the mounted file).
            var envSecret = Environment.GetEnvironmentVariable($"NETOPT_FED_{p.Scheme.ToUpperInvariant().Replace('-', '_')}_SECRET");
            var secret = envSecret ?? p.ClientSecret;

            await _providers.SaveAsync(provider, secret);
        }

        _logger.LogInformation("Applied {Count} IaC-managed federation providers from {Path}.", config.Providers.Count, path);
    }

    private sealed class IdentityConfigFile
    {
        [JsonPropertyName("providers")] public List<ProviderConfig>? Providers { get; set; }
    }

    private sealed class ProviderConfig
    {
        public string? Scheme { get; set; }
        public string? Type { get; set; }
        public string? DisplayName { get; set; }
        public string? ButtonLabel { get; set; }
        public bool? Enabled { get; set; }
        public string? Authority { get; set; }
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? Scopes { get; set; }
        public string? SubjectClaim { get; set; }
        public string? UsernameClaim { get; set; }
        public string? DisplayNameClaim { get; set; }
        public string? EmailClaim { get; set; }
        public string? GroupsClaim { get; set; }
        public string? JitProvisioning { get; set; }
        public string? RoleMappingMode { get; set; }
    }
}
