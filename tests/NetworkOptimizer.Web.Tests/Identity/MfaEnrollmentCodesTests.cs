using FluentAssertions;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The hand-off that carries recovery codes from the enrollment endpoint to the account page across
/// the redirect. The page renders twice - prerendered over HTTP, then again on the interactive
/// circuit - and both passes read this store, so a strictly single-read window would let the
/// prerender swallow the codes and leave the visible render empty.
/// </summary>
public class MfaEnrollmentCodesTests
{
    private static readonly string[] Codes = { "aaaaa-bbbbb", "ccccc-ddddd" };

    [Fact]
    public void BothRenderPassesSeeTheSameCodes()
    {
        var store = new MfaEnrollmentCodes();
        store.Stash("user-1", Codes);

        store.Take("user-1").Should().BeEquivalentTo(Codes, "the prerender pass reads first");
        store.Take("user-1").Should().BeEquivalentTo(Codes, "the interactive pass must still see them");
    }

    [Fact]
    public void CodesAreNotHandedToAnotherUser()
    {
        var store = new MfaEnrollmentCodes();
        store.Stash("user-1", Codes);

        store.Take("user-2").Should().BeNull();
        store.Take("user-1").Should().BeEquivalentTo(Codes);
    }

    [Fact]
    public void NothingIsReturnedWhenNoEnrollmentHappened()
    {
        new MfaEnrollmentCodes().Take("user-1").Should().BeNull();
    }

    [Fact]
    public void DismissingDropsThemImmediately()
    {
        var store = new MfaEnrollmentCodes();
        store.Stash("user-1", Codes);
        store.Take("user-1").Should().NotBeNull();

        store.Discard("user-1");

        store.Take("user-1").Should().BeNull(
            "saying you saved them must beat the grace window, or a refresh would show them again");
    }

    [Fact]
    public void AFreshEnrollmentReplacesAnEarlierSet()
    {
        var store = new MfaEnrollmentCodes();
        store.Stash("user-1", Codes);
        store.Take("user-1");

        var replacement = new[] { "eeeee-fffff" };
        store.Stash("user-1", replacement);

        store.Take("user-1").Should().BeEquivalentTo(replacement,
            "re-enrolling invalidates the previous codes, so the store must not serve them again");
    }
}
