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

    private static IOptionsMonitor<OpenIdConnectOptions> BuildOptionsPipeline()
    {
        // No provider matches the placeholder scheme.
        var providerService = new Mock<IFederationProviderService>();
        providerService.Setup(s => s.GetBySchemeAsync(It.IsAny<string>()))
            .ReturnsAsync((FederationProvider?)null);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddScoped(_ => providerService.Object);

        // Mirror the app: register the OIDC handler infra via a placeholder scheme, then our named
        // options configurer. Resolving the options runs Configure + PostConfigure + Validate.
        services.AddAuthentication().AddOpenIdConnect(TemplateScheme, _ => { });
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
}
