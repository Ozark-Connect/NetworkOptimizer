using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Shared string manipulation utilities
/// </summary>
public static partial class StringUtilities
{
    /// <summary>
    /// Whether a value is a valid site slug as produced by <see cref="ToSlug"/>:
    /// lowercase alphanumeric with inner hyphens, at most 64 characters.
    /// </summary>
    public static bool IsSlug(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && SlugRegex().IsMatch(value);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SlugRegex();

    /// <summary>
    /// Converts a display name to a URL-safe kebab-case slug: lowercase ASCII letters,
    /// digits, and single hyphens. Diacritics are stripped (e.g. "Café" becomes "cafe"),
    /// any other character becomes a hyphen, and runs of hyphens collapse to one.
    /// Returns "site" if nothing usable remains.
    /// </summary>
    /// <param name="name">User-entered display name</param>
    /// <param name="maxLength">Maximum slug length; the slug is cut at this length then trimmed of trailing hyphens</param>
    public static string ToSlug(string? name, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "site";

        var normalized = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        var lastWasHyphen = true;

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(lower);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > maxLength)
            slug = slug[..maxLength].TrimEnd('-');

        return slug.Length == 0 ? "site" : slug;
    }

    /// <summary>
    /// Splits plain text into text and http(s) URL segments, in order, so a renderer can link
    /// the URLs. Trailing sentence punctuation is left out of a URL, and so is a closing
    /// parenthesis the URL did not open ("(see https://x/y)." links "https://x/y").
    /// </summary>
    public static IReadOnlyList<(string Text, bool IsUrl)> SplitLinks(string? text)
    {
        var segments = new List<(string, bool)>();
        if (string.IsNullOrEmpty(text))
            return segments;

        var pos = 0;
        foreach (Match m in UrlRegex().Matches(text))
        {
            var url = m.Value;
            while (url.Length > 0)
            {
                var last = url[^1];
                if (".,;:!?'\"".Contains(last) || (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')')))
                    url = url[..^1];
                else
                    break;
            }
            if (url.Length <= "https://".Length)
                continue;

            if (m.Index > pos)
                segments.Add((text[pos..m.Index], false));
            segments.Add((url, true));
            pos = m.Index + url.Length;
        }
        if (pos < text.Length)
            segments.Add((text[pos..], false));
        return segments;
    }

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
