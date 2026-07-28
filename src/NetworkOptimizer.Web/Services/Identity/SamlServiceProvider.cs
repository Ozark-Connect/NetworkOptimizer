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


    /// <summary>
    /// This install's SAML entity ID: the operator's value when they set one, otherwise our metadata
    /// URL.
    ///
    /// Null-or-EMPTY, not just null. The field is optional and clearing it in the UI stores an empty
    /// string, so a plain ?? left the entity ID blank - we then issued an AuthnRequest with no issuer
    /// and accepted an audience of "", which surfaced as a NullReferenceException from inside the SAML
    /// library rather than anything naming the setting.
    /// </summary>
    private static string SpEntityId(FederationProvider provider, string origin)
        => string.IsNullOrWhiteSpace(provider.SpEntityId)
            ? $"{origin}/saml/{provider.Scheme}/metadata"
            : provider.SpEntityId.Trim();

    private async Task<Saml2Configuration> BuildConfigAsync(FederationProvider provider, HttpContext context)
    {
        var origin = _origin.Resolve(context);
        var entityId = SpEntityId(provider, origin);
        var config = new Saml2Configuration
        {
            Issuer = entityId,
            SingleSignOnDestination = null, // populated from IdP metadata below
            AllowedAudienceUris = { entityId },
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
        var entityId = SpEntityId(provider, origin);

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

        var request = new Saml2AuthnRequest(config);
        var binding = new Saml2RedirectBinding();
        binding.SetRelayStateQuery(new Dictionary<string, string> { { "returnUrl", returnUrl } });
        binding.Bind(request);

        RememberRequest(context, provider, request.IdAsString);
        return Results.Redirect(binding.RedirectLocation.OriginalString);
    }



    /// <summary>
    /// Describes the response's shape at Debug so a parse failure inside the library can be traced to
    /// what was actually sent. Element names, the status code and the issuer only - never the assertion
    /// itself, which carries the subject's identity.
    /// </summary>
    private void LogResponseShape(FederationProvider provider, string samlResponseBase64)
    {
        try
        {
            var xml = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(samlResponseBase64));
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null)
                return;

            const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
            const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";

            var status = root.Element(System.Xml.Linq.XName.Get("Status", Protocol))
                ?.Element(System.Xml.Linq.XName.Get("StatusCode", Protocol))
                ?.Attribute("Value")?.Value;

            _logger.LogDebug(
                "SAML response for {Provider}: root <{Root}>, issuer {Issuer}, status {Status}, "
                + "destination {Destination}, inResponseTo {InResponseTo}, assertion {HasAssertion}, "
                + "encrypted {HasEncrypted}, signature {HasSignature}",
                provider.DisplayName,
                root.Name.LocalName,
                root.Element(System.Xml.Linq.XName.Get("Issuer", Assertion))?.Value ?? "(none)",
                status ?? "(none)",
                root.Attribute("Destination")?.Value ?? "(none)",
                root.Attribute("InResponseTo")?.Value ?? "(none)",
                root.Element(System.Xml.Linq.XName.Get("Assertion", Assertion)) is not null,
                root.Element(System.Xml.Linq.XName.Get("EncryptedAssertion", Assertion)) is not null,
                root.Elements().Any(e => e.Name.LocalName == "Signature"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not describe the SAML response shape for {Provider}.", provider.DisplayName);
        }
    }

    /// <summary>
    /// Correlation cookie name for a provider. One per provider so two IdPs in flight at once do not
    /// clobber each other.
    /// </summary>
    private static string CorrelationCookie(FederationProvider provider) => $"netopt_saml_{provider.Scheme}";

    /// <summary>
    /// Remembers the AuthnRequest we just issued so the response can be tied back to it.
    ///
    /// SameSite=None because the IdP POSTs the response cross-site and a Lax cookie would not be sent -
    /// which forces Secure, and therefore HTTPS. On a plain-HTTP install the cookie is not set at all
    /// and correlation is skipped rather than rejecting every login; the warning says so. SAML over
    /// plain HTTP is already outside sensible practice, and silently breaking such an install would be
    /// worse than declining to add a check it cannot carry.
    /// </summary>
    private void RememberRequest(HttpContext context, FederationProvider provider, string requestId)
    {
        if (!context.Request.IsHttps && !(_origin.Configured?.StartsWith("https://") ?? false))
        {
            _logger.LogWarning(
                "SAML request correlation is off for {Provider}: it needs a Secure cookie, which needs "
                + "HTTPS. Responses cannot be tied to the request that started them.", provider.DisplayName);
            return;
        }

        context.Response.Cookies.Append(CorrelationCookie(provider), requestId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            IsEssential = true,
            Expires = DateTimeOffset.UtcNow.AddMinutes(15),
            Path = "/",
        });
    }

    /// <summary>
    /// Ties a response back to the request that started it, and consumes the cookie so the same
    /// assertion cannot be posted twice - single use is what makes this replay protection as well as
    /// correlation.
    /// </summary>
    private bool ConsumeRequestCorrelation(HttpContext context, FederationProvider provider, string? inResponseTo)
    {
        var name = CorrelationCookie(provider);
        var expected = context.Request.Cookies[name];
        context.Response.Cookies.Delete(name);

        if (string.IsNullOrEmpty(expected))
        {
            // No cookie: either an IdP-initiated response (already refused upstream unless the provider
            // opted in), or an install that cannot set one. Both are decided before this point.
            return true;
        }

        if (string.Equals(expected, inResponseTo, StringComparison.Ordinal))
            return true;

        _logger.LogWarning(
            "SAML response for {Provider} did not answer the request we issued - InResponseTo {Actual} "
            + "does not match. Rejected.", provider.DisplayName, inResponseTo ?? "(absent)");
        return false;
    }

    public async Task<ClaimsPrincipal?> HandleAssertionAsync(FederationProvider provider, HttpContext context)
    {
        try
        {
            var config = await BuildConfigAsync(provider, context);

            if (!context.Request.HasFormContentType)
            {
                _logger.LogWarning(
                    "SAML response for {Provider} was not a form POST (content type {ContentType}).",
                    provider.DisplayName, context.Request.ContentType ?? "(none)");
                return null;
            }

            // readBodyAsString: false. The POST binding reads Form, and asking for the body instead
            // left Form null on the converted request - which is the absent thing the reader was
            // dereferencing. Convert once and read only the result: touching context.Request.Form
            // first consumes the stream, and then neither Form nor Body survives the conversion.
            var genericRequest = await context.Request.ToGenericHttpRequestAsync(readBodyAsString: false);

            var samlResponse = genericRequest.Form?["SAMLResponse"];
            if (string.IsNullOrEmpty(samlResponse))
            {
                _logger.LogWarning(
                    "SAML response for {Provider} carried no SAMLResponse field. Form {FormState}, keys [{Keys}].",
                    provider.DisplayName,
                    genericRequest.Form is null ? "null" : "present",
                    genericRequest.Form is null
                        ? ""
                        : string.Join(", ", genericRequest.Form.AllKeys.Where(k => k is not null)));
                return null;
            }

            LogResponseShape(provider, samlResponse);

            var binding = new Saml2PostBinding();
            var response = new Saml2AuthnResponse(config);
            genericRequest.Binding = binding;

            binding.ReadSamlResponse(genericRequest, response);

            if (response.Status != Saml2StatusCodes.Success)
            {
                _logger.LogWarning("SAML response for {Provider} had status {Status}.", provider.DisplayName, response.Status);
                return null;
            }

            binding.Unbind(genericRequest, response);

            // Correlate only AFTER Unbind, which is what validates the signature: deciding anything on
            // the strength of an unverified InResponseTo would be trusting the attacker's own value.
            if (!ConsumeRequestCorrelation(context, provider, response.InResponseTo?.Value))
                return null;

            return response.ClaimsIdentity is null ? null : new ClaimsPrincipal(response.ClaimsIdentity);
        }
        catch (Exception ex)
        {
            // Include the exception type: ITfoxtec surfaces audience mismatch, signature failure and a
            // malformed response all through the same catch, and "validation failed" alone sent us
            // hunting the wrong one.
            _logger.LogWarning(ex,
                "SAML assertion validation failed for {Provider} ({ExceptionType}): {Message}",
                provider.DisplayName, ex.GetType().Name, ex.Message);
            return null;
        }
    }
}
