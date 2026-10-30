using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

/// <summary>
/// Per-modem SSH providers offer the site's stored key alongside the modem's own password,
/// the same additive way gateway and device SSH do.
/// </summary>
public class ModemStoredSshKeyTests
{
    private static readonly ModemPollContext Context = new()
    {
        Id = 1,
        SiteSlug = "default",
        Name = "Backup LTE",
        Host = "192.0.2.1",  // RFC 5737 documentation address
        Username = "admin",
        Password = "modem-password",
    };

    private static IServiceProvider SiteProvider(SshKey? key)
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<NetworkOptimizerDbContext>(o => o.UseInMemoryDatabase(name));
        var protection = new Mock<ICredentialProtectionService>();
        protection.Setup(p => p.Decrypt(It.IsAny<string>())).Returns<string>(s => "decrypted:" + s);
        services.AddSingleton(protection.Object);
        var sp = services.BuildServiceProvider();

        if (key != null)
        {
            var db = sp.GetRequiredService<NetworkOptimizerDbContext>();
            db.SshKeys.Add(key);
            db.SaveChanges();
        }
        return sp;
    }

    private static SshKey Key(string? passphrase = null) => new()
    {
        PublicKey = "ssh-ed25519 AAAA test",
        PrivateKeyProtected = "pem",
        PassphraseProtected = passphrase,
        Fingerprint = "SHA256:test",
    };

    [Fact]
    public async Task WithStoredKey_KeyPresent_ContextCarriesKeyAndKeepsPassword()
    {
        var context = await CellularModemService.WithStoredKeyAsync(Context, SiteProvider(Key("phrase")));

        context.StoredPrivateKeyPem.Should().Be("decrypted:pem");
        context.StoredPrivateKeyPassphrase.Should().Be("decrypted:phrase");
        context.Password.Should().Be("modem-password");
        context.Username.Should().Be("admin");
    }

    [Fact]
    public async Task WithStoredKey_NoKey_ContextUnchanged()
    {
        var context = await CellularModemService.WithStoredKeyAsync(Context, SiteProvider(null));

        context.Should().BeSameAs(Context);
        context.StoredPrivateKeyPem.Should().BeNull();
        context.Password.Should().Be("modem-password");
    }

    [Fact]
    public async Task Providers_KeyPresent_ConnectionOffersStoredKeyAndPassword()
    {
        var context = await CellularModemService.WithStoredKeyAsync(Context, SiteProvider(Key()));

        foreach (var connection in new[]
                 {
                     await QuectelAtModemProvider.ToConnectionInfoAsync(context),
                     await MikroTikRouterOsProvider.ToConnectionInfoAsync(context),
                 })
        {
            connection.StoredPrivateKeyPem.Should().Be("decrypted:pem");
            connection.StoredPrivateKeyPassphrase.Should().BeNull();
            connection.Password.Should().Be("modem-password");
            connection.HasCredentials.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Providers_NoKey_ConnectionUsesPasswordOnly()
    {
        var context = await CellularModemService.WithStoredKeyAsync(Context, SiteProvider(null));

        foreach (var connection in new[]
                 {
                     await QuectelAtModemProvider.ToConnectionInfoAsync(context),
                     await MikroTikRouterOsProvider.ToConnectionInfoAsync(context),
                 })
        {
            connection.StoredPrivateKeyPem.Should().BeNull();
            connection.Password.Should().Be("modem-password");
            connection.UsePasswordAuth.Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(ModemProviderKeys.MikroTikRouterOs, false, false)]
    [InlineData(ModemProviderKeys.MikroTikRouterOs, true, true)]
    [InlineData(ModemProviderKeys.QuectelAt, false, false)]
    [InlineData(ModemProviderKeys.QuectelAt, true, true)]
    [InlineData(ModemProviderKeys.Qmicli, true, false)]
    public void The_stored_key_is_offered_only_to_a_per_modem_SSH_modem_that_opts_in(
        string provider, bool useManagedSshKey, bool offered)
    {
        // Off by default: an existing password modem is never offered a key it does not hold.
        var modem = new ModemConfiguration { Provider = provider, UseManagedSshKey = useManagedSshKey };

        CellularModemService.OffersStoredKey(modem).Should().Be(offered);
    }

    [Fact]
    public async Task Providers_KeyOnly_ConnectionHasCredentialsWithoutAPassword()
    {
        // A modem saved with the key ticked and no password must still authenticate.
        var keyOnly = Context with { Password = null };
        var context = await CellularModemService.WithStoredKeyAsync(keyOnly, SiteProvider(Key()));

        foreach (var connection in new[]
                 {
                     await QuectelAtModemProvider.ToConnectionInfoAsync(context),
                     await MikroTikRouterOsProvider.ToConnectionInfoAsync(context),
                 })
        {
            connection.Password.Should().BeNull();
            connection.StoredPrivateKeyPem.Should().Be("decrypted:pem");
            connection.HasCredentials.Should().BeTrue();
        }
    }

    [Fact]
    public void A_modem_does_not_opt_in_by_default()
    {
        new ModemConfiguration().UseManagedSshKey.Should().BeFalse();
    }
}
