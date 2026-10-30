using System.Runtime.InteropServices;
using Microsoft.Playwright;
using Xunit;

namespace NetworkOptimizer.Web.BrowserTests;

/// <summary>
/// Finds a browser to drive: the system Google Chrome first (GitHub's ubuntu-latest runner and
/// most dev boxes have it, so nothing is downloaded), else Playwright's own Chromium if it was
/// installed with <c>playwright.ps1 install chromium</c>.
/// </summary>
public static class BrowserLocator
{
    /// <summary>The Playwright channel to launch: "chrome", "" for the bundled Chromium, or null for none.</summary>
    public static readonly string? Channel = Find();

    private static string? Find()
    {
        if (ChromePaths().Any(File.Exists)) return "chrome";
        return BundledChromiumInstalled() ? "" : null;
    }

    private static IEnumerable<string> ChromePaths()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
            {
                var dir = Environment.GetEnvironmentVariable(root);
                if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, "Google", "Chrome", "Application", "chrome.exe");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
        else
        {
            yield return "/opt/google/chrome/chrome";
            yield return "/usr/bin/google-chrome";
        }
    }

    private static bool BundledChromiumInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") switch
        {
            { Length: > 0 } custom => custom,
            _ when RuntimeInformation.IsOSPlatform(OSPlatform.Windows) =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright"),
            _ when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => Path.Combine(home, "Library", "Caches", "ms-playwright"),
            _ => Path.Combine(home, ".cache", "ms-playwright"),
        };
        return Directory.Exists(dir) && Directory.EnumerateDirectories(dir, "chromium-*").Any();
    }
}

/// <summary>A test that needs a browser; skipped, not failed, where none is installed.</summary>
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute()
    {
        if (BrowserLocator.Channel == null)
            Skip = "No browser: install Google Chrome, or run playwright.ps1 install chromium.";
    }
}

/// <summary>One browser for the whole collection; each test opens its own page.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    /// <summary>The launched browser, null where none is installed (every test is skipped then).</summary>
    public IBrowser? Browser { get; private set; }

    /// <summary>The Web project's wwwroot, served to the test pages.</summary>
    public string WebRoot { get; } = FindWebRoot();

    public async Task InitializeAsync()
    {
        if (BrowserLocator.Channel == null) return;
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = BrowserLocator.Channel.Length > 0 ? BrowserLocator.Channel : null,
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser != null) await Browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private static string FindWebRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var root = Path.Combine(dir.FullName, "src", "NetworkOptimizer.Web", "wwwroot");
            if (Directory.Exists(root)) return root;
        }
        throw new DirectoryNotFoundException("src/NetworkOptimizer.Web/wwwroot not found above the test output.");
    }
}

[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "Browser";
}
