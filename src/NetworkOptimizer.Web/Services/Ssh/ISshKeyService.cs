using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Identity;
using Renci.SshNet;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// What the UI is allowed to know about the site's stored key. Deliberately has no private-key member:
/// the type itself is the reason no future edit can leak one through a view model.
/// </summary>
/// <param name="KeyType">"ed25519" or "rsa".</param>
/// <param name="Source">"Generated" or "Uploaded".</param>
/// <param name="PublicKey">The line the user installs on their devices.</param>
/// <param name="Fingerprint">OpenSSH SHA256 fingerprint.</param>
/// <param name="CreatedAt">When it was created or uploaded.</param>
public sealed record SshKeyInfo(
    string KeyType, string Source, string PublicKey, string Fingerprint, DateTime CreatedAt);

/// <summary>
/// The site's stored SSH keypair: generate one, upload an existing one, or remove it. Optional in every
/// sense - password and key-file authentication are unchanged and a site with no stored key behaves
/// exactly as it did before this existed.
///
/// Site-scoped, so the roles below resolve against the site in context: administering one site does not
/// confer key management on another. The private half is never returned by anything here.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISshKeyService
{
    /// <summary>The stored key's public details, or null when the site has none.</summary>
    [RequireRole(Roles.Viewer)]
    Task<SshKeyInfo?> GetAsync();

    /// <summary>
    /// Generates a keypair, replacing any existing one. The private half is protected here and never
    /// leaves the server.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key")]
    Task<SshKeyInfo> GenerateAsync(SshKeyType type);

    /// <summary>
    /// Stores a keypair the user already has, replacing any existing one. The public half is derived
    /// from the private key rather than taken on trust, which also validates that it parses.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key does not parse, or the passphrase is wrong.</exception>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key")]
    Task<SshKeyInfo> UploadAsync(string privateKeyPem, string? passphrase);

    /// <summary>Removes the stored key. Connections fall back to whatever else is configured.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, TargetType = "ssh_key")]
    Task RemoveAsync();
}

/// <inheritdoc />
public sealed class SshKeyService : ISshKeyService
{
    private readonly NetworkOptimizerDbContext _db;
    private readonly ICredentialProtectionService _credentialProtection;
    private readonly ICallerContext _caller;

    /// <summary>
    /// Takes the scoped context, not the singleton factory: the scoped one routes to the site in
    /// context, while the factory is pinned to the main database. With the factory, a Site Admin on a
    /// secondary site would write their key into the main site's database and overwrite its key.
    /// </summary>
    public SshKeyService(
        NetworkOptimizerDbContext db,
        ICredentialProtectionService credentialProtection,
        ICallerContext caller)
    {
        _db = db;
        _credentialProtection = credentialProtection;
        _caller = caller;
    }

    /// <inheritdoc />
    public async Task<SshKeyInfo?> GetAsync()
    {
        var key = await _db.SshKeys.AsNoTracking().FirstOrDefaultAsync();
        return key is null ? null : ToInfo(key);
    }

    /// <inheritdoc />
    public async Task<SshKeyInfo> GenerateAsync(SshKeyType type)
    {
        var generated = SshKeyGenerator.Generate(type);
        return await StoreAsync(new SshKey
        {
            KeyType = type == SshKeyType.Ed25519 ? "ed25519" : "rsa",
            Source = "Generated",
            PublicKey = generated.PublicKey,
            PrivateKeyProtected = _credentialProtection.Encrypt(generated.PrivateKeyPem),
            Fingerprint = generated.Fingerprint,
        });
    }

    /// <inheritdoc />
    public async Task<SshKeyInfo> UploadAsync(string privateKeyPem, string? passphrase)
    {
        var (publicKey, keyType) = DerivePublicHalf(privateKeyPem, passphrase);

        return await StoreAsync(new SshKey
        {
            KeyType = keyType,
            Source = "Uploaded",
            PublicKey = publicKey,
            PrivateKeyProtected = _credentialProtection.Encrypt(privateKeyPem),
            PassphraseProtected = string.IsNullOrEmpty(passphrase)
                ? null
                : _credentialProtection.Encrypt(passphrase),
            Fingerprint = SshKeyGenerator.FingerprintOfPublicKey(publicKey) ?? "",
        });
    }

    /// <inheritdoc />
    public Task RemoveAsync() => _db.SshKeys.ExecuteDeleteAsync();

    /// <summary>
    /// Reads the uploaded key with the same type the authentication path uses, so anything we accept
    /// here is something we can actually connect with later. The public half comes out of the parsed
    /// key rather than from the user, which is why no separate validation step is needed.
    /// </summary>
    private static (string PublicKey, string KeyType) DerivePublicHalf(string privateKeyPem, string? passphrase)
    {
        PrivateKeyFile parsed;
        try
        {
            using var pem = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKeyPem));
            parsed = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(pem)
                : new PrivateKeyFile(pem, passphrase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "That does not look like a private key we can read. If it has a passphrase, enter it "
                + "as well.", ex);
        }

        var algorithm = parsed.HostKeyAlgorithms.FirstOrDefault()
            ?? throw new InvalidOperationException("That key has no usable algorithm.");

        var line = $"{algorithm.Name} {Convert.ToBase64String(algorithm.Data)} {SshKeyGenerator.DefaultComment}";
        return (line, algorithm.Name.Contains("ed25519", StringComparison.OrdinalIgnoreCase) ? "ed25519" : "rsa");
    }

    /// <summary>One key per site, so storing a new one replaces whatever was there.</summary>
    private async Task<SshKeyInfo> StoreAsync(SshKey key)
    {
        key.CreatedBy = _caller.Current?.ActorName;
        key.CreatedAt = DateTime.UtcNow;

        await _db.SshKeys.ExecuteDeleteAsync();
        _db.SshKeys.Add(key);
        await _db.SaveChangesAsync();

        return ToInfo(key);
    }

    private static SshKeyInfo ToInfo(SshKey key)
        => new(key.KeyType, key.Source, key.PublicKey, key.Fingerprint, key.CreatedAt);
}
