namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

/// <summary>
/// Lends a test's stub handler to a provider that builds and disposes a client per poll, so the
/// stub and its recorded state outlive each poll.
/// </summary>
internal sealed class BorrowedHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _invoker.SendAsync(request, cancellationToken);
}
