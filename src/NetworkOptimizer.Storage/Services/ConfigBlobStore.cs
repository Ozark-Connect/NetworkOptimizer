using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Content-addressed storage for Change Control snapshot collections and console backups, one
/// store per site. Content is compressed and encrypted with the site's key before it touches disk,
/// because a restorable snapshot holds every secret the UniFi Console has.
/// </summary>
public interface IConfigBlobStore
{
    /// <summary>SHA-256 of the content, lowercase hex. The blob's name.</summary>
    string HashOf(string content);

    /// <summary>Writes the content under its hash unless it is already stored; returns the hash.</summary>
    Task<string> PutAsync(string siteSlug, string content, CancellationToken cancellationToken = default);

    /// <summary>Reads a blob back, or null when it is missing.</summary>
    Task<string?> GetAsync(string siteSlug, string hash, CancellationToken cancellationToken = default);

    /// <summary>Every hash stored for a site.</summary>
    IReadOnlyList<string> ListHashes(string siteSlug);

    /// <summary>Deletes blobs by hash. Missing ones are ignored.</summary>
    void Delete(string siteSlug, IEnumerable<string> hashes);

    /// <summary>Writes a binary file (a console backup) under a name; returns its SHA-256.</summary>
    Task<string> PutFileAsync(string siteSlug, string name, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>Reads a binary file back, or null when missing.</summary>
    Task<byte[]?> GetFileAsync(string siteSlug, string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a binary file.</summary>
    void DeleteFile(string siteSlug, string name);

    /// <summary>Bytes on disk for a site's blobs and files.</summary>
    (long BlobBytes, long FileBytes) GetStorageBytes(string siteSlug);
}

/// <summary>
/// Disk implementation: <c>change-control/blobs/ab/abcd....blob</c> under the site's data folder
/// (the main data folder for the default site). Each file is the base64 of gzip output, encrypted
/// with <see cref="ICredentialProtectionService.EncryptForSite"/>.
/// </summary>
public class ConfigBlobStore : IConfigBlobStore
{
    private readonly SiteDatabasePaths _paths;
    private readonly ICredentialProtectionService _protection;
    private readonly ILogger<ConfigBlobStore> _logger;

    public ConfigBlobStore(SiteDatabasePaths paths, ICredentialProtectionService protection, ILogger<ConfigBlobStore> logger)
    {
        _paths = paths;
        _protection = protection;
        _logger = logger;
    }

    /// <inheritdoc />
    public string HashOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    /// <inheritdoc />
    public async Task<string> PutAsync(string siteSlug, string content, CancellationToken cancellationToken = default)
    {
        var hash = HashOf(content);
        var path = BlobPath(siteSlug, hash);
        if (File.Exists(path)) return hash;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sealedText = _protection.EncryptForSite(Convert.ToBase64String(Compress(Encoding.UTF8.GetBytes(content))), siteSlug);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, sealedText, cancellationToken);
        File.Move(temp, path, overwrite: true);
        return hash;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string siteSlug, string hash, CancellationToken cancellationToken = default)
    {
        var path = BlobPath(siteSlug, hash);
        if (!File.Exists(path)) return null;
        try
        {
            var plain = _protection.Decrypt(await File.ReadAllTextAsync(path, cancellationToken));
            return Encoding.UTF8.GetString(Decompress(Convert.FromBase64String(plain)));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or CryptographicException)
        {
            _logger.LogWarning(ex, "Change Control blob {Hash} for site {Site} could not be read", hash, siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListHashes(string siteSlug)
    {
        var root = Path.Combine(Root(siteSlug), "blobs");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.blob", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Select(n => n!)
            .ToList();
    }

    /// <inheritdoc />
    public void Delete(string siteSlug, IEnumerable<string> hashes)
    {
        foreach (var hash in hashes)
        {
            try { File.Delete(BlobPath(siteSlug, hash)); }
            catch (IOException ex) { _logger.LogDebug(ex, "Could not delete Change Control blob {Hash}", hash); }
        }
    }

    /// <inheritdoc />
    public async Task<string> PutFileAsync(string siteSlug, string name, byte[] content, CancellationToken cancellationToken = default)
    {
        var path = FilePath(siteSlug, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sealedText = _protection.EncryptForSite(Convert.ToBase64String(content), siteSlug);
        await File.WriteAllTextAsync(path, sealedText, cancellationToken);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetFileAsync(string siteSlug, string name, CancellationToken cancellationToken = default)
    {
        var path = FilePath(siteSlug, name);
        if (!File.Exists(path)) return null;
        return Convert.FromBase64String(_protection.Decrypt(await File.ReadAllTextAsync(path, cancellationToken)));
    }

    /// <inheritdoc />
    public void DeleteFile(string siteSlug, string name)
    {
        try { File.Delete(FilePath(siteSlug, name)); }
        catch (IOException ex) { _logger.LogDebug(ex, "Could not delete Change Control file {Name}", name); }
    }

    /// <inheritdoc />
    public (long BlobBytes, long FileBytes) GetStorageBytes(string siteSlug)
    {
        static long Sum(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
        var root = Root(siteSlug);
        return (Sum(Path.Combine(root, "blobs")), Sum(Path.Combine(root, "files")));
    }

    private string Root(string siteSlug)
    {
        var dataDir = string.Equals(siteSlug, SiteDatabasePaths.DefaultSiteSlug, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetFullPath(_paths.MainDbPath))!
            : _paths.GetSiteDataDir(siteSlug);
        return Path.Combine(dataDir, "change-control");
    }

    private string BlobPath(string siteSlug, string hash)
    {
        if (hash.Length < 8 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Not a blob hash", nameof(hash));
        return Path.Combine(Root(siteSlug), "blobs", hash[..2], hash + ".blob");
    }

    private string FilePath(string siteSlug, string name)
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrEmpty(safe) || safe != name)
            throw new ArgumentException("Not a plain file name", nameof(name));
        return Path.Combine(Root(siteSlug), "files", safe + ".enc");
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
