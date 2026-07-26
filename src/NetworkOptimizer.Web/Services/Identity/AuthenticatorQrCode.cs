using QRCoder;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Renders the TOTP enrolment <c>otpauth://</c> URI as a QR code for the account security page.
/// Generated on the server as a PNG data URI: nothing about the shared secret goes to a script, and
/// the page needs no client-side library.
/// </summary>
public static class AuthenticatorQrCode
{
    /// <summary>
    /// A scannable PNG data URI for <paramref name="otpAuthUri"/>. Deliberately black on white
    /// regardless of theme - a QR needs the light background and the quiet zone to scan reliably.
    /// </summary>
    public static string ToPngDataUri(string otpAuthUri)
    {
        using var generator = new QRCodeGenerator();
        // Q correction tolerates a phone camera at an angle or a partially obscured code.
        using var data = generator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }
}
