using System.Security.Cryptography;
using System.Text;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Signs a request to an AP Agent so the token proves the caller without travelling.
///
/// The agent serves plain HTTP on the management LAN, and the membership tier polls every three
/// seconds per access point: as a bearer token that is a password on the wire thousands of times a
/// day, and anything that reads it can steer or ban a client. The MAC covers method, path,
/// timestamp, nonce and body, so a captured request cannot be replayed or edited either.
/// </summary>
public static class ApAgentRequestSigner
{
    /// <summary>The Authorization header value for one request.</summary>
    public static string Sign(string token, string method, string path, string? jsonBody)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return $"HMAC ts={ts},nonce={nonce},sig={Signature(token, method, path, ts, nonce, jsonBody)}";
    }

    /// <summary>
    /// The MAC itself. The agent builds this same string in Go, so the two must agree byte for
    /// byte - a shared vector pins it on both sides.
    /// </summary>
    public static string Signature(string token, string method, string path, string ts, string nonce, string? jsonBody)
    {
        var body = jsonBody is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(jsonBody);
        var canonical = string.Join('\n', method, path, ts, nonce,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());

        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }
}
