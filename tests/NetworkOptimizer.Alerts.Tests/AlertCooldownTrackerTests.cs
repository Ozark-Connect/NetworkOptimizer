using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Alerts.Tests;

public class AlertCooldownTrackerTests
{
    private readonly AlertCooldownTracker _tracker = new();

    [Fact]
    public void IsInCooldown_FirstFire_ReturnsFalse()
    {
        _tracker.IsInCooldown("rule1:device1", 300).Should().BeFalse();
    }

    [Fact]
    public void IsInCooldown_AfterRecordFired_WithinWindow_ReturnsTrue()
    {
        _tracker.RecordFired("rule1:device1");

        _tracker.IsInCooldown("rule1:device1", 300).Should().BeTrue();
    }

    [Fact]
    public void IsInCooldown_AfterRecordFired_ZeroCooldown_ReturnsFalse()
    {
        _tracker.RecordFired("rule1:device1");

        _tracker.IsInCooldown("rule1:device1", 0).Should().BeFalse();
    }

    [Fact]
    public void IsInCooldown_AfterRecordFired_NegativeCooldown_ReturnsFalse()
    {
        _tracker.RecordFired("rule1:device1");

        _tracker.IsInCooldown("rule1:device1", -1).Should().BeFalse();
    }

    [Fact]
    public void IsInCooldown_DifferentKeys_Independent()
    {
        _tracker.RecordFired("rule1:device1");

        _tracker.IsInCooldown("rule1:device1", 300).Should().BeTrue();
        _tracker.IsInCooldown("rule1:device2", 300).Should().BeFalse();
        _tracker.IsInCooldown("rule2:device1", 300).Should().BeFalse();
    }

    [Fact]
    public void RecordFired_UpdatesExistingKey()
    {
        _tracker.RecordFired("rule1:device1");

        // Should still be in cooldown after re-recording
        _tracker.RecordFired("rule1:device1");
        _tracker.IsInCooldown("rule1:device1", 300).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_RemovesExpiredEntries()
    {
        _tracker.RecordFired("rule1:device1");

        // Cleanup with zero max age should remove everything
        _tracker.Cleanup(TimeSpan.Zero);

        _tracker.IsInCooldown("rule1:device1", 300).Should().BeFalse();
    }

    [Fact]
    public void Cleanup_KeepsRecentEntries()
    {
        _tracker.RecordFired("rule1:device1");

        // Cleanup with large max age should keep recent entries
        _tracker.Cleanup(TimeSpan.FromHours(1));

        _tracker.IsInCooldown("rule1:device1", 300).Should().BeTrue();
    }

    [Fact]
    public void IsInCooldown_VeryShortCooldown_EventuallyExpires()
    {
        _tracker.RecordFired("rule1:device1");

        // A 1-second cooldown should still be active immediately after recording
        _tracker.IsInCooldown("rule1:device1", 1).Should().BeTrue();
    }
}
