using System.Text;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A failed LAN speed test as the page shows it: one readable line, plus the raw iperf3 output
/// when there is any to show.
/// </summary>
/// <param name="Cause">The readable cause, e.g. "unable to connect to server (Connection refused)".</param>
/// <param name="RawOutput">Both directions' raw output, or null when the message carries none.</param>
public sealed record SpeedTestFailure(string Cause, string? RawOutput)
{
    /// <summary>The line shown in a device's result row.</summary>
    public string Summary => "Speed test failed: " + Cause;
}

/// <summary>
/// Turns an <c>Iperf3Result.ErrorMessage</c> into a <see cref="SpeedTestFailure"/>. The both-directions
/// message embeds each direction's iperf3 <c>-J</c> output; the summary takes each JSON <c>error</c> field.
/// </summary>
public static class SpeedTestFailureSummary
{
    private const string BothFailedPrefix = "Both tests failed. Download: ";
    private const string UploadSeparator = ", Upload: ";

    /// <summary>
    /// Builds the summary for <paramref name="errorMessage"/>. Identical errors in both directions
    /// collapse to one. Anything that does not parse falls back to the message unchanged.
    /// </summary>
    public static SpeedTestFailure Summarize(string? errorMessage)
    {
        var message = errorMessage?.Trim() ?? "";
        if (!message.StartsWith(BothFailedPrefix, StringComparison.Ordinal))
            return new SpeedTestFailure(message, null);

        try
        {
            var rest = message[BothFailedPrefix.Length..];
            if (!TrySplit(rest, out var download, out var upload))
                return new SpeedTestFailure(message, null);

            var downloadError = ErrorOf(download);
            var uploadError = ErrorOf(upload);
            var raw = $"Download: {download}\n\nUpload: {upload}";

            string line;
            if (downloadError == null && uploadError == null)
                return new SpeedTestFailure(message, null);
            if (downloadError == null || uploadError == null
                || string.Equals(downloadError, uploadError, StringComparison.OrdinalIgnoreCase))
                line = downloadError ?? uploadError!;
            else
                line = $"From device: {downloadError}; To device: {uploadError}";

            return new SpeedTestFailure(line, raw);
        }
        catch (Exception)
        {
            return new SpeedTestFailure(message, null);
        }
    }

    /// <summary>
    /// Shortens an iperf3 error to its cause: "unable to connect to server - server may have
    /// stopped running ...: Connection refused" becomes "unable to connect to server (Connection refused)".
    /// </summary>
    public static string Shorten(string error)
    {
        var text = error.Trim();
        if (text.StartsWith("error - ", StringComparison.OrdinalIgnoreCase))
            text = text["error - ".Length..];

        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0) return text;

        var head = text[..dash];
        var colon = text.LastIndexOf(": ", StringComparison.Ordinal);
        return colon > dash ? $"{head} ({text[(colon + 2)..].Trim()})" : head;
    }

    /// <summary>
    /// Splits "A, Upload: B". A JSON download output is read to its closing brace first, because
    /// the JSON itself may contain the separator text.
    /// </summary>
    private static bool TrySplit(string rest, out string download, out string upload)
    {
        download = upload = "";
        if (rest.StartsWith('{'))
        {
            var bytes = Encoding.UTF8.GetBytes(rest);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true });
            if (reader.Read() && reader.TrySkip())
            {
                var end = Encoding.UTF8.GetCharCount(bytes, 0, (int)reader.BytesConsumed);
                if (rest.AsSpan(end).StartsWith(UploadSeparator, StringComparison.Ordinal))
                {
                    download = rest[..end].Trim();
                    upload = rest[(end + UploadSeparator.Length)..].Trim();
                    return true;
                }
            }
        }

        var index = rest.LastIndexOf(UploadSeparator, StringComparison.Ordinal);
        if (index < 0) return false;
        download = rest[..index].Trim();
        upload = rest[(index + UploadSeparator.Length)..].Trim();
        return true;
    }

    /// <summary>The direction's error: the JSON <c>error</c> field, else the first line of plain text.</summary>
    private static string? ErrorOf(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        if (output.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(output);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(error.GetString()))
                    return Shorten(error.GetString()!);
                return null;
            }
            catch (JsonException)
            {
                // Truncated or non-JSON output: show its first line as text below.
            }
        }

        var firstLine = output.Split('\n', 2)[0].Trim();
        return firstLine.Length == 0 ? null : Shorten(firstLine);
    }
}
