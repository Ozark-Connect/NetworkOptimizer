using System.Reflection;

namespace NetworkOptimizer.Reports;

/// <summary>
/// Branding assets embedded in this assembly, shared by every report generator so they all
/// stamp the same header. Reports built outside this project (the ISP Health export, which
/// needs models that live above this assembly) load the logo from here rather than shipping
/// a second copy.
/// </summary>
public static class ReportAssets
{
    private const string LogoResourceName = "NetworkOptimizer.Reports.Resources.logo.png";

    private static byte[]? _logoBytes;
    private static bool _logoLoaded;

    /// <summary>
    /// The report header logo, or null when the embedded resource is unavailable - callers
    /// render without it rather than failing the report.
    /// </summary>
    public static byte[]? Logo()
    {
        if (_logoLoaded) return _logoBytes;
        _logoLoaded = true;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(LogoResourceName);
            if (stream == null) return _logoBytes;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _logoBytes = ms.ToArray();
        }
        catch
        {
            _logoBytes = null;
        }
        return _logoBytes;
    }
}
