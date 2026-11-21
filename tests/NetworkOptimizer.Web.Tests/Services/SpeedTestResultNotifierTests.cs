using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class SpeedTestResultNotifierTests
{
    [Fact]
    public void NotifyResultSaved_PassesSlugAndKind()
    {
        var notifier = new SpeedTestResultNotifier();
        (string Slug, SpeedTestResultKind Kind)? seen = null;
        notifier.ResultSaved += (slug, kind) => seen = (slug, kind);

        notifier.NotifyResultSaved("site-a", SpeedTestResultKind.Wan);

        seen.Should().Be(("site-a", SpeedTestResultKind.Wan));
    }

    [Fact]
    public void NotifyResultSaved_ThrowingSubscriber_DoesNotFailCallerOrSkipOthers()
    {
        var notifier = new SpeedTestResultNotifier();
        var reached = false;
        notifier.ResultSaved += (_, _) => throw new InvalidOperationException("subscriber failed");
        notifier.ResultSaved += (_, _) => reached = true;

        var act = () => notifier.NotifyResultSaved("default", SpeedTestResultKind.Lan);

        act.Should().NotThrow();
        reached.Should().BeTrue();
    }

    [Fact]
    public void NotifyResultSaved_NoSubscribers_DoesNothing()
    {
        var act = () => new SpeedTestResultNotifier().NotifyResultSaved("default", SpeedTestResultKind.Lan);

        act.Should().NotThrow();
    }
}
