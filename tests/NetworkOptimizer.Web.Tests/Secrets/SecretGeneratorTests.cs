using FluentAssertions;
using NetworkOptimizer.Web.Services.Secrets;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Secrets;

public class SecretGeneratorTests
{
    [Fact]
    public void WordList_IsTheFullEffLongList()
    {
        SecretGenerator.Words.Should().HaveCount(7776).And.OnlyHaveUniqueItems();
        SecretGenerator.Words.Should().OnlyContain(w => w.Length >= 3 && w.Length <= 9);
    }

    [Fact]
    public void Passphrase_Default_IsSixListedWords_WithinTheWpaLimit()
    {
        var words = new HashSet<string>(SecretGenerator.Words);
        for (var i = 0; i < 200; i++)
        {
            var p = SecretGenerator.Passphrase();
            p.Length.Should().BeInRange(8, SecretGenerator.WpaMaxLength);
            // Four list words contain a hyphen (t-shirt, yo-yo), so check by rebuilding from the list.
            CanSplit(p, words, SecretGenerator.DefaultWords).Should().BeTrue(p);
        }
        SecretGenerator.PassphraseBits(SecretGenerator.DefaultWords).Should().BeApproximately(77.5, 0.1);
    }

    [Fact]
    public void Passphrase_LongestPossible_StaysWithinTheWpaLimit()
    {
        var longest = SecretGenerator.Words.Max(w => w.Length);
        (longest * SecretGenerator.DefaultWords + SecretGenerator.DefaultWords - 1).Should().BeLessThanOrEqualTo(SecretGenerator.WpaMaxLength);
    }

    [Fact]
    public void Random_Default_Is24CharactersFromTheAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var r = SecretGenerator.Random();
            r.Should().HaveLength(SecretGenerator.DefaultLength);
            r.Should().Match(s => s.All(c => SecretGenerator.RandomAlphabet.Contains(c)));
        }
        SecretGenerator.RandomAlphabet.Should().NotContainAny("0", "O", "1", "l", "I");
        SecretGenerator.RandomBits(SecretGenerator.DefaultLength).Should().BeGreaterThan(139);
    }

    [Fact]
    public void Generators_DoNotRepeat()
    {
        Enumerable.Range(0, 500).Select(_ => SecretGenerator.Random()).Should().OnlyHaveUniqueItems();
        Enumerable.Range(0, 500).Select(_ => SecretGenerator.Passphrase()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Random_UsesTheWholeAlphabet()
    {
        var seen = string.Concat(Enumerable.Range(0, 200).Select(_ => SecretGenerator.Random())).ToHashSet();
        seen.Should().HaveCount(SecretGenerator.RandomAlphabet.Length);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void Passphrase_RejectsWeakOrUnwieldyLengths(int words)
    {
        var act = () => SecretGenerator.Passphrase(words);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static bool CanSplit(string s, HashSet<string> words, int count)
    {
        if (count == 0) return s.Length == 0;
        for (var i = 1; i <= s.Length; i++)
        {
            var head = s[..i];
            if (!words.Contains(head)) continue;
            if (count == 1 ? i == s.Length : i < s.Length && s[i] == '-' && CanSplit(s[(i + 1)..], words, count - 1)) return true;
        }
        return false;
    }
}
