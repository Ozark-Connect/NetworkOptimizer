using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;

namespace NetworkOptimizer.Monitoring.Tailscale;

/// <summary>A local tailscaled could not be read; the message says why, for the person setting it up.</summary>
public sealed class TailscaleLocalException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the status of the tailscaled on this host: LocalAPI <c>GET /localapi/v0/status</c> over its
/// socket (a named pipe on Windows), else the <c>tailscale status --json</c> CLI. Only that one read
/// is ever sent. The socket itself does not limit a root caller to reads.
/// </summary>
public static class TailscaleLocalApi
{
    /// <summary>LocalAPI requests must name this host; tailscaled rejects any other.</summary>
    private const string LocalApiHost = "local-tailscaled.sock";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string WindowsPipe = @"ProtectedPrefix\Administrators\Tailscale\tailscaled";

    /// <summary>
    /// The socket tailscaled listens on by default here: /var/run/tailscale/tailscaled.sock on Linux,
    /// /var/run/tailscaled.socket for the open-source tailscaled on macOS, the named pipe on Windows.
    /// </summary>
    public static string DefaultSocketPath =>
        OperatingSystem.IsWindows() ? @"\\.\pipe\" + WindowsPipe
        : OperatingSystem.IsMacOS() ? "/var/run/tailscaled.socket"
        : "/var/run/tailscale/tailscaled.sock";

    /// <summary>Reads and parses the local status.</summary>
    /// <param name="socketPath">The socket to use; null or empty for <see cref="DefaultSocketPath"/>.</param>
    public static async Task<TailscaleLocalStatus> ReadStatusAsync(string? socketPath, CancellationToken ct)
    {
        var json = await ReadStatusJsonAsync(socketPath, ct);
        try
        {
            return TailscaleStatusParser.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new TailscaleLocalException("tailscaled answered, but not with a status this version can read.", ex);
        }
    }

    private static async Task<string> ReadStatusJsonAsync(string? socketPath, CancellationToken ct)
    {
        var path = string.IsNullOrWhiteSpace(socketPath) ? DefaultSocketPath : socketPath.Trim();
        Exception? socketError;
        try
        {
            return await ReadOverSocketAsync(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            socketError = ex;
        }

        // The CLI finds tailscaled where the socket path does not reach (the macOS app has no socket).
        // Only for the default path: a configured path that fails is the answer the user needs.
        if (string.IsNullOrWhiteSpace(socketPath) && await TryCliAsync(ct) is { } cliJson)
            return cliJson;

        throw new TailscaleLocalException(socketError switch
        {
            UnauthorizedAccessException => $"Network Optimizer may not open {path}. The socket belongs to root.",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => $"tailscaled refused the status read on {path}.",
            HttpRequestException { InnerException: SocketException or IOException }
                or SocketException or IOException or FileNotFoundException or TimeoutException or TaskCanceledException =>
                $"No tailscaled answered on {path}.",
            _ => $"Could not read tailscaled on {path}: {socketError?.Message}",
        }, socketError);
    }

    private static async Task<string> ReadOverSocketAsync(string path, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    var pipe = new NamedPipeClientStream(".", PipeName(path), PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(token);
                        return pipe;
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }
                }
                if (!File.Exists(path))
                    throw new FileNotFoundException("No socket at this path.", path);
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        using var response = await client.GetAsync($"http://{LocalApiHost}/localapi/v0/status", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string PipeName(string path) =>
        path.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) ? path[@"\\.\pipe\".Length..] : path;

    private static readonly string[] CliCandidates = OperatingSystem.IsMacOS()
        ? ["tailscale", "/Applications/Tailscale.app/Contents/MacOS/Tailscale"]
        : OperatingSystem.IsWindows() ? ["tailscale", @"C:\Program Files\Tailscale\tailscale.exe"]
        : ["tailscale"];

    private static async Task<string?> TryCliAsync(CancellationToken ct)
    {
        foreach (var cli in CliCandidates)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo(cli, "status --json")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };
                if (!process.Start()) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(Timeout);
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                try
                {
                    await process.WaitForExitAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw;
                }
                await errors;
                var json = await output;
                if (process.ExitCode == 0 && json.TrimStart().StartsWith('{')) return json;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed under this name.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out; try the next name.
            }
        }
        return null;
    }
}
