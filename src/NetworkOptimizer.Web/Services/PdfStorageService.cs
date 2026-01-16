using NetworkOptimizer.Reports;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for storing and retrieving pre-generated PDF reports.
/// PDFs are stored on disk to avoid JS interop issues on mobile browsers.
/// </summary>
public class PdfStorageService
{
    private readonly ILogger<PdfStorageService> _logger;
    private readonly string _pdfDirectory;

    public PdfStorageService(ILogger<PdfStorageService> logger)
    {
        _logger = logger;
        _pdfDirectory = GetPdfDirectory();

        // Ensure directory exists
        Directory.CreateDirectory(_pdfDirectory);
        _logger.LogInformation("PDF storage directory: {Directory}", _pdfDirectory);
    }

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
            // Ensure directory exists (may have been deleted)
            Directory.CreateDirectory(_pdfDirectory);

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
        return Path.Combine(_pdfDirectory, $"audit_{auditId}.pdf");
    }

    /// <summary>
    /// Deletes old PDFs that don't have corresponding audit records.
    /// Call this periodically to clean up orphaned files.
    /// </summary>
    public void CleanupOldPdfs(IEnumerable<int> validAuditIds)
    {
        try
        {
            var validSet = validAuditIds.ToHashSet();
            var files = Directory.GetFiles(_pdfDirectory, "audit_*.pdf");

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
