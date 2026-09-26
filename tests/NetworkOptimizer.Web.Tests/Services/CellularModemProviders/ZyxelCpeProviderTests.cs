using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

/// <summary>
/// End-to-end session handling for <see cref="ZyxelCpeProvider"/> against a stub ZCFG router that
/// holds a real RSA key pair. The stub decrypts the login and encrypts its replies with its own
/// crypto code, so these tests pin the wire format rather than round-tripping the provider's.
/// </summary>
public class ZyxelCpeProviderTests
{
    private const string Password = "test-password";

    private const string CellwanObject = """
        {
          "INTF_Status": "Up",
          "INTF_Current_Access_Technology": "NR5G-NSA",
          "INTF_Network_In_Use": "Current_Test Carrier_NR5G-NSA_00101",
          "INTF_Current_Band": "LTE_BC28",
          "INTF_PhyCell_ID": 123,
          "INTF_Cell_ID": 1234567,
          "INTF_RFCN": 9410,
          "INTF_RSRP": -88,
          "INTF_RSRQ": -12,
          "INTF_SINR": 11,
          "NSA_Enable": true,
          "NSA_Band": "N78",
          "NSA_RSRP": -104,
          "NSA_RSRQ": -11,
          "NSA_SINR": 13
        }
        """;

    private const string StatusObject = """
        { "DeviceInfo": { "ProductClass": "NR7302", "SoftwareVersion": "V1.00(ABCD.1)C0" } }
        """;

    private static ModemPollContext Context(string password = Password, string? username = null, int port = 443) => new()
    {
        Id = 1,
        Name = "Test CPE",
        Host = "192.0.2.1",
        Port = port,
        Username = username,
        Password = password,
        ModemType = "Zyxel CPE",
    };

    private static ZyxelCpeProvider Create(StubRouter router) =>
        new(NullLogger<ZyxelCpeProvider>.Instance, router);

    // ----- Encrypted firmware -----

