namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A message with one phrase inside it turned into a link, split ready to render.
/// </summary>
/// <param name="Before">Text before the link.</param>
/// <param name="LinkText">The linked phrase, or null when there is no link.</param>
/// <param name="Href">Where it goes, or null when there is no link.</param>
/// <param name="After">Text after the link.</param>
public readonly record struct LinkedMessage(string? Before, string? LinkText, string? Href, string? After)
{
    /// <summary>Whether the caller should render an anchor at all.</summary>
    public bool HasLink => !string.IsNullOrEmpty(Href) && !string.IsNullOrEmpty(LinkText);
}

/// <summary>
/// Turns a phrase inside a message into a link. The caller names the phrase, which is what lets a
/// whole breadcrumb ("Settings &gt; Multi-Site") be the link rather than the bare word "Settings",
/// and lets two messages on one surface link different phrases to different places.
/// </summary>
public static class MessageLinker
{
    /// <summary>
    /// Splits <paramref name="message"/> around <paramref name="linkText"/>. Falls back to the
    /// whole message unlinked whenever a link cannot be made - no href, no phrase named, or the
    /// phrase is not in the message. The text is never dropped or altered, only divided, so a
    /// caller that gets its phrase wrong loses the link and nothing else.
    /// </summary>
    public static LinkedMessage Split(string? message, string? linkText, string? href)
    {
        if (string.IsNullOrEmpty(message)) return new LinkedMessage(message, null, null, null);
        if (string.IsNullOrEmpty(linkText) || string.IsNullOrEmpty(href))
            return new LinkedMessage(message, null, null, null);

        var index = message.IndexOf(linkText, StringComparison.Ordinal);
        if (index < 0) return new LinkedMessage(message, null, null, null);

        return new LinkedMessage(
            message[..index], linkText, href, message[(index + linkText.Length)..]);
    }
}
