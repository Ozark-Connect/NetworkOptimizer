using Microsoft.AspNetCore.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Describes the configured password policy in one sentence, for the hint shown beside every field
/// that sets a password. Built from <see cref="PasswordOptions"/> rather than written out, so
/// changing the policy changes what users are told instead of leaving three copies to drift.
/// </summary>
public static class PasswordPolicyText
{
    public static string Describe(PasswordOptions options)
    {
        var parts = new List<string>();

        if (options.RequireDigit)
            parts.Add("a number");
        if (options.RequireLowercase)
            parts.Add("a lower-case letter");
        if (options.RequireUppercase)
            parts.Add("a capital letter");
        if (options.RequireNonAlphanumeric)
            parts.Add("a symbol");
        if (options.RequiredUniqueChars > 1)
            parts.Add($"{options.RequiredUniqueChars} different characters");

        var length = $"At least {options.RequiredLength} characters";
        return parts.Count == 0
            ? $"{length}."
            : $"{length}, including {Join(parts)}.";
    }

    private static string Join(List<string> parts) => parts.Count switch
    {
        1 => parts[0],
        2 => $"{parts[0]} and {parts[1]}",
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))}, and {parts[^1]}",
    };
}