    [Fact]
    public async Task PollAsync_EncryptedFirmware_SignsInAndReadsCellwanStatus()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().BeNull();
        var stats = result.Stats!;
        stats.ModemModel.Should().Be("NR7302");
        stats.SoftwareVersion.Should().Be("V1.00(ABCD.1)C0");
        stats.Carrier.Should().Be("Test Carrier");
        stats.Lte!.Rsrp.Should().Be(-88);
        stats.Nr5g!.Rsrp.Should().Be(-104);
    }

    [Fact]
    public async Task PollAsync_EncryptedFirmware_SendsTheLoginTheWebUiSends()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());

        var login = router.LastLogin!.RootElement;
        login.GetProperty("Input_Account").GetString().Should().Be("admin");
        login.GetProperty("Input_Passwd").GetString().Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)));
        login.GetProperty("currLang").GetString().Should().Be("en");
        login.GetProperty("RememberPassword").GetInt32().Should().Be(0);
        router.LoginWasEnvelope.Should().BeTrue();
    }

    [Fact]
    public async Task PollAsync_DalCallsCarryTheSessionCookieAndKey()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());

        var dal = router.Requests.Where(r => r.Path == "/cgi-bin/DAL").ToList();
        dal.Should().NotBeEmpty();
        dal.Should().OnlyContain(r => r.Cookie != null && r.Cookie.Contains($"Session={StubRouter.SessionCookie}"));
        dal.Should().OnlyContain(r => r.Query.Contains($"sessionkey={StubRouter.SessionKey}"));
    }

    [Fact]
    public async Task PollAsync_ReusesTheSessionAndReadsDeviceInfoOnce()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());
        var second = await provider.PollAsync(Context());

        second.Stats!.ModemModel.Should().Be("NR7302");
        router.LoginCount.Should().Be(1);
        router.Requests.Count(r => r.Oid == "cellwan_status").Should().Be(2);
        router.Requests.Count(r => r.Oid == "status").Should().Be(1);
    }

    [Fact]
    public async Task PollAsync_DefaultsTheUsernameToAdmin_AndUsesASavedOne()
    {
        using var router = new StubRouter(encrypted: true, username: "operator");
        using var provider = Create(router);

        var blank = await provider.PollAsync(Context(username: "  "));
        blank.Stats.Should().BeNull();
        router.LastLogin!.RootElement.GetProperty("Input_Account").GetString().Should().Be("admin");

        var saved = await provider.PollAsync(Context(username: "operator"));
        saved.Stats.Should().NotBeNull();
    }

    // ----- Plain firmware -----

    [Theory]
    [InlineData("""{"RSAPublicKey":"None"}""")]
    [InlineData("""{"RSAPublicKey":""}""")]
    [InlineData("<html>404</html>")]
    public async Task PollAsync_PlainFirmware_SignsInWithPlainJson(string keyResponse)
    {
        using var router = new StubRouter(encrypted: false) { PublicKeyResponse = keyResponse };
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.Stats!.Lte!.Rsrp.Should().Be(-88);
        router.LoginWasEnvelope.Should().BeFalse();
        router.LastLogin!.RootElement.GetProperty("Input_Account").GetString().Should().Be("admin");
    }

    [Fact]
    public async Task PollAsync_PlainFirmwareRejectingWith401_IsARejectedPassword()
    {
        using var router = new StubRouter(encrypted: false) { LoginStatus = HttpStatusCode.Unauthorized };
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Contain("rejected the admin username or password");
    }

    // ----- Session renewal -----

    [Fact]
    public async Task PollAsync_ExpiredSession_SignsInAgain()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());
        router.ExpireSession();
        var result = await provider.PollAsync(Context());

        result.Stats.Should().NotBeNull();
        router.LoginCount.Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_ResponseUnderAnotherKey_SignsInAgain()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());
        router.ScrambleSessionKey();
        var result = await provider.PollAsync(Context());

        result.Stats.Should().NotBeNull();
        router.LoginCount.Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_SessionRefusedAfterFreshSignIn_Fails()
    {
        using var router = new StubRouter(encrypted: true) { RefuseAllDal = true };
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Contain("refused the session");
        router.LoginCount.Should().Be(2);
    }

    [Fact]
    public async Task PollAsync_StatusCallFailing_DoesNotFailThePoll()
    {
        using var router = new StubRouter(encrypted: true) { StatusResult = "ZCFG_NO_SUCH_OBJECT" };
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.Stats!.ModemModel.Should().Be("Zyxel CPE");
        router.LoginCount.Should().Be(1);
    }

    // ----- Sign-in failures -----

    [Fact]
    public async Task PollAsync_RejectedPassword_IsNotRetriedByLaterPolls()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        var first = await provider.PollAsync(Context(password: "wrong"));
        var requestsAfterFirst = router.Requests.Count;
        var second = await provider.PollAsync(Context(password: "wrong"));

        first.FailureReason.Should().Contain("rejected the admin username or password");
        second.FailureReason.Should().Contain("rejected the admin username or password");
        router.Requests.Count.Should().Be(requestsAfterFirst);
    }

    [Fact]
    public async Task PollAsync_ChangedPassword_SignsInAgain()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context(password: "wrong"));
        var result = await provider.PollAsync(Context());

        result.Stats.Should().NotBeNull();
    }

    [Fact]
    public async Task TestConnectionAsync_ClearsTheRejectedGuardAndReportsTheModel()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context(password: "wrong"));
        router.Password = "wrong";  // e.g. the password was reset on the router itself
        var (success, message) = await provider.TestConnectionAsync(Context(password: "wrong"));

        success.Should().BeTrue();
        message.Should().Be("Detected Zyxel NR7302 on Test Carrier");
    }

    [Fact]
    public async Task TestConnectionAsync_ReusesALiveSessionWithTheSameCredentials()
    {
        // A second sign-in for the same account can be refused as "Duplicated login".
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());
        var (success, _) = await provider.TestConnectionAsync(Context());

        success.Should().BeTrue();
        router.LoginCount.Should().Be(1);
    }

    [Fact]
    public async Task PollAsync_ChangedCredentials_OpenANewSession()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context());
        router.Password = "new-password";
        var result = await provider.PollAsync(Context(password: "new-password"));

        result.Stats.Should().NotBeNull();
        router.LoginCount.Should().Be(2);
        router.LastLogin!.RootElement.GetProperty("Input_Passwd").GetString()
            .Should().Be(Convert.ToBase64String(Encoding.UTF8.GetBytes("new-password")));
    }

    [Theory]
    [InlineData("Locked User", "temporarily locked the admin account")]
    [InlineData("Duplicated login", "signed in elsewhere")]
    [InlineData("Maxium number of login account has reached", "limit of signed-in sessions")]
    [InlineData("Something new", "refused the sign-in (Something new)")]
    public async Task PollAsync_OtherSignInRefusals_SayWhy_AndAreRetried(string routerResult, string expected)
    {
        using var router = new StubRouter(encrypted: true) { ForcedLoginResult = routerResult };
        using var provider = Create(router);

        var first = await provider.PollAsync(Context());
        await provider.PollAsync(Context());

        first.FailureReason.Should().Contain(expected);
        router.LoginCount.Should().Be(2, "only a wrong password pauses polling");
    }

    [Fact]
    public async Task PollAsync_NoPassword_FailsWithoutContactingTheRouter()
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        var result = await provider.PollAsync(Context(password: ""));

        result.FailureReason.Should().Contain("admin password is required");
        router.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PollAsync_HtmlLoginReply_ReportsUnreadableAnswer()
    {
        using var router = new StubRouter(encrypted: false) { LoginBodyOverride = "<html>login</html>" };
        using var provider = Create(router);

        var result = await provider.PollAsync(Context());

        result.FailureReason.Should().Be("192.0.2.1 answered, but not with stats this optimizer can read.");
    }

    [Theory]
    [InlineData(443, "https://192.0.2.1/")]
    [InlineData(0, "https://192.0.2.1/")]
    [InlineData(8443, "https://192.0.2.1:8443/")]
    [InlineData(80, "http://192.0.2.1/")]
    public async Task PollAsync_BuildsTheBaseUrlFromThePort(int port, string expectedPrefix)
    {
        using var router = new StubRouter(encrypted: true);
        using var provider = Create(router);

        await provider.PollAsync(Context(port: port));

        router.Requests.Should().OnlyContain(r => r.Url.StartsWith(expectedPrefix, StringComparison.Ordinal));
    }

    /// <summary>One request the stub received.</summary>
    private sealed record RouterRequest(string Url, string Path, string Query, string? Oid, string? Cookie);

    /// <summary>
    /// A ZCFG router: GetInfoNoLogin, getRSAPublickKey, UserLogin, and DAL, with the session
    /// cookie, session key, and (when <c>encrypted</c>) the RSA/AES envelope the web UI uses.
    /// </summary>
    private sealed class StubRouter : HttpMessageHandler
    {
        public const string SessionCookie = "abc123";
        public const string SessionKey = "987654321";

        private readonly bool _encrypted;
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly string _username;
        private byte[]? _aesKey;
        private bool _sessionValid;

        public List<RouterRequest> Requests { get; } = new();
        public JsonDocument? LastLogin { get; private set; }
        public bool LoginWasEnvelope { get; private set; }
        public int LoginCount { get; private set; }

        public string Password { get; set; } = ZyxelCpeProviderTests.Password;
        public string? PublicKeyResponse { get; init; }
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public string? LoginBodyOverride { get; init; }
        public string? ForcedLoginResult { get; init; }
        public bool RefuseAllDal { get; init; }
        public string StatusResult { get; init; } = "ZCFG_SUCCESS";

        public StubRouter(bool encrypted, string username = "admin")
        {
            _encrypted = encrypted;
            _username = username;
        }

        public void ExpireSession() => _sessionValid = false;

        public void ScrambleSessionKey() => _aesKey = RandomNumberGenerator.GetBytes(32);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            request.Headers.TryGetValues("Cookie", out var cookieValues);
            Requests.Add(new RouterRequest(uri.ToString(), uri.AbsolutePath, uri.Query, query["oid"], cookieValues?.FirstOrDefault()));

            switch (uri.AbsolutePath)
            {
                case "/GetInfoNoLogin":
                    return Reply(HttpStatusCode.OK, """{"result":"ZCFG_SUCCESS"}""", setCookie: "Session=prelogin");

                case "/getRSAPublickKey":
                    var keyBody = PublicKeyResponse ?? (_encrypted
                        ? JsonSerializer.Serialize(new { RSAPublicKey = _rsa.ExportSubjectPublicKeyInfoPem() })
                        : """{"RSAPublicKey":"None"}""");
                    return Reply(HttpStatusCode.OK, keyBody);

                case "/UserLogin":
                    return await LoginAsync(request, cancellationToken);

                case "/cgi-bin/DAL":
                    return Dal(query["oid"], query["sessionkey"], cookieValues?.FirstOrDefault());

                default:
                    return Reply(HttpStatusCode.NotFound, "{}");
            }
        }

        private async Task<HttpResponseMessage> LoginAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LoginCount++;
            if (LoginStatus != HttpStatusCode.OK)
                return Reply(LoginStatus, "{}");
            if (LoginBodyOverride != null)
                return Reply(HttpStatusCode.OK, LoginBodyOverride);

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var outer = JsonDocument.Parse(body);
            LoginWasEnvelope = outer.RootElement.TryGetProperty("key", out _);

            string loginJson;
            if (LoginWasEnvelope)
            {
                // The RSA plaintext is the Base64 text of the AES key; the IV's first 16 bytes are used.
                var wrapped = Convert.FromBase64String(outer.RootElement.GetProperty("key").GetString()!);
                var keyText = Encoding.ASCII.GetString(_rsa.Decrypt(wrapped, RSAEncryptionPadding.Pkcs1));
                _aesKey = Convert.FromBase64String(keyText);
                var iv = Convert.FromBase64String(outer.RootElement.GetProperty("iv").GetString()!);
                iv.Length.Should().Be(32);
                using var aes = Aes.Create();
                aes.Key = _aesKey;
                loginJson = Encoding.UTF8.GetString(aes.DecryptCbc(
                    Convert.FromBase64String(outer.RootElement.GetProperty("content").GetString()!),
                    iv.AsSpan(0, 16), PaddingMode.PKCS7));
            }
            else
            {
                _aesKey = null;
                loginJson = body;
            }

            LastLogin = JsonDocument.Parse(loginJson);
            var account = LastLogin.RootElement.GetProperty("Input_Account").GetString();
            var passwd = Encoding.UTF8.GetString(Convert.FromBase64String(LastLogin.RootElement.GetProperty("Input_Passwd").GetString()!));

            string reply;
            if (ForcedLoginResult != null)
                reply = JsonSerializer.Serialize(new { result = ForcedLoginResult });
            else if (account != _username || passwd != Password)
                reply = """{"result":"Invalid Username or Password"}""";
            else
            {
                _sessionValid = true;
                reply = JsonSerializer.Serialize(new { result = "ZCFG_SUCCESS", sessionkey = SessionKey });
            }

            var ok = reply.Contains("ZCFG_SUCCESS");
            return Reply(HttpStatusCode.OK, Seal(reply), setCookie: ok ? $"Session={SessionCookie}; Path=/; HttpOnly" : null);
        }

        private HttpResponseMessage Dal(string? oid, string? sessionKey, string? cookie)
        {
            if (RefuseAllDal || !_sessionValid || sessionKey != SessionKey || cookie?.Contains($"Session={SessionCookie}") != true)
                return Reply(HttpStatusCode.Unauthorized, "{}");

            var body = oid switch
            {
                "cellwan_status" => $$"""{"result":"ZCFG_SUCCESS","Object":[{{CellwanObject}}]}""",
                "status" => $$"""{"result":"{{StatusResult}}","Object":[{{StatusObject}}]}""",
                _ => """{"result":"ZCFG_NO_SUCH_OBJECT"}""",
            };
            return Reply(HttpStatusCode.OK, Seal(body));
        }

        /// <summary>Encrypt a reply under the session key, as encrypted firmware does.</summary>
        private string Seal(string json)
        {
            if (_aesKey == null)
                return json;

            var iv = RandomNumberGenerator.GetBytes(32);
            using var aes = Aes.Create();
            aes.Key = _aesKey;
            var content = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), iv.AsSpan(0, 16), PaddingMode.PKCS7);
            return JsonSerializer.Serialize(new { content = Convert.ToBase64String(content), iv = Convert.ToBase64String(iv) });
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body, string? setCookie = null)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (setCookie != null)
                response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _rsa.Dispose();
                LastLogin?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
