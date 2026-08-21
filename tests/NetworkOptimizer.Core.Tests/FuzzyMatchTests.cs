using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("Cable Modem Monitoring", "cable")]
    [InlineData("Cable Modem Monitoring", "modem monitoring")]
    [InlineData("Cable Modem Monitoring", "monitoring cable")]
    [InlineData("External Speed Test Servers", "extspd")]
    [InlineData("Managed SSH Key (Optional)", "ssh key")]
    [InlineData("UniFi Console (Controller) Connection", "controller")]
    public void Matches_what_a_user_would_type(string candidate, string query) =>
        FuzzyMatch.Score(candidate, query).Should().BeGreaterThanOrEqualTo(FuzzyMatch.MinimumUsefulScore);

    [Fact]
    public void A_word_the_candidate_does_not_contain_at_all_scores_nothing() =>
        FuzzyMatch.Score("Cable Modem Monitoring", "starlink").Should().Be(0);

    [Fact]
    public void Every_word_of_a_multi_word_query_has_to_land() =>
        FuzzyMatch.Score("Cable Modem Monitoring", "cable starlink").Should().Be(0);

    [Fact]
    public void A_scattered_subsequence_falls_below_the_useful_threshold()
    {
        // Every letter of "sen" is in "Speed Test Settings" in order, which is exactly the false
        // positive the run and word-start bonuses exist to keep out of the results.
        FuzzyMatch.Score("Speed Test Settings", "sen")
            .Should().BeLessThan(FuzzyMatch.MinimumUsefulScore);
    }

    [Fact]
    public void A_short_term_buried_inside_a_longer_word_is_not_a_useful_match()
    {
        // "ont" really is inside "Controller". It is still not the card anyone means by it.
        FuzzyMatch.Score("UniFi Console (Controller) Connection", "ont")
            .Should().BeLessThan(FuzzyMatch.MinimumUsefulScore);
    }

    [Fact]
    public void A_word_start_wins_over_an_earlier_match_inside_a_word() =>
        FuzzyMatch.Score("Front ONT Panel", "ont")
            .Should().BeGreaterThan(FuzzyMatch.Score("Front Panel Only", "ont"));

    [Fact]
    public void The_card_actually_named_that_outranks_one_that_merely_contains_the_letters()
    {
        var ont = FuzzyMatch.Score("ONT Device Monitoring", "ont");
        var connection = FuzzyMatch.Score("UniFi Console (Controller) Connection", "ont");
        ont.Should().BeGreaterThan(connection);
    }

    [Fact]
    public void A_word_start_beats_the_same_text_mid_word()
    {
        var wordStart = FuzzyMatch.Score("Managed SSH Key", "key");
        var midWord = FuzzyMatch.Score("Monkey Business", "key");
        wordStart.Should().BeGreaterThan(midWord);
    }

    [Fact]
    public void A_shorter_title_wins_when_both_contain_the_query()
    {
        var exact = FuzzyMatch.Score("Users", "users");
        var longer = FuzzyMatch.Score("Users and Groups Administration Console", "users");
        exact.Should().BeGreaterThanOrEqualTo(longer);
    }

    [Theory]
    [InlineData(null, "query")]
    [InlineData("candidate", null)]
    [InlineData("candidate", "")]
    [InlineData("", "query")]
    [InlineData("candidate", "   ")]
    public void Nothing_to_match_scores_nothing(string? candidate, string? query) =>
        FuzzyMatch.Score(candidate, query).Should().Be(0);

    [Fact]
    public void Case_does_not_matter() =>
        FuzzyMatch.Score("Adaptive SQM Monitor", "sqm")
            .Should().Be(FuzzyMatch.Score("adaptive sqm monitor", "SQM"));

    [Fact]
    public void ScoreBest_takes_the_strongest_candidate()
    {
        string?[] candidates = ["Something Else", "Cable Modem", null];
        FuzzyMatch.ScoreBest(candidates, "cable modem")
            .Should().Be(FuzzyMatch.Score("Cable Modem", "cable modem"));
    }
}
