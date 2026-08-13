using System.Net;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Turns a failed device HTTP poll into one short line the reader can act on.
/// <para>
/// The HTTP counterpart of <see cref="Ssh.SshFailureSummary"/>, and it exists for the same
/// reason: the framework's own text names the socket or the status code, which does not tell
/// anyone whether the device is unreachable, refusing their password, or answering with
/// something we cannot read. Those are the only three they can do anything about. The original
/// text still goes to the log; this is what the UI says.
/// </para>
/// </summary>
public static class HttpFailureSummary
{
    /// <summary>
    /// Describe a transport failure - the request never produced a response.
    /// </summary>
    /// <param name="ex">The exception the request threw.</param>
    /// <param name="host">The device address, for naming what could not be reached.</param>
    public static string Describe(Exception? ex, string host)
    {
        var where = string.IsNullOrWhiteSpace(host) ? "the device" : host;

        return ex switch
        {
            TaskCanceledException or OperationCanceledException => $"{where} did not answer in time. Check connectivity or firewall rules.",
            HttpRequestException { StatusCode: { } status } => ForStatus(status, where),
            HttpRequestException http => ForTransport(http, where),
            _ => $"Could not read from {where}: {FirstLine(ex?.Message)}",
        };
    }

    /// <summary>
    /// Describe a response that arrived but was no use - a status the device should not have
    /// returned, or a body that did not parse.
    /// </summary>
    /// <param name="status">The status code returned, or null when the body was the problem.</param>
    /// <param name="host">The device address.</param>
    public static string ForResponse(HttpStatusCode? status, string host)
    {
        var where = string.IsNullOrWhiteSpace(host) ? "the device" : host;
        return status is { } code
            ? ForStatus(code, where)
            : $"{where} answered, but not with stats this optimizer can read.";
    }

    private static string ForStatus(HttpStatusCode status, string where) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            $"{where} rejected the sign-in. Check the username and password saved for it.",
        HttpStatusCode.NotFound =>
            $"{where} answered, but not on the address this model is polled at. Check the model setting.",
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
            $"{where} did not answer in time.",
        _ when (int)status >= 500 =>
            $"{where} answered with an error of its own ({(int)status}). It may be starting up or overloaded.",
        _ => $"{where} answered {(int)status}, which is not a reply this optimizer can use.",
    };

    /// <summary>
    /// A transport failure with no status: nothing answered. The inner socket error separates a
    /// host that refused the connection from one that is not there at all, which is the difference
    /// between a wrong port and a wrong address.
    /// </summary>
    private static string ForTransport(HttpRequestException ex, string where)
    {
        var socket = ex.InnerException as System.Net.Sockets.SocketException
            ?? ex.InnerException?.InnerException as System.Net.Sockets.SocketException;

        return socket?.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused =>
                $"{where} refused the connection. It is reachable, but nothing is listening on that port.",
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData =>
                $"{where} could not be resolved.",
            System.Net.Sockets.SocketError.TimedOut =>
                $"{where} did not answer in time. Check connectivity or firewall rules.",
            _ => $"Could not reach {where}.",
        };
    }

    /// <summary>
    /// The first line of a message, capped. Exception text can run to paragraphs, and the UI shows
    /// this inline.
    /// </summary>
    private static string FirstLine(string? text)
    {
        const int maxLength = 160;
        var line = (text ?? "").Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";
        return line.Length <= maxLength ? line : line[..maxLength] + "...";
    }
}
