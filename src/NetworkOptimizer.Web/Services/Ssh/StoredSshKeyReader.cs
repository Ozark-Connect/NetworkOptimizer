using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Reads and decrypts the site's stored SSH key for the authentication path.
///
/// Separate from <see cref="ISshKeyService"/> on purpose: that one is role-gated for the UI, while this
/// runs for background pollers and deployments that have no caller to authorize. Neither returns a
/// private key to a caller outside the server - this one hands it straight to SSH.NET.
/// </summary>
public static class StoredSshKeyReader
{
    /// <summary>
    /// Attaches the site's stored key to a connection, if it has one. Called from inside a site-pinned
    /// scope, so the context resolves to that site's database.
    ///
    /// Failures are swallowed deliberately: a key that cannot be read must not take down a connection
    /// that has a working password. The connection proceeds with whatever else is configured.
    /// </summary>
    public static async Task AttachAsync(IServiceProvider siteScopedProvider, SshConnectionInfo connection)
    {
        try
        {
            var db = siteScopedProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var key = await db.SshKeys.AsNoTracking().FirstOrDefaultAsync();
            if (key is null) return;

            var protection = siteScopedProvider.GetRequiredService<ICredentialProtectionService>();
            connection.StoredPrivateKeyPem = protection.Decrypt(key.PrivateKeyProtected);
            connection.StoredPrivateKeyPassphrase = string.IsNullOrEmpty(key.PassphraseProtected)
                ? null
                : protection.Decrypt(key.PassphraseProtected);
        }
        catch (Exception)
        {
            // Leave the connection as it was: password and key-file auth are unaffected.
        }
    }

    /// <summary>Whether the site has a stored key, for credential checks that run before connecting.</summary>
    public static async Task<bool> ExistsAsync(IServiceProvider siteScopedProvider)
    {
        try
        {
            var db = siteScopedProvider.GetRequiredService<NetworkOptimizerDbContext>();
            return await db.SshKeys.AsNoTracking().AnyAsync();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
