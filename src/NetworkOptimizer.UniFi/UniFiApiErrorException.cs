namespace NetworkOptimizer.UniFi;

/// <summary>
/// Thrown when the UniFi Console refuses a write with an error code in <c>meta.msg</c> (for
/// example <c>api.err.DuplicateFixedIP</c>), for callers that opt in to see why.
/// </summary>
public class UniFiApiErrorException : Exception
{
    /// <summary>The Console's error code, such as <c>api.err.InvalidFixedIP</c>.</summary>
    public string Code { get; }

    /// <summary>The HTTP status the Console answered with.</summary>
    public int StatusCode { get; }

    /// <param name="code">The Console's error code.</param>
    /// <param name="statusCode">The HTTP status.</param>
    public UniFiApiErrorException(string code, int statusCode)
        : base($"UniFi Network refused the request ({code}).")
    {
        Code = code;
        StatusCode = statusCode;
    }
}
