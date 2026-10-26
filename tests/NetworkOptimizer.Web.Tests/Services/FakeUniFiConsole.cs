using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// A loopback HTTPS stand-in for a UniFi OS console's sign-in. <c>GET /login</c> answers 200 so the
/// client takes the UniFi OS endpoint, and <c>POST /api/auth/login</c> answers <see cref="LoginStatus"/>.
/// <see cref="LoginAttempts"/> counts the sign-ins that actually reached it.
/// </summary>
internal sealed class FakeUniFiConsole : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private int _loginAttempts;

    public FakeUniFiConsole(HttpStatusCode loginStatus)
    {
        LoginStatus = loginStatus;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=console.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // Windows SChannel cannot serve TLS from an ephemeral key; a PFX round trip persists it.
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        _certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        _listener.Start();
        _accept = AcceptAsync();
    }

    public HttpStatusCode LoginStatus { get; set; }

    public int LoginAttempts => Volatile.Read(ref _loginAttempts);

    public string Host => $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                var (method, path) = await ReadRequestAsync(tls);
                var status = (method, path) switch
                {
                    ("GET", "/login") => HttpStatusCode.OK,
                    ("POST", "/api/auth/login") => CountLogin(),
                    _ => HttpStatusCode.NotFound,
                };
                var response = $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{{}}";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                await tls.FlushAsync(_stop.Token);
            }
            catch
            {
                // A client that hangs up mid-request is not this stub's concern.
            }
        }
    }

    private HttpStatusCode CountLogin()
    {
        Interlocked.Increment(ref _loginAttempts);
        return LoginStatus;
    }

    /// <summary>Reads the request line and headers, then drains a Content-Length body.</summary>
    private async Task<(string Method, string Path)> ReadRequestAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
                break;
            buffer.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var length = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l["Content-Length:".Length..].Trim()))
            .FirstOrDefault();
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), _stop.Token);
            if (n == 0) break;
            read += n;
        }

        return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "");
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; } catch { }
        _certificate.Dispose();
        _stop.Dispose();
    }
}
