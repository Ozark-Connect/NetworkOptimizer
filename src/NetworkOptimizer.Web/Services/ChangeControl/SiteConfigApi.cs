using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// <see cref="IUniFiConfigApi"/> over a site's console connection. Agent sites go through the
/// existing tunnel like every other console call.
/// </summary>
public sealed class SiteConfigApi : IUniFiConfigApi
{
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);
    private readonly UniFiConnectionService _connection;

    public SiteConfigApi(UniFiConnectionService connection) => _connection = connection;

    /// <inheritdoc />
    public bool IsConnected => _connection.IsConnected && _connection.Client != null;

    /// <inheritdoc />
    public async Task<UniFiRawResponse> SendAsync(UniFiApiScope scope, HttpMethod method, string path, string? json = null,
        CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync();
        if (client == null) return new UniFiRawResponse(0, "The UniFi Console is not connected.");
        using (UniFiWriteSource.Value == null ? UniFiWriteSource.Use("change-control") : null)
            return await client.SendRawAsync(scope, method, path, json, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(int Status, byte[] Bytes)> GetBytesAsync(UniFiApiScope scope, string path, CancellationToken cancellationToken = default)
    {
        var client = await ClientAsync();
        return client == null ? (0, []) : await client.GetRawBytesAsync(scope, path, cancellationToken);
    }

    private async Task<UniFiApiClient?> ClientAsync()
    {
        if (_connection.Client is { } client) return client;
        await _connection.WaitForConnectionAsync(ConnectWait);
        return _connection.Client;
    }
}
