namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>Protocol of a federation provider.</summary>
public enum FederationProviderType
{
    /// <summary>OpenID Connect relying-party.</summary>
    Oidc = 0,

    /// <summary>SAML 2.0 service-provider.</summary>
    Saml = 1,
}

/// <summary>Just-in-time provisioning behaviour for an unknown external identity.</summary>
public enum JitProvisioningMode
{
    /// <summary>Reject logins with no pre-existing local link.</summary>
    Off = 0,

    /// <summary>Create a local user on first login and link the external identity.</summary>
    CreateOnFirstLogin = 1,
}

/// <summary>How roles/memberships derive from IdP claims.</summary>
public enum RoleMappingMode
{
    /// <summary>Mappings applied at JIT creation only; afterwards a local admin owns roles.</summary>
    Manual = 0,

    /// <summary>Roles + memberships recomputed from claims at every login; local edits locked.</summary>
    IdpAuthoritative = 1,
}

/// <summary>
/// A configured external authentication provider (OIDC RP or SAML SP). DB is the source of truth;
/// schemes are registered/updated at runtime without restart. Secrets are encrypted at rest with
/// ASP.NET Core Data Protection, are write-only in the UI, and are never logged (design doc 03).
/// </summary>
public class FederationProvider
{
    public int Id { get; set; }

    /// <summary>OIDC or SAML.</summary>
    public FederationProviderType Type { get; set; }

    /// <summary>Internal display name (Settings list).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Login-button label ("Sign in with Okta").</summary>
    public string ButtonLabel { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Sort order of the login button.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Stable per-provider scheme key (kebab-case, unique). Used in the authentication scheme name
    /// and callback routes (<c>/signin-oidc/{Scheme}</c>, <c>/saml/{Scheme}/acs</c>). Immutable after
    /// creation so IdP-side redirect URIs stay valid.
    /// </summary>
    public string Scheme { get; set; } = "";

    // --- OIDC ---
    public string? Authority { get; set; }
    public string? ClientId { get; set; }

    /// <summary>Client secret, Data-Protection encrypted. Write-only in UI; never returned or logged.</summary>
    public string? ClientSecretProtected { get; set; }

    /// <summary>Space-separated scope list (e.g. "openid profile email groups").</summary>
    public string? Scopes { get; set; }

    public bool UsePkce { get; set; } = true;

    public string? ResponseType { get; set; } = "code";

    /// <summary>Optional requested authentication context (steps up MFA at the IdP).</summary>
    public string? AcrValues { get; set; }

    public bool GetClaimsFromUserInfo { get; set; }

    /// <summary>Whether RP-initiated logout to the IdP end-session endpoint is enabled.</summary>
    public bool EndSessionSupport { get; set; }

    // --- SAML ---
    public string? IdpMetadataUrl { get; set; }
    public string? IdpMetadataXml { get; set; }
    public string? SpEntityId { get; set; }
    public bool WantAssertionsEncrypted { get; set; }

    /// <summary>
    /// IdP-initiated SSO acceptance. OFF by default (unsolicited-response risk), and deliberately not
    /// offered in the UI: nothing sets it, so it is false on every provider. An assertion that does not
    /// answer a request this server issued is rejected and logged (see SamlServiceProvider). Turning it
    /// on is a checkbox on the SAML form whenever an install genuinely needs IdP-initiated sign-in;
    /// until then the safe posture is the only reachable one.
    /// </summary>
    public bool AllowIdpInitiated { get; set; }

    /// <summary>Assertion clock-skew tolerance in seconds (default 120).</summary>
    public int ClockSkewSeconds { get; set; } = 120;

    /// <summary>SAML assertion-decryption cert (PFX), Data-Protection encrypted. Write-only; never logged.</summary>
    public string? SamlDecryptionCertProtected { get; set; }

    // --- Common claim mapping ---
    public string? SubjectClaim { get; set; }
    public string? UsernameClaim { get; set; }
    public string? DisplayNameClaim { get; set; }
    public string? EmailClaim { get; set; }
    public string? GroupsClaim { get; set; }

    /// <summary>Trust the IdP's MFA (default on); when off and the role requires MFA, local step-up follows login.</summary>
    public bool TrustIdpMfa { get; set; } = true;

    public JitProvisioningMode JitProvisioning { get; set; } = JitProvisioningMode.Off;

    public RoleMappingMode RoleMappingMode { get; set; } = RoleMappingMode.Manual;

    /// <summary>
    /// True when this provider was upserted from the IaC config file (<c>/app/config/identity.json</c>
    /// or env). Editing is locked in the UI ("managed by config file").
    /// </summary>
    public bool ManagedByConfigFile { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>IdP group/claim value to global role mappings.</summary>
    public List<FederationRoleMapping> RoleMappings { get; set; } = new();

    /// <summary>IdP group/claim value to per-site (or group / all-sites) role mappings.</summary>
    public List<FederationSiteMapping> SiteMappings { get; set; } = new();
}

/// <summary>Maps an IdP group/claim value to a global role (design doc 03).</summary>
public class FederationRoleMapping
{
    public int Id { get; set; }
    public int ProviderId { get; set; }

    /// <summary>The group name or claim value to match (e.g. "netopt-admins").</summary>
    public string GroupOrClaimValue { get; set; } = "";

    /// <summary>Target global role name (see <see cref="Roles"/>).</summary>
    public string GlobalRole { get; set; } = "";
}

/// <summary>Maps an IdP group/claim value to a site/group/all-sites membership (design doc 03/04).</summary>
public class FederationSiteMapping
{
    public int Id { get; set; }
    public int ProviderId { get; set; }

    /// <summary>The group name or claim value to match.</summary>
    public string GroupOrClaimValue { get; set; } = "";

    /// <summary>Whether the grant targets a single site, a site group, or all sites.</summary>
    public MembershipTargetType TargetType { get; set; } = MembershipTargetType.Site;

    /// <summary>Site slug (Site), group name (Group), or null (AllSites).</summary>
    public string? TargetValue { get; set; }

    /// <summary>The site role granted.</summary>
    public SiteRole SiteRole { get; set; }
}
