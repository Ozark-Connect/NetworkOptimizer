using FluentAssertions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Regression guard for the OIDC handler-registration scheme. The shared OpenID Connect handler is
/// registered via a placeholder "__template__" scheme, and because the OIDC handler is an
/// IAuthenticationRequestHandler, UseAuthentication initializes it on EVERY request to test for its
/// callback path - which runs the full options pipeline (Configure -> PostConfigure -> Validate). If
/// the placeholder (or any scheme with no matching provider) is left without a ClientId/Authority,
/// Validate throws and every request 500s (this took the app down on the first Mac deploy).
/// </summary>
public class ConfigureOidcOptionsTests
{
    private const string TemplateScheme = ConfigureOidcOptions.Prefix + "__template__";

    private static IOptionsMonitor<OpenIdConnectOptions> BuildOptionsPipeline(params FederationProvider[] providers)
    {
        // No provider matches the placeholder scheme.
        var providerService = new Mock<IFederationProviderService>();
        providerService.Setup(s => s.GetBySchemeAsync(It.IsAny<string>()))
            .ReturnsAsync((string scheme) => providers.FirstOrDefault(p => p.Scheme == scheme));

        var canonical = new Mock<ICanonicalOrigin>();
        canonical.SetupGet(c => c.Configured).Returns((string?)null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddScoped(_ => providerService.Object);
        services.AddSingleton(canonical.Object);

        // Mirror the app: register the OIDC handler infra via a placeholder scheme, then our named
        // options configurer. Resolving the options runs Configure + PostConfigure + Validate.
        var auth = services.AddAuthentication().AddOpenIdConnect(TemplateScheme, _ => { });
        foreach (var p in providers)
            auth.AddOpenIdConnect(ConfigureOidcOptions.Prefix + p.Scheme, _ => { });
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, ConfigureOidcOptions>();

        return services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
    }

    [Fact]
    public void UnconfiguredScheme_ResolvesWithoutThrowing()
    {
        var monitor = BuildOptionsPipeline();

        // The exact failure that took the app down on first deploy: Validate() -> ArgumentNullException
        // on ClientId (then Authority). Resolving the options must no longer throw.
        var act = () => monitor.Get(TemplateScheme);
        act.Should().NotThrow();
        monitor.Get(TemplateScheme).ClientId.Should().NotBeNullOrEmpty();
    }

    private static FederationProvider Oidc(string scheme, string? authority, string? clientId = "client") => new()
    {
        Scheme = scheme,
        DisplayName = scheme,
        Type = FederationProviderType.Oidc,
        Enabled = true,
        Authority = authority,
        ClientId = clientId,
    };

    [Theory]
    [InlineData("http://idp.example.com")]
    [InlineData("not a url")]
    [InlineData("idp.example.com")]
    [InlineData("https:\\\\idp.example.com")]
    [InlineData("")]
    public void UnusableAuthority_IsHeldInertInsteadOfFailingEveryRequest(string authority)
    {
        // One provider row with a plain-http or malformed Authority used to make Validate throw on every
        // request, which took down every route including the sign-in page, and survived a restart.
        var monitor = BuildOptionsPipeline(Oidc("broken", authority));

        var act = () => monitor.Get(ConfigureOidcOptions.Prefix + "broken");
        act.Should().NotThrow();
        var options = monitor.Get(ConfigureOidcOptions.Prefix + "broken");
        options.ClientId.Should().Be("unconfigured");
        options.CallbackPath.Value.Should().Be("/signin-oidc-unconfigured/broken");
    }

    [Fact]
    public void MissingClientId_IsHeldInert()
    {
        var monitor = BuildOptionsPipeline(Oidc("noclient", "https://idp.example.com", clientId: " "));

        monitor.Get(ConfigureOidcOptions.Prefix + "noclient").ClientId.Should().Be("unconfigured");
    }

    [Fact]
    public void UsableProvider_GetsItsOwnSettings()
    {
        var monitor = BuildOptionsPipeline(Oidc("good", " https://idp.example.com/realms/x "));

        var options = monitor.Get(ConfigureOidcOptions.Prefix + "good");
        options.Authority.Should().Be("https://idp.example.com/realms/x");
        options.ClientId.Should().Be("client");
        options.CallbackPath.Value.Should().Be("/signin-oidc/good");
    }

    [Fact]
    public async Task RemoteFailure_ReturnsToSignInWithAMessage()
    {
        // A user who cancels at the IdP, or returns after the correlation cookie expired, used to get
        // the unhandled-exception page.
        var monitor = BuildOptionsPipeline(Oidc("good", "https://idp.example.com"));
        var options = monitor.Get(ConfigureOidcOptions.Prefix + "good");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme(
            ConfigureOidcOptions.Prefix + "good", null, typeof(OpenIdConnectHandler));
        var context = new Microsoft.AspNetCore.Authentication.RemoteFailureContext(
            http, scheme, options, new Exception("Correlation failed."));

        await options.Events.RemoteFailure(context);

        context.Result.Should().NotBeNull();
        context.Result!.Handled.Should().BeTrue();
        http.Response.StatusCode.Should().Be(302);
        http.Response.Headers.Location.ToString().Should().Be("/login?error=external_failed");
    }

    [Theory]
    [InlineData("https://idp.example.com", "client", null)]
    [InlineData("http://idp.example.com", "client", "https://")]
    [InlineData(null, "client", "Authority")]
    [InlineData("https://idp.example.com", "", "Client ID")]
    public void ConfigurationProblem_NamesWhatIsWrong(string? authority, string clientId, string? expectedFragment)
    {
        var problem = ConfigureOidcOptions.ConfigurationProblem(Oidc("x", authority, clientId));

        if (expectedFragment is null)
            problem.Should().BeNull();
        else
            problem.Should().Contain(expectedFragment);
    }
}
