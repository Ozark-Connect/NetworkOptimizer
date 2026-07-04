using NetworkOptimizer.Reports;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for storing and retrieving pre-generated PDF reports.
/// PDFs are stored on disk to avoid JS interop issues on mobile browsers.
/// Scoped per site: the default site uses the root directory (unchanged from
/// single-site installs), and each secondary site's PDFs live under their own
/// slug subdirectory since AuditResults.Id autoincrements independently in each
/// site's database.
/// </summary>
public class PdfStorageService
{
    private readonly ILogger<PdfStorageService> _logger;
    private readonly SiteContextService _siteContext;
    private readonly string _pdfDirectory;

    public PdfStorageService(ILogger<PdfStorageService> logger, SiteContextService siteContext)
    {
        _logger = logger;
        _siteContext = siteContext;
        _pdfDirectory = GetPdfDirectory();

        // Ensure the root directory exists; the per-site subdirectory (below) is
        // created lazily on write since the site isn't known until resolved.
        Directory.CreateDirectory(_pdfDirectory);
        _logger.LogInformation("PDF storage directory: {Directory}", _pdfDirectory);
    }

    /// <summary>
    /// This site's PDF directory. AuditResults.Id autoincrements per-site database,
    /// so two sites can produce the same audit ID - namespacing by slug keeps their
    /// PDFs from colliding. The default site keeps the original root path so PDFs
    /// generated before multi-site stay findable (same layout as floor plans);
    /// secondary sites nest under their slug.
    /// </summary>
    private string SitePdfDirectory => _siteContext.IsDefault
        ? _pdfDirectory
        : Path.Combine(_pdfDirectory, _siteContext.Slug);

    private static string GetPdfDirectory()
    {
        var isDocker = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        string baseDataPath;
        if (isDocker)
        {
            baseDataPath = "/app/data";
        }
        else if (OperatingSystem.IsWindows())
        {
            baseDataPath = Path.Combine(AppContext.BaseDirectory, "data");
        }
        else
        {
            baseDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetworkOptimizer");
        }

        return Path.Combine(baseDataPath, "report_pdfs");
    }

    /// <summary>
    /// Saves a PDF report for the given audit ID.
    /// </summary>
    public async Task SavePdfAsync(int auditId, ReportData reportData)
    {
        try
        {
            // Ensure the site's directory exists (may have been deleted, or this may
            // be the first PDF saved for this site)
            Directory.CreateDirectory(SitePdfDirectory);

            var filePath = GetPdfPath(auditId);

            _logger.LogInformation("Generating PDF for audit {AuditId} at {Path}", auditId, filePath);

            var generator = new PdfReportGenerator();
            var pdfBytes = generator.GenerateReportBytes(reportData);

            await File.WriteAllBytesAsync(filePath, pdfBytes);

            _logger.LogInformation("Saved PDF for audit {AuditId}: {Size} bytes", auditId, pdfBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save PDF for audit {AuditId}", auditId);
            throw;
        }
    }

    /// <summary>
    /// Gets the PDF bytes for the given audit ID, or null if not found.
    /// </summary>
    public async Task<byte[]?> GetPdfAsync(int auditId)
    {
        var filePath = GetPdfPath(auditId);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("PDF not found for audit {AuditId} at {Path}", auditId, filePath);
            return null;
        }

        return await File.ReadAllBytesAsync(filePath);
    }

    /// <summary>
    /// Checks if a PDF exists for the given audit ID.
    /// </summary>
    public bool PdfExists(int auditId)
    {
        return File.Exists(GetPdfPath(auditId));
    }

    /// <summary>
    /// Gets the file path for a PDF by audit ID.
    /// </summary>
    public string GetPdfPath(int auditId)
    {
        return Path.Combine(SitePdfDirectory, $"audit_{auditId}.pdf");
    }

    /// <summary>
    /// Deletes old PDFs that don't have corresponding audit records.
    /// Call this periodically to clean up orphaned files.
    /// </summary>
    public void CleanupOldPdfs(IEnumerable<int> validAuditIds)
    {
        try
        {
            // Nothing to clean up if this site has never saved a PDF yet.
            if (!Directory.Exists(SitePdfDirectory))
                return;

            var validSet = validAuditIds.ToHashSet();
            var files = Directory.GetFiles(SitePdfDirectory, "audit_*.pdf");

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("audit_") &&
                    int.TryParse(fileName.Substring(6), out var auditId) &&
                    !validSet.Contains(auditId))
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogInformation("Deleted orphaned PDF: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete orphaned PDF: {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during PDF cleanup");
        }
    }
}
