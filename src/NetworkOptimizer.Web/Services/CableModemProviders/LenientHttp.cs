using System.Text;
using NetworkOptimizer.Monitoring.Providers;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// A raw-socket HTTP/1.0 GET and a tolerant response parser, for modems whose web server sends
/// HTTP that .NET's strict parser rejects. The NET-DK/1.0 server (Netgear CM700, ARRIS SB6141)
/// intermittently drops the leading bytes of response header lines - "Content-Length" arrives as
/// "ntent-Length", or a stray line loses its colon - which makes HttpClient throw "Received an
/// invalid header line" even though browsers parse the same bytes fine. There is no in-framework
/// switch to relax that parsing (see dotnet/runtime#29927), so callers catch exactly that failure
/// (<see cref="IsMalformedHttpResponse"/>) and re-fetch through this reader. Compliant responses
/// never throw it, so they never reach this path.
/// </summary>
internal static class LenientHttp
{
    /// <summary>
    /// Cap on the bytes read from a raw response (the status pages are tens of KB); guards against
    /// a modem that never closes the connection.
    /// </summary>
    internal const int MaxRawResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// True when an HttpRequestException reflects a structurally malformed response that .NET
    /// could not parse into an HTTP status, rather than a normal HTTP error status or a connection
    /// failure. Gated on a null StatusCode so a real 401/404/5xx is never diverted to the raw
    /// reader, and matched on the specific parser messages so an offline modem (connection
    /// refused/timed out) isn't either.
    /// </summary>
    internal static bool IsMalformedHttpResponse(HttpRequestException ex)
    {
        if (ex.StatusCode is not null)
            return false;

        return Mentions(ex.Message) || (ex.InnerException is { } inner && Mentions(inner.Message));

        static bool Mentions(string m) =>
            m.Contains("invalid header", StringComparison.OrdinalIgnoreCase)
            || m.Contains("header line", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid status", StringComparison.OrdinalIgnoreCase)
            || m.Contains("malformed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase)
            || m.Contains("invalid chunk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Open a TCP connection and perform one HTTP/1.0 GET, returning the full raw response bytes
    /// (headers and body) read until the server closes the connection. No Accept-Encoding is sent,
    /// so the body comes back uncompressed and plain.
    /// </summary>
    internal static async Task<byte[]> RawHttpGetAsync(
        IDeviceDialer dialer, string host, int port, string pathAndQuery, string? authHeader, string? cookie,
        CancellationToken cancellationToken)
    {
        var request = new StringBuilder();
        request.Append("GET ").Append(pathAndQuery).Append(" HTTP/1.0\r\n");
        request.Append("Host: ").Append(host).Append("\r\n");
        if (authHeader != null)
            request.Append("Authorization: ").Append(authHeader).Append("\r\n");
        if (cookie != null)
            request.Append("Cookie: ").Append(cookie).Append("\r\n");
        request.Append("Accept: */*\r\n");
        request.Append("Connection: close\r\n");
        request.Append("\r\n");

        await using var stream = await dialer.DialAsync(host, port, cancellationToken);

        var reqBytes = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(reqBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxRawResponseBytes)
                break;
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A status code, leniently parsed headers, and decoded body extracted from a raw HTTP
    /// response by <see cref="ParseRawHttpResponse"/>.
    /// </summary>
    internal sealed record RawHttpResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>
    /// Parse a raw HTTP response leniently, tolerating the malformations seen from NET-DK modems:
    /// header lines whose leading bytes were dropped (a corrupted but colon-bearing name is kept
    /// as-is; a line that lost its colon entirely is skipped rather than aborting the whole
    /// response), and bare-LF line endings. The status code is taken from the first 3-digit token
    /// on the status line, so a clipped "HTTP/1.1" prefix still yields the code. The body is decoded
    /// as UTF-8; when a valid Content-Length survives it bounds the body, otherwise everything after
    /// the header separator is returned (the modem closes the connection to signal the end).
    /// </summary>
    internal static RawHttpResponse ParseRawHttpResponse(byte[] data, int length)
    {
        length = Math.Min(length, data.Length);

        var (headerEnd, bodyStart) = FindHeaderBodySplit(data, length);
        var headerText = Encoding.Latin1.GetString(data, 0, headerEnd);
        var lines = headerText.Split('\n');

        int status = 0;
        if (lines.Length > 0)
        {
            foreach (var token in lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length == 3 && int.TryParse(token, out var s) && s is >= 100 and < 600)
                {
                    status = s;
                    break;
                }
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue; // dropped-colon corruption - skip rather than reject the whole response
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0)
                continue;
            headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }

        int bodyLength = Math.Max(0, length - bodyStart);
        if (headers.TryGetValue("Content-Length", out var clRaw)
            && int.TryParse(clRaw.Trim(), out var contentLength)
            && contentLength >= 0 && contentLength <= bodyLength)
        {
            bodyLength = contentLength;
        }

        var body = Encoding.UTF8.GetString(data, bodyStart, bodyLength);
        return new RawHttpResponse(status, headers, body);
    }

    /// <summary>
    /// Locate the header/body boundary, preferring the RFC CRLFCRLF separator and falling back to
    /// the bare-LFLF some embedded servers emit. Returns the end of the header block and the start
    /// of the body; when no separator is found the whole buffer is treated as headers.
    /// </summary>
    internal static (int HeaderEnd, int BodyStart) FindHeaderBodySplit(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return (i, i + 4);
        }
        for (int i = 0; i + 1 < length; i++)
        {
            if (data[i] == '\n' && data[i + 1] == '\n')
                return (i, i + 2);
        }
        return (length, length);
    }
}
