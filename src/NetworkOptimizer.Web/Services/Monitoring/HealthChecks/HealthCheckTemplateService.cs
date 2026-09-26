using System.Text.Json;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// Loads the shipped health check templates from <c>wwwroot/data/health-checks/*.json</c>, the
/// way tours are loaded: adding a template is a file. A malformed file is skipped, never fatal.
/// </summary>
public class HealthCheckTemplateService
{
    private readonly ILogger<HealthCheckTemplateService> _logger;
    private readonly object _loadLock = new();
    private List<HealthCheckTemplate>? _templates;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public HealthCheckTemplateService(ILogger<HealthCheckTemplateService> logger)
    {
        _logger = logger;
    }

    /// <summary>Every valid template, in file order.</summary>
    public IReadOnlyList<HealthCheckTemplate> GetTemplates()
    {
        EnsureLoaded();
        return _templates!;
    }

    /// <summary>One template by id, or null.</summary>
    public HealthCheckTemplate? GetTemplate(string? id) =>
        string.IsNullOrEmpty(id) ? null
            : GetTemplates().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    private void EnsureLoaded()
    {
        if (_templates != null) return;
        lock (_loadLock)
        {
            if (_templates != null) return;

            var loaded = new List<HealthCheckTemplate>();
            var dir = FindDirectory();
            if (dir != null)
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var template = JsonSerializer.Deserialize<HealthCheckTemplate>(File.ReadAllText(file), JsonOptions);
                        if (template == null || string.IsNullOrWhiteSpace(template.Id)
                            || string.IsNullOrWhiteSpace(template.Command)
                            || !HealthCheckEvaluation.FieldNamePattern.IsMatch(template.FieldName))
                        {
                            _logger.LogWarning("Skipping health check template {File}: missing id, command, or a valid fieldName", file);
                            continue;
                        }
                        loaded.Add(template);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping health check template {File}", file);
                    }
                }
            }

            _templates = loaded;
        }
    }

    private static string? FindDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "health-checks"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data", "health-checks"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
