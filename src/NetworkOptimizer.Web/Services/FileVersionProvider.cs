using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.FileProviders;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provides file versioning for static assets (CSS, JS) to enable cache busting.
/// Appends a version query string based on the file's last modified time.
/// </summary>
public class FileVersionProvider : IFileVersionProvider
{
    private readonly IFileProvider _fileProvider;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public FileVersionProvider(IWebHostEnvironment env)
    {
        _fileProvider = env.WebRootFileProvider;
    }

    public string AddFileVersionToPath(PathString requestPathBase, string path)
    {
        // Normalize the path
        var normalizedPath = path.TrimStart('/');
        var cacheKey = $"{requestPathBase}:{normalizedPath}";

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var fileInfo = _fileProvider.GetFileInfo(normalizedPath);
        if (!fileInfo.Exists)
        {
            // File doesn't exist, return original path
            _cache[cacheKey] = path;
            return path;
        }

        // Use last modified time as version (converted to Unix timestamp for brevity)
        var version = fileInfo.LastModified.ToUnixTimeSeconds().ToString("x");
        var separator = path.Contains('?') ? "&" : "?";
        var versionedPath = $"{path}{separator}v={version}";

        _cache[cacheKey] = versionedPath;
        return versionedPath;
    }
}
