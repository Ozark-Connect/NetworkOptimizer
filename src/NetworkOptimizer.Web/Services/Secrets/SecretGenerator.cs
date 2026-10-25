using System.Reflection;
using System.Security.Cryptography;

namespace NetworkOptimizer.Web.Services.Secrets;

/// <summary>The kinds of value <see cref="SecretGenerator"/> makes.</summary>
public enum SecretKind
{
    /// <summary>Words from the EFF long wordlist, hyphen-separated (Wi-Fi passphrases).</summary>
    Passphrase,

    /// <summary>Letters and digits with look-alike characters removed (RADIUS secrets, PPSKs, keys).</summary>
    Random,
}

/// <summary>
/// Generates secrets from the OS CSPRNG (<see cref="RandomNumberGenerator.GetInt32(int)"/>, which is
/// unbiased). Strength comes only from the random choice, never from the shape of the output, so the
/// entropy figures below are exact.
/// </summary>
public static class SecretGenerator
{
    /// <summary>Default passphrase length: 6 words is ~77.5 bits, enough for offline WPA2 guessing.</summary>
    public const int DefaultWords = 6;

    /// <summary>Default random length: 24 characters from <see cref="RandomAlphabet"/> is ~140 bits.</summary>
    public const int DefaultLength = 24;

    /// <summary>WPA2/WPA3-Personal passphrases are 8 to 63 ASCII characters.</summary>
    public const int WpaMaxLength = 63;

    /// <summary>A-Z, a-z, 2-9 without O, I, and l, so a value can be read aloud or typed from paper.</summary>
    public const string RandomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private static readonly Lazy<string[]> WordList = new(LoadWords);

    /// <summary>The passphrase word list (EFF long wordlist, 7,776 words).</summary>
    public static IReadOnlyList<string> Words => WordList.Value;

    /// <summary>A passphrase of <paramref name="words"/> words joined by <paramref name="separator"/>.</summary>
    public static string Passphrase(int words = DefaultWords, char separator = '-')
    {
        if (words is < 4 or > 10) throw new ArgumentOutOfRangeException(nameof(words), "Use 4 to 10 words.");
        var list = WordList.Value;
        return string.Join(separator, Enumerable.Range(0, words).Select(_ => list[RandomNumberGenerator.GetInt32(list.Length)]));
    }

    /// <summary>A random string of <paramref name="length"/> characters from <see cref="RandomAlphabet"/>.</summary>
    public static string Random(int length = DefaultLength)
    {
        if (length is < 12 or > 128) throw new ArgumentOutOfRangeException(nameof(length), "Use 12 to 128 characters.");
        return RandomNumberGenerator.GetString(RandomAlphabet, length);
    }

    /// <summary>A value of the given kind at its default strength.</summary>
    public static string Generate(SecretKind kind) => kind switch
    {
        SecretKind.Passphrase => Passphrase(),
        SecretKind.Random => Random(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Entropy in bits of a passphrase of <paramref name="words"/> words.</summary>
    public static double PassphraseBits(int words) => words * Math.Log2(WordList.Value.Length);

    /// <summary>Entropy in bits of a random value of <paramref name="length"/> characters.</summary>
    public static double RandomBits(int length) => length * Math.Log2(RandomAlphabet.Length);

    private static string[] LoadWords()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NetworkOptimizer.Web.Resources.Secrets.eff_large_wordlist.txt")
            ?? throw new InvalidOperationException("The passphrase word list is missing from the build.");
        using var reader = new StreamReader(stream);
        var words = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The entropy figures assume exactly this list; a truncated or duplicated list would overstate them.
        if (words.Length != 7776 || words.Distinct(StringComparer.Ordinal).Count() != words.Length)
            throw new InvalidOperationException($"The passphrase word list has {words.Length} words; expected 7,776 unique.");
        return words;
    }
}
