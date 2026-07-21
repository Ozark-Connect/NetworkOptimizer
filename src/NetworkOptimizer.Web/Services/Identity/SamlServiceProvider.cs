using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Thin SAML 2.0 service-provider abstraction (design doc 03) so the underlying library is swappable -
/// the surface we need is small: SP metadata, an SP-initiated AuthnRequest, ACS response validation to
/// a claims principal, and best-effort SLO. The concrete implementation wraps ITfoxtec.Identity.Saml2.
/// </summary>
public interface ISamlServiceProvider
{
    /// <summary>Returns the SP metadata XML for a provider (EntityId, ACS URL, signing cert).</summary>
    Task<string> BuildMetadataAsync(FederationProvider provider, HttpContext context);

    /// <summary>Builds the SP-initiated AuthnRequest redirect to the IdP.</summary>
    Task<IResult> ChallengeAsync(FederationProvider provider, HttpContext context, string returnUrl);

    /// <summary>Validates an ACS response (signature, conditions, replay) and returns the asserted claims principal.</summary>
    Task<ClaimsPrincipal?> HandleAssertionAsync(FederationProvider provider, HttpContext context);
}

/// <inheritdoc />
public sealed class SamlServiceProvider : ISamlServiceProvider
{
    private readonly ICanonicalOrigin _origin;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SamlServiceProvider> _logger;

    public SamlServiceProvider(ICanonicalOrigin origin, IHttpClientFactory httpClientFactory, ILogger<SamlServiceProvider> logger)
    {
        _origin = origin;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private async Task<Saml2Configuration> BuildConfigAsync(FederationProvider provider, HttpContext context)
    {
        var origin = _origin.Resolve(context);
        var config = new Saml2Configuration
        {
            Issuer = provider.SpEntityId ?? $"{origin}/saml/{provider.Scheme}/metadata",
            SingleSignOnDestination = null, // populated from IdP metadata below
            AllowedAudienceUris = { provider.SpEntityId ?? $"{origin}/saml/{provider.Scheme}/metadata" },
            SignAuthnRequest = false,
        };
        config.RevocationMode = X509RevocationMode.NoCheck;

        // Load IdP metadata (URL or pasted XML) to discover the SSO destination + signing certs.
        if (!string.IsNullOrEmpty(provider.IdpMetadataUrl))
        {
            var idpMetadata = new EntityDescriptor();
            await idpMetadata.ReadIdPSsoDescriptorFromUrlAsync(_httpClientFactory, new Uri(provider.IdpMetadataUrl));
            ApplyIdpMetadata(config, idpMetadata);
        }
        else if (!string.IsNullOrEmpty(provider.IdpMetadataXml))
        {
            var idpMetadata = new EntityDescriptor();
            idpMetadata.ReadIdPSsoDescriptor(provider.IdpMetadataXml);
            ApplyIdpMetadata(config, idpMetadata);
        }

        return config;
    }

    private static void ApplyIdpMetadata(Saml2Configuration config, EntityDescriptor idpMetadata)
    {
        if (idpMetadata.IdPSsoDescriptor is null)
            return;
        config.SingleSignOnDestination = idpMetadata.IdPSsoDescriptor.SingleSignOnServices.First().Location;
        config.SingleLogoutDestination = idpMetadata.IdPSsoDescriptor.SingleLogoutServices?.FirstOrDefault()?.Location;
        foreach (var cert in idpMetadata.IdPSsoDescriptor.SigningCertificates)
            config.SignatureValidationCertificates.Add(cert);
    }

    public async Task<string> BuildMetadataAsync(FederationProvider provider, HttpContext context)
    {
        var origin = _origin.Resolve(context);
        var config = await BuildConfigAsync(provider, context);
        var entityId = provider.SpEntityId ?? $"{origin}/saml/{provider.Scheme}/metadata";

        var entityDescriptor = new EntityDescriptor(config)
        {
            ValidUntil = 365,
            SPSsoDescriptor = new SPSsoDescriptor
            {
                WantAssertionsSigned = true,
                AssertionConsumerServices = new[]
                {
                    new AssertionConsumerService { Binding = ProtocolBindings.HttpPost, Location = new Uri($"{origin}/saml/{provider.Scheme}/acs") },
                },
            },
        };
        return entityDescriptor.ToXmlDocument().OuterXml;
    }

    public async Task<IResult> ChallengeAsync(FederationProvider provider, HttpContext context, string returnUrl)
    {
        var config = await BuildConfigAsync(provider, context);
        if (config.SingleSignOnDestination is null)
            return Results.Redirect("/login?error=saml_misconfigured");

        var binding = new Saml2RedirectBinding();
        binding.SetRelayStateQuery(new Dictionary<string, string> { { "returnUrl", returnUrl } });
        binding.Bind(new Saml2AuthnRequest(config));
        return Results.Redirect(binding.RedirectLocation.OriginalString);
    }

    public async Task<ClaimsPrincipal?> HandleAssertionAsync(FederationProvider provider, HttpContext context)
    {
        try
        {
            var config = await BuildConfigAsync(provider, context);
            var genericRequest = await context.Request.ToGenericHttpRequestAsync(readBodyAsString: true);
            var binding = new Saml2PostBinding();
            var response = new Saml2AuthnResponse(config);
            binding.ReadSamlResponse(genericRequest, response);

            if (response.Status != Saml2StatusCodes.Success)
            {
                _logger.LogWarning("SAML response for {Provider} had status {Status}.", provider.DisplayName, response.Status);
                return null;
            }

            binding.Unbind(genericRequest, response);
            return response.ClaimsIdentity is null ? null : new ClaimsPrincipal(response.ClaimsIdentity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAML assertion validation failed for {Provider}.", provider.DisplayName);
            return null;
        }
    }
}
