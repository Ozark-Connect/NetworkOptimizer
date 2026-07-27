using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// The site's stored SSH keypair, used by gateway and device SSH instead of a password or a key file
/// on the server. Single row per site: one key is placed into UniFi Network's Device SSH Settings for
/// the fleet and by hand on a console gateway, so a second one has nowhere to go.
///
/// The private half is encrypted at rest with Data Protection and is never returned by any API - the
/// UI only ever hands back <see cref="PublicKey"/>. At connect time it is decrypted into a stream, so
/// it is never written to the filesystem.
/// </summary>
public class SshKey
{
    [Key]
    public int Id { get; set; }

    /// <summary>"ed25519" or "rsa". Stored as text so a direct sqlite query reads plainly.</summary>
    [Required]
    [MaxLength(20)]
    public string KeyType { get; set; } = "ed25519";

    /// <summary>"Generated" when we made it, "Uploaded" when the user brought their own.</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = "Generated";

    /// <summary>Single-line OpenSSH public key. Not a secret - this is what the user installs.</summary>
    [Required]
    [MaxLength(4000)]
    public string PublicKey { get; set; } = "";

    /// <summary>PEM private key, Data Protection encrypted.</summary>
    [Required]
    public string PrivateKeyProtected { get; set; } = "";

    /// <summary>
    /// Passphrase for an uploaded encrypted key, Data Protection encrypted. Null for generated keys,
    /// which are created without one: it would sit in the same database as the key it protects.
    /// </summary>
    public string? PassphraseProtected { get; set; }

    /// <summary>OpenSSH SHA256 fingerprint, shown so the user can match it against the device.</summary>
    [Required]
    [MaxLength(100)]
    public string Fingerprint { get; set; } = "";

    /// <summary>Username of whoever created or uploaded it, for the audit trail.</summary>
    [MaxLength(256)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
