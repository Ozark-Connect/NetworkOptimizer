using System.Text.Json;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Thrown when the UniFi Console returns a non-JSON body (typically an HTML error or login
/// page served while the console is rebooting or mid-firmware-upgrade) for an API call that
/// expects JSON. Derives from <see cref="JsonException"/> so existing catch blocks and retry
/// predicates keyed on <see cref="JsonException"/> behave exactly as before; only the message
/// becomes a concise, single-line diagnosis instead of a raw parser error.
/// </summary>
public class UniFiNonJsonResponseException : JsonException
{
    private const int PreviewLength = 120;

    public UniFiNonJsonResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Builds the exception with a single-line message describing the response:
    /// status code, content type, and the start of the body with whitespace flattened.
    /// </summary>
    public static UniFiNonJsonResponseException Create(
        int statusCode, string? contentType, string? body, JsonException inner)
    {
        var preview = body ?? string.Empty;
        if (preview.Length > PreviewLength)
            preview = preview[..PreviewLength] + "...";
        preview = preview.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return new UniFiNonJsonResponseException(
            $"UniFi Console returned non-JSON content (status {statusCode}, content-type {contentType ?? "unknown"}) - " +
            $"it may be rebooting or mid-firmware-upgrade. Body starts with: {preview}",
            inner);
    }
}
