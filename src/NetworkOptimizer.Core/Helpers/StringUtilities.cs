using System.Globalization;
using System.Text;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Shared string manipulation utilities
/// </summary>
public static class StringUtilities
{
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
}
