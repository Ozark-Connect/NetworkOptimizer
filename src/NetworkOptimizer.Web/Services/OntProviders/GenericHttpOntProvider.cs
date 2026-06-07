using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Fallback skeleton provider for ONT brands not yet supported with dedicated parsing.
/// Returns null from PollAsync (no scraping logic) but provides basic HTTP/HTTPS connectivity testing
/// with self-signed cert bypass.
/// </summary>
public class GenericHttpOntProvider : IOntProvider
{
    private readonly ILogger<GenericHttpOntProvider> _logger;

    public GenericHttpOntProvider(ILogger<GenericHttpOntProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "generic-http-ont";
    public string DisplayName => "Generic HTTP ONT";

    public Task<OntStats?> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Generic HTTP ONT provider cannot poll {Host} - needs configuration for a specific ONT model",
            context.Host);

        return Task.FromResult<OntStats?>(null);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateHttpClient();

            var port = context.Port > 0 ? context.Port : 80;
            var primaryScheme = port == 443 ? "https" : "http";
            var fallbackScheme = primaryScheme == "https" ? "http" : "https";

            var primaryUrl = BuildUrl(context.Host, port, primaryScheme);
            try
            {
                using var response = await client.GetAsync(primaryUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return (true, $"{primaryScheme.ToUpperInvariant()} {(int)response.StatusCode} - device is reachable");
                return (false, $"{primaryScheme.ToUpperInvariant()} {(int)response.StatusCode} from {context.Host}");
            }
            catch (HttpRequestException)
            {
                _logger.LogDebug("{Scheme} failed for {Host}, trying {Fallback}",
                    primaryScheme.ToUpperInvariant(), context.Host, fallbackScheme.ToUpperInvariant());
            }

            var fallbackUrl = BuildUrl(context.Host, port, fallbackScheme);
            using var fb = await client.GetAsync(fallbackUrl, cancellationToken);
            if (fb.IsSuccessStatusCode)
                return (true, $"{fallbackScheme.ToUpperInvariant()} {(int)fb.StatusCode} - device is reachable");
            return (false, $"{fallbackScheme.ToUpperInvariant()} {(int)fb.StatusCode} from {context.Host}");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    private static string BuildUrl(string host, int port, string scheme)
    {
        var portSuffix = (scheme == "http" && port == 80) || (scheme == "https" && port == 443)
            ? "" : $":{port}";
        return $"{scheme}://{host}{portSuffix}/";
    }
}
