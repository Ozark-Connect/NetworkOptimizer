using System.Text.Json;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Loads tour definitions from wwwroot/data/tours/*.json. Singleton with lazy
/// double-checked loading; a missing directory or a malformed file degrades to
/// fewer tours, never an error.
/// </summary>
public class TourDefinitionService
{
    private readonly ILogger<TourDefinitionService> _logger;
    private readonly object _loadLock = new();
    private List<TourDefinition>? _tours;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TourDefinitionService(ILogger<TourDefinitionService> logger)
    {
        _logger = logger;
    }

    /// <summary>All valid tours, ordered by release version ascending.</summary>
    public IReadOnlyList<TourDefinition> GetTours()
    {
        EnsureLoaded();
        return _tours!;
    }

    public TourDefinition? GetTour(string id) =>
        GetTours().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The version tour eligibility is judged against. Source builds have no release
    /// version, so they use the newest tour shipped in the build - a test site always
    /// sees the content it carries.
    /// </summary>
    public Version CurrentEffectiveVersion()
    {
        // Gate on IsSourceBuild, not on ReleaseVersion being parseable: a git-checkout
        // source build carries the NEXT version with prerelease height (2.3.2-alpha...),
        // and judging tours against that base would hide the tour it was built to test.
        if (!AppVersionInfo.IsSourceBuild && TourVersions.Parse(AppVersionInfo.ReleaseVersion) is { } release)
            return release;
        return GetTours().Select(t => t.ParsedVersion).Where(v => v != null).Max() ?? new Version(0, 0, 0);
    }

    private void EnsureLoaded()
    {
        if (_tours != null) return;
        lock (_loadLock)
        {
            if (_tours != null) return;

            var loaded = new List<TourDefinition>();
            var dir = FindToursDirectory();
            if (dir == null)
            {
                _logger.LogDebug("No tours directory found; guided tours unavailable");
                _tours = loaded;
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var tour = JsonSerializer.Deserialize<TourDefinition>(File.ReadAllText(file), JsonOptions);
                    if (tour == null || string.IsNullOrWhiteSpace(tour.Id) || tour.Steps.Count == 0)
                    {
                        _logger.LogWarning("Skipping tour file {File}: missing id or steps", file);
                        continue;
                    }
                    if (tour.ParsedVersion == null)
                    {
                        _logger.LogWarning("Skipping tour {Id}: no parseable version", tour.Id);
                        continue;
                    }
                    if (tour.Steps.Any(s => string.IsNullOrWhiteSpace(s.Id)))
                    {
                        _logger.LogWarning("Skipping tour {Id}: a step has no id", tour.Id);
                        continue;
                    }
                    loaded.Add(tour);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unreadable tour file {File}", file);
                }
            }

            _tours = loaded
                .OrderBy(t => t.ParsedVersion)
                .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _logger.LogInformation("Loaded {Count} guided tour(s) from {Dir}", _tours.Count, dir);
        }
    }

    private static string? FindToursDirectory()
    {
        // wwwroot/data first (deployed), then the working directory for development
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "tours"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data", "tours"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
