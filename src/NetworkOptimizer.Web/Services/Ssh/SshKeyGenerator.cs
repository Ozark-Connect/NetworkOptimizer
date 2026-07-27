using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>The key algorithms a generated keypair can use.</summary>
public enum SshKeyType
{
    /// <summary>The default. Small, fast, and what a key created today should be.</summary>
    Ed25519 = 0,

    /// <summary>
    /// The fallback for older firmware. Dropbear only gained Ed25519 in 2020, and there is UniFi
    /// switch and AP firmware in the field older than that.
    /// </summary>
    Rsa4096 = 1,
}

/// <summary>A freshly generated keypair. The private half is the caller's to protect immediately.</summary>
/// <param name="Type">Algorithm the pair was generated with.</param>
/// <param name="PrivateKeyPem">PEM-armored private key, in the format SSH.NET's PrivateKeyFile parses.</param>
/// <param name="PublicKey">Single-line OpenSSH public key ("ssh-ed25519 AAAA... comment").</param>
/// <param name="Fingerprint">OpenSSH SHA256 fingerprint of the public half ("SHA256:...").</param>
public sealed record GeneratedSshKey(SshKeyType Type, string PrivateKeyPem, string PublicKey, string Fingerprint);

/// <summary>
/// Generates SSH keypairs for the site's stored key. Generation happens here so the private half never
/// crosses the wire: callers protect it and hand back only <see cref="GeneratedSshKey.PublicKey"/>.
///
/// BouncyCastle rather than the BCL because net10.0 has no standalone Ed25519 primitive (it appears only
/// as a component of Composite ML-DSA), and SSH.NET does not generate keys.
/// </summary>
public static class SshKeyGenerator
{
    /// <summary>Comment appended to generated public keys, so they are recognizable in authorized_keys.</summary>
    public const string DefaultComment = "network-optimizer";

    /// <summary>Generates a keypair of the requested type.</summary>
    public static GeneratedSshKey Generate(SshKeyType type, string comment = DefaultComment)
    {
        var pair = type switch
        {
            SshKeyType.Ed25519 => GenerateEd25519(),
            SshKeyType.Rsa4096 => GenerateRsa(4096),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown SSH key type"),
        };

        var publicBlob = OpenSshPublicKeyUtilities.EncodePublicKey(pair.Public);
        var privateDer = OpenSshPrivateKeyUtilities.EncodePrivateKey(pair.Private);

        return new GeneratedSshKey(
            type,
            Pem(PemLabel(type), privateDer),
            $"{KeyTypeName(type)} {Convert.ToBase64String(publicBlob)} {comment}".TrimEnd(),
            FingerprintOf(publicBlob));
    }

    /// <summary>
    /// OpenSSH SHA256 fingerprint of a single-line public key ("ssh-ed25519 AAAA... comment"), for
    /// displaying an uploaded key the same way a generated one is displayed. Null when the line does
    /// not carry a decodable base64 blob.
    /// </summary>
    public static string? FingerprintOfPublicKey(string publicKeyLine)
    {
        var parts = publicKeyLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 2 })
            return null;

        try
        {
            return FingerprintOf(Convert.FromBase64String(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static AsymmetricCipherKeyPair GenerateEd25519()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        return generator.GenerateKeyPair();
    }

    private static AsymmetricCipherKeyPair GenerateRsa(int strength)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(
            BigInteger.ValueOf(0x10001), new SecureRandom(), strength, 100));
        return generator.GenerateKeyPair();
    }

    /// <summary>SHA256 over the raw public key blob, base64, minus the padding - the ssh-keygen format.</summary>
    private static string FingerprintOf(byte[] publicBlob)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

    private static string KeyTypeName(SshKeyType type) => type switch
    {
        SshKeyType.Ed25519 => "ssh-ed25519",
        SshKeyType.Rsa4096 => "ssh-rsa",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown SSH key type"),
    };

    /// <summary>
    /// BouncyCastle encodes Ed25519 private keys as the openssh-key-v1 container and RSA as PKCS#1,
    /// so the armor label differs by algorithm. SSH.NET reads both.
    /// </summary>
    private static string PemLabel(SshKeyType type) => type switch
    {
        SshKeyType.Ed25519 => "OPENSSH PRIVATE KEY",
        SshKeyType.Rsa4096 => "RSA PRIVATE KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown SSH key type"),
    };

    private static string Pem(string label, byte[] der)
    {
        var builder = new StringBuilder();
        builder.Append("-----BEGIN ").Append(label).Append("-----\n");
        var base64 = Convert.ToBase64String(der);
        for (var offset = 0; offset < base64.Length; offset += 70)
            builder.Append(base64, offset, Math.Min(70, base64.Length - offset)).Append('\n');
        builder.Append("-----END ").Append(label).Append("-----\n");
        return builder.ToString();
    }
}
