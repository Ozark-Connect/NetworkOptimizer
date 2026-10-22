using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>Reads the FRITZ!Box Cable web interface's docInfo JSON using an AVM session.</summary>
[VendorSpecific("AVM", "FRITZ!Box docInfo JSON; OFDM codewords excluded like Netgear/UCI")]
public sealed class FritzBoxCableProvider : ICableModemProvider
{
    public string ProviderKey => "fritzbox-cable";
    public string DisplayName => "FRITZ!Box Cable (HTTP)";
    public CmCredentialRequirement Credentials => CmCredentialRequirement.Required;
    public bool BlankUsernameMeansAdmin => false;

    private const string LoginPath = "/login_sid.lua?version=2";
    private const string FallbackModel = "FRITZ!Box Cable";
    private readonly ILogger<FritzBoxCableProvider> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    // CacheKey includes the site: configuration IDs alone are not globally unique.
    // Keep the gate with the SID so concurrent probe/poll calls cannot both sign in.
    private sealed class Session
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SessionIdentity? Identity { get; set; }
        public string? Sid { get; set; }
    }

    private sealed record SessionIdentity(Uri Origin, string Username, string PasswordHash);

    public FritzBoxCableProvider(ILogger<FritzBoxCableProvider> logger) => _logger = logger;

    public async Task<PollResult<CableModemStats>> PollAsync(
        CmPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = await FetchStatsAsync(context, cancellationToken);
            _logger.LogDebug("FRITZ!Box {Name} polled: {DsCount} DS channels, {UsCount} US channels",
                context.Name, stats.DownstreamChannels.Count, stats.UpstreamChannels.Count);
            return PollResult<CableModemStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // XML/JSON exceptions can include response fragments containing the SID. Log only the type.
            _logger.LogWarning("FRITZ!Box {Name} poll failed ({FailureType})", context.Name, ex.GetType().Name);
            return PollResult<CableModemStats>.Failed(DescribeFailure(ex, context.Host));
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        CmPollContext context, CancellationToken cancellationToken = default)
    {
        var result = await PollAsync(context, cancellationToken);
        return result.Stats is { } stats
            ? (true, $"Connected to {stats.DeviceModel} - {stats.DownstreamChannels.Count} downstream, " +
                     $"{stats.UpstreamChannels.Count} upstream channels detected")
            : (false, result.FailureReason!);
    }

    private async Task<CableModemStats> FetchStatsAsync(CmPollContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            throw new InvalidOperationException("No address is configured for this device.");
        if (string.IsNullOrWhiteSpace(context.Username) || string.IsNullOrEmpty(context.Password))
            throw new InvalidOperationException("Enter the FRITZ!Box account's username and password in Settings.");

        var port = context.Port > 0 ? context.Port : 80;
        var baseUri = new UriBuilder(port == 443 ? "https" : "http", context.Host, port).Uri;
        var path = string.IsNullOrWhiteSpace(context.StatusPagePath) ? "/data.lua" : context.StatusPagePath;
        if (!Uri.TryCreate(path, UriKind.Relative, out _) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\') || !Uri.TryCreate(baseUri, path, out var statusUri)
            || statusUri.Scheme != baseUri.Scheme || statusUri.Host != baseUri.Host || statusUri.Port != baseUri.Port)
            throw new InvalidOperationException("The status page path must be a relative path on this FRITZ!Box.");

        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = DeviceHttp.Via(context.Dialer),
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            // Like the other HTTPS modem providers, accept the device's self-signed certificate.
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        }) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };

        var session = _sessions.GetOrAdd(context.CacheKey, _ => new Session());
        var identity = new SessionIdentity(baseUri, context.Username,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Password))));
        await session.Gate.WaitAsync(ct);
        try
        {
            if (session.Identity != identity)
            {
                session.Sid = null;
                session.Identity = identity;
            }

            var document = session.Sid == null ? null
                : await ReadDocsisAsync(client, statusUri, session.Sid, ct);
            if (document == null)
            {
                session.Sid = null;
                session.Sid = await LoginAsync(client, context, ct);
                document = await ReadDocsisAsync(client, statusUri, session.Sid, ct);
                if (document == null)
                {
                    session.Sid = null;
                    throw new InvalidOperationException("FRITZ!Box did not accept the session for cable statistics. Check the account permissions.");
                }
            }

            using (document)
            {
                var stats = Parse(document.RootElement, context);
                await ReadMetadataAsync(client, stats, ct);
                return stats;
            }
        }
        finally
        {
            // Successful reads renew AVM's session. Idle sessions expire on the modem;
            // logging out here would generate a new sign-in on every scheduled poll.
            session.Gate.Release();
        }
    }

    private static async Task<string> LoginAsync(HttpClient client, CmPollContext context, CancellationToken ct)
    {
        var login = ReadSession(await client.GetStringAsync(LoginPath, ct));
        if (!int.TryParse(login.Element("BlockTime")?.Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var blockTime))
            throw new InvalidDataException("Missing login block time.");
        if (blockTime > 0)
            throw new InvalidOperationException($"FRITZ!Box sign-in is temporarily blocked. Try again in {blockTime} seconds.");

        var response = CalculateResponse(login.Element("Challenge")?.Value ?? "", context.Password!);
        login = ReadSession(await PostFormAsync(client, LoginPath, new()
        {
            ["username"] = context.Username!, ["response"] = response,
        }, ct));
        var sid = login.Element("SID")?.Value;
        if (sid is not { Length: 16 } || !sid.All(Uri.IsHexDigit) || sid == "0000000000000000")
            throw new InvalidOperationException("FRITZ!Box rejected the sign-in. Check the account username and password.");
        return sid;
    }

    private static async Task<JsonDocument?> ReadDocsisAsync(HttpClient client, Uri uri, string sid, CancellationToken ct)
    {
        try
        {
            var document = JsonDocument.Parse(await PostFormAsync(client, uri.AbsoluteUri, new()
            {
                ["xhr"] = "1", ["sid"] = sid, ["lang"] = "de", ["page"] = "docInfo", ["xhrId"] = "all",
            }, ct));
            if (Text(document.RootElement, "pid") != "login") return document;
            document.Dispose();
            if (await SessionExpiredAsync(client, sid, ct)) return null;
            throw new InvalidOperationException("FRITZ!Box did not grant access to cable statistics. Check the account permissions.");
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException
               { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.SeeOther })
        {
            // The 6690 can return HTML with HTTP 200 or redirect with HTTP 303 for an expired SID. Check the SID
            // without signing in: corrupt JSON or missing permissions must not cause login churn.
            if (await SessionExpiredAsync(client, sid, ct)) return null;
            throw;
        }
    }

    private static async Task<bool> SessionExpiredAsync(HttpClient client, string sid, CancellationToken ct)
    {
        var response = ReadSession(await PostFormAsync(client, LoginPath, new() { ["sid"] = sid }, ct));
        var returned = response.Element("SID")?.Value;
        return returned == "0000000000000000" ? true : returned == sid ? false
            : throw new InvalidDataException("Invalid AVM session validation response.");
    }

    private static async Task<string> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(path, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static XDocument ReadXml(string body)
    {
        using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000,
        });
        return XDocument.Load(reader);
    }

    private static XElement ReadSession(string body)
    {
        var root = ReadXml(body).Root;
        return root?.Name == "SessionInfo" ? root : throw new InvalidDataException("Not an AVM session response.");
    }

    internal static string CalculateResponse(string challenge, string password)
    {
        if (challenge.StartsWith("2$", StringComparison.Ordinal))
        {
            var parts = challenge.Split('$');
            if (parts.Length != 5 || !Iterations(parts[1], out var first) || !Iterations(parts[3], out var second)
                || parts[2].Length is < 2 or > 256 || parts[4].Length is < 2 or > 256)
                throw new InvalidDataException("Invalid AVM challenge.");
            var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password),
                Convert.FromHexString(parts[2]), first, HashAlgorithmName.SHA256, 32);
            hash = Rfc2898DeriveBytes.Pbkdf2(hash, Convert.FromHexString(parts[4]), second, HashAlgorithmName.SHA256, 32);
            return parts[4] + "$" + Convert.ToHexStringLower(hash);
        }

        if (challenge.Length != 8 || !challenge.All(char.IsAsciiLetterOrDigit))
            throw new InvalidDataException("Unsupported AVM challenge.");
        // AVM's legacy protocol replaces Unicode code points above U+00FF with a period.
        var legacyPassword = string.Concat(password.EnumerateRunes().Select(r => r.Value > 255 ? "." : r.ToString()));
        return challenge + "-" + Convert.ToHexStringLower(
            MD5.HashData(Encoding.Unicode.GetBytes(challenge + "-" + legacyPassword)));
    }

    private static bool Iterations(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is > 0 and <= 1_000_000;

    internal static CableModemStats Parse(JsonElement root, CmPollContext context)
    {
        if (Text(root, "pid") != "docInfo")
            throw new InvalidOperationException("FRITZ!Box did not grant access to cable statistics. Check the account permissions.");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing DOCSIS data.");
        if (Text(data, "readyState") != "ready")
            throw new InvalidOperationException("FRITZ!Box cable statistics are not ready. Try again after it has connected.");

        var stats = new CableModemStats { DeviceHost = context.Host, DeviceName = context.Name, DeviceModel = FallbackModel };
        foreach (var version in new[] { "docsis30", "docsis31" })
        {
            var ofdm = version == "docsis31";
            foreach (var row in Channels(data, "channelDs", version))
            {
                var id = ChannelId(row);
                if (id == 0) continue;
                stats.DownstreamChannels.Add(new DsChannel
                {
                    ChannelId = id, LockStatus = "Locked", // docInfo lists assigned channels, without a lock flag.
                    Modulation = ofdm ? "OFDM" : Modulation(Text(row, "modulation")),
                    Frequency = Frequency(Text(row, "frequency")),
                    Power = Number(row, "powerLevel"),
                    Snr = ofdm ? Number(row, "mer") : Number(row, "mse") is { } mse ? Math.Abs(mse) : null,
                    // Like Netgear/UCI, exclude BOTH OFDM counters from SC-QAM FEC totals.
                    Correctables = ofdm ? 0 : Counter(row, "corrErrors"),
                    Uncorrectables = ofdm ? 0 : Counter(row, "nonCorrErrors"),
                });
            }
            foreach (var row in Channels(data, "channelUs", version))
            {
                var id = ChannelId(row);
                if (id == 0) continue;
                stats.UpstreamChannels.Add(new UsChannel
                {
                    ChannelId = id, LockStatus = "Locked",
                    ChannelType = ofdm ? "OFDMA" : Text(row, "multiplex") ?? "ATDMA",
                    Modulation = Modulation(Text(row, "modulation")),
                    Frequency = Frequency(Text(row, "frequency")),
                    Power = Number(row, "powerLevel"),
                });
            }
        }
        return stats;
    }

    private static IEnumerable<JsonElement> Channels(JsonElement data, string direction, string version)
    {
        if (!data.TryGetProperty(direction, out var section) || section.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing DOCSIS channel section.");
        if (!section.TryGetProperty(version, out var rows) && version == "docsis31") yield break;
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Missing DOCSIS channel array.");
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid DOCSIS channel.");
            yield return row;
        }
    }

    private static int ChannelId(JsonElement row)
    {
        var id = checked((int)Counter(row, "channelID"));
        if (id == 0 && !string.IsNullOrEmpty(Text(row, "frequency"))
            && decimal.TryParse(Text(row, "frequency"), NumberStyles.Float, CultureInfo.InvariantCulture, out var frequency)
            && frequency == 0 && Number(row, "powerLevel") is null or 0
            && Number(row, "mse") is null or 0 && Number(row, "mer") is null or 0)
            return 0; // An unused slot has no assigned frequency or RF measurements.
        return id > 0 ? id : throw new InvalidDataException("Invalid channel ID.");
    }

    internal static long Frequency(string? text)
    {
        var parts = (text ?? "").Split(" - ", StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !decimal.TryParse(parts[0], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var start) || start <= 0)
            throw new InvalidDataException("Invalid channel frequency.");
        var center = start;
        if (parts.Length == 2)
        {
            if (!decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end) || end < start)
                throw new InvalidDataException("Invalid channel frequency band.");
            center = (start + end) / 2; // Representative spectrum position, not a reported OFDM center.
        }
        return checked((long)decimal.Round(center * 1_000_000));
    }

    private static string Modulation(string? text)
    {
        var value = (text ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        return value.EndsWith("QAM", StringComparison.Ordinal) ? "QAM" + value[..^3] : value;
    }

    private static string? Text(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null }
            : null;

    private static double? Number(JsonElement row, string key)
    {
        if (row.TryGetProperty(key, out var value)
            && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
            throw new InvalidDataException("Invalid RF measurement.");
        var text = Text(row, key);
        if (string.IsNullOrEmpty(text)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException("Invalid RF measurement.");
    }

    private static long Counter(JsonElement row, string key) =>
        long.TryParse(Text(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 0
            ? number : throw new InvalidDataException("Missing or invalid DOCSIS counter.");

    private async Task ReadMetadataAsync(HttpClient client, CableModemStats stats, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var document = ReadXml(await client.GetStringAsync("/juis_boxinfo.xml", timeout.Token));
            string? Field(string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            if (Field("Name") is { Length: > 0 } model) stats.DeviceModel = model;
            if (int.TryParse(Field("Minor"), out var major) && int.TryParse(Field("Patch"), out var minor))
                stats.FirmwareVersion = $"{major}.{minor:00}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug("FRITZ!Box display metadata unavailable ({FailureType})", ex.GetType().Name);
        }
    }

    private static string DescribeFailure(Exception ex, string host) => ex switch
    {
        InvalidOperationException => ex.Message,
        HttpRequestException or OperationCanceledException => HttpFailureSummary.Describe(ex, host),
        UriFormatException => "Check the FRITZ!Box address and port in Settings.",
        _ => HttpFailureSummary.ForResponse(null, host),
    };
}
