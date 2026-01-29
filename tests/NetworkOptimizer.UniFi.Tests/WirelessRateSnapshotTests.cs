using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for WirelessRateSnapshot model and rate comparison logic.
/// </summary>
public class WirelessRateSnapshotTests
{
    #region WirelessRateSnapshot Model Tests

    [Fact]
    public void WirelessRateSnapshot_NewInstance_HasEmptyDictionaries()
    {
        // Act
        var snapshot = new WirelessRateSnapshot();

        // Assert
        snapshot.ClientRates.Should().NotBeNull();
        snapshot.ClientRates.Should().BeEmpty();
        snapshot.MeshUplinkRates.Should().NotBeNull();
        snapshot.MeshUplinkRates.Should().BeEmpty();
    }

    [Fact]
    public void WirelessRateSnapshot_ClientRates_CaseInsensitiveLookup()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        snapshot.ClientRates["AA:BB:CC:DD:EE:FF"] = (866000, 866000, "ap:mac:01");

        // Act & Assert - different cases should find the same entry
        snapshot.ClientRates.ContainsKey("aa:bb:cc:dd:ee:ff").Should().BeTrue();
        snapshot.ClientRates.ContainsKey("AA:BB:CC:DD:EE:FF").Should().BeTrue();
        snapshot.ClientRates.ContainsKey("Aa:Bb:Cc:Dd:Ee:Ff").Should().BeTrue();
    }

    [Fact]
    public void WirelessRateSnapshot_MeshUplinkRates_CaseInsensitiveLookup()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        snapshot.MeshUplinkRates["AA:BB:CC:DD:EE:FF"] = (866000, 866000);

        // Act & Assert - different cases should find the same entry
        snapshot.MeshUplinkRates.ContainsKey("aa:bb:cc:dd:ee:ff").Should().BeTrue();
        snapshot.MeshUplinkRates.ContainsKey("AA:BB:CC:DD:EE:FF").Should().BeTrue();
    }

    [Fact]
    public void WirelessRateSnapshot_ClientRates_StoresAllFields()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        var expectedTx = 1200000L;
        var expectedRx = 866000L;
        var expectedApMac = "aa:bb:cc:00:00:03";

        // Act
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (expectedTx, expectedRx, expectedApMac);

        // Assert
        snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var rates).Should().BeTrue();
        rates.TxKbps.Should().Be(expectedTx);
        rates.RxKbps.Should().Be(expectedRx);
        rates.ApMac.Should().Be(expectedApMac);
    }

    [Fact]
    public void WirelessRateSnapshot_ClientRates_NullApMac_Allowed()
    {
        // Arrange - AP MAC can be null for clients with incomplete data
        var snapshot = new WirelessRateSnapshot();

        // Act
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (866000, 866000, null);

        // Assert
        snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var rates).Should().BeTrue();
        rates.ApMac.Should().BeNull();
    }

    #endregion

    #region Rate Comparison Logic Tests

    [Theory]
    [InlineData(800000, 600000, 800000)]  // Current higher
    [InlineData(600000, 800000, 800000)]  // Snapshot higher
    [InlineData(866000, 866000, 866000)]  // Equal
    [InlineData(0, 866000, 866000)]       // Current zero, use snapshot
    [InlineData(866000, 0, 866000)]       // Snapshot zero, use current
    public void RateComparison_SelectsMaximum(long current, long snapshot, long expected)
    {
        // This tests the Math.Max behavior used in BuildHopList
        var result = Math.Max(current, snapshot);
        result.Should().Be(expected);
    }

    [Fact]
    public void RateComparison_IndependentForTxAndRx()
    {
        // Arrange - Simulate the snapshot comparison logic
        // Current: Tx=800, Rx=600 (asymmetric, dropped rates after traffic)
        // Snapshot: Tx=600, Rx=900 (asymmetric, captured during traffic)
        var currentTx = 800000L;
        var currentRx = 600000L;
        var snapshotTx = 600000L;
        var snapshotRx = 900000L;

        // Act - Each direction picks its own maximum
        var finalTx = Math.Max(currentTx, snapshotTx);
        var finalRx = Math.Max(currentRx, snapshotRx);

        // Assert
        finalTx.Should().Be(800000, "TX should pick higher current rate");
        finalRx.Should().Be(900000, "RX should pick higher snapshot rate");
    }

    #endregion

    #region Roaming Detection Tests

    [Fact]
    public void RoamingDetection_SameAp_ShouldUseSnapshot()
    {
        // Arrange
        var snapshotApMac = "aa:bb:cc:00:00:03";
        var currentApMac = "aa:bb:cc:00:00:03"; // Same AP

        // Act - Check if roamed (case-insensitive comparison)
        var roamed = !string.IsNullOrEmpty(snapshotApMac) &&
                     !string.Equals(snapshotApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeFalse("client did not roam - same AP");
    }

    [Fact]
    public void RoamingDetection_DifferentAp_ShouldSkipSnapshot()
    {
        // Arrange
        var snapshotApMac = "aa:bb:cc:00:00:03"; // AP during snapshot
        var currentApMac = "aa:bb:cc:00:00:04";  // Different AP now

        // Act - Check if roamed
        var roamed = !string.IsNullOrEmpty(snapshotApMac) &&
                     !string.Equals(snapshotApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeTrue("client roamed to different AP - snapshot should be skipped");
    }

    [Fact]
    public void RoamingDetection_SameApDifferentCase_ShouldUseSnapshot()
    {
        // Arrange - Same AP but different MAC case
        var snapshotApMac = "AA:BB:CC:00:00:03";
        var currentApMac = "aa:bb:cc:00:00:03";

        // Act - Check if roamed (case-insensitive)
        var roamed = !string.IsNullOrEmpty(snapshotApMac) &&
                     !string.Equals(snapshotApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeFalse("same AP with different case - should not be considered roaming");
    }

    [Fact]
    public void RoamingDetection_NullSnapshotApMac_ShouldUseSnapshot()
    {
        // Arrange - Snapshot AP MAC is null (incomplete data at snapshot time)
        string? snapshotApMac = null;
        var currentApMac = "aa:bb:cc:00:00:03";

        // Act - Check if roamed (null AP MAC means we can't detect roaming)
        var roamed = !string.IsNullOrEmpty(snapshotApMac) &&
                     !string.Equals(snapshotApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeFalse("null snapshot AP MAC - cannot detect roaming, should use snapshot");
    }

    [Fact]
    public void RoamingDetection_EmptySnapshotApMac_ShouldUseSnapshot()
    {
        // Arrange - Snapshot AP MAC is empty string
        var snapshotApMac = "";
        var currentApMac = "aa:bb:cc:00:00:03";

        // Act - Check if roamed
        var roamed = !string.IsNullOrEmpty(snapshotApMac) &&
                     !string.Equals(snapshotApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeFalse("empty snapshot AP MAC - should use snapshot");
    }

    #endregion

    #region Mesh Device Snapshot Tests

    [Fact]
    public void MeshSnapshot_ClientNotInSnapshot_UsesCurrentRates()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        var clientMac = "aa:bb:cc:00:01:02";

        // Act - Try to get snapshot rates (won't exist)
        var hasSnapshotRates = snapshot.ClientRates.TryGetValue(clientMac, out _);

        // Assert
        hasSnapshotRates.Should().BeFalse();
        // Without snapshot, current rates would be used as-is (866000 Kbps each direction)
    }

    [Fact]
    public void MeshSnapshot_DeviceInSnapshot_ComparesRates()
    {
        // Arrange - Mesh AP with rates captured during traffic
        var snapshot = new WirelessRateSnapshot();
        var meshMac = "aa:bb:cc:00:00:04";
        snapshot.MeshUplinkRates[meshMac] = (1200000, 1000000); // Tx=1200, Rx=1000 during traffic

        // Current rates after traffic (may have dropped)
        var currentTx = 800000L;
        var currentRx = 866000L;

        // Act - Get snapshot and compare
        snapshot.MeshUplinkRates.TryGetValue(meshMac, out var snapshotRates).Should().BeTrue();
        var finalTx = Math.Max(currentTx, snapshotRates.TxKbps);
        var finalRx = Math.Max(currentRx, snapshotRates.RxKbps);

        // Assert
        finalTx.Should().Be(1200000, "should use higher snapshot TX rate");
        finalRx.Should().Be(1000000, "should use higher snapshot RX rate");
    }

    [Fact]
    public void MeshSnapshot_CurrentRatesHigher_UsesCurrentRates()
    {
        // Arrange - Mesh AP where current rates are higher than snapshot
        // This can happen if wireless conditions improved after snapshot
        var snapshot = new WirelessRateSnapshot();
        var meshMac = "aa:bb:cc:00:00:04";
        snapshot.MeshUplinkRates[meshMac] = (600000, 500000); // Lower rates in snapshot

        var currentTx = 1200000L;
        var currentRx = 1000000L;

        // Act
        snapshot.MeshUplinkRates.TryGetValue(meshMac, out var snapshotRates).Should().BeTrue();
        var finalTx = Math.Max(currentTx, snapshotRates.TxKbps);
        var finalRx = Math.Max(currentRx, snapshotRates.RxKbps);

        // Assert
        finalTx.Should().Be(1200000, "should use higher current TX rate");
        finalRx.Should().Be(1000000, "should use higher current RX rate");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Snapshot_MultipleClients_IndependentRates()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        snapshot.ClientRates["client1:mac"] = (866000, 866000, "ap1:mac");
        snapshot.ClientRates["client2:mac"] = (1200000, 600000, "ap2:mac");
        snapshot.ClientRates["client3:mac"] = (2400000, 2400000, "ap1:mac");

        // Assert - Each client has independent rates
        snapshot.ClientRates.Should().HaveCount(3);
        snapshot.ClientRates["client1:mac"].TxKbps.Should().Be(866000);
        snapshot.ClientRates["client2:mac"].TxKbps.Should().Be(1200000);
        snapshot.ClientRates["client3:mac"].TxKbps.Should().Be(2400000);
    }

    [Fact]
    public void Snapshot_MultipleMeshDevices_IndependentRates()
    {
        // Arrange
        var snapshot = new WirelessRateSnapshot();
        snapshot.MeshUplinkRates["mesh1:mac"] = (866000, 866000);
        snapshot.MeshUplinkRates["mesh2:mac"] = (1200000, 600000);

        // Assert
        snapshot.MeshUplinkRates.Should().HaveCount(2);
        snapshot.MeshUplinkRates["mesh1:mac"].TxKbps.Should().Be(866000);
        snapshot.MeshUplinkRates["mesh2:mac"].TxKbps.Should().Be(1200000);
    }

    [Fact]
    public void Snapshot_ZeroRates_HandledCorrectly()
    {
        // Arrange - Zero rates can occur in edge cases
        var snapshot = new WirelessRateSnapshot();
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (0, 0, "ap:mac");

        // Act
        var hasRates = snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var rates);

        // Assert
        hasRates.Should().BeTrue();
        rates.TxKbps.Should().Be(0);
        rates.RxKbps.Should().Be(0);
    }

    [Fact]
    public void RateComparison_AsymmetricSnapshotAndCurrent_PicksBestOfEach()
    {
        // Arrange - Real-world scenario:
        // During download: High RX (AP receives from client), normal TX
        // After download: Normal RX, may have different TX
        var snapshotTx = 600000L;   // Lower TX during download (not stressed)
        var snapshotRx = 1200000L;  // High RX during download (client uploading)
        var currentTx = 866000L;    // Normal TX after traffic
        var currentRx = 400000L;    // Lower RX after traffic stopped

        // Act
        var finalTx = Math.Max(currentTx, snapshotTx);
        var finalRx = Math.Max(currentRx, snapshotRx);

        // Assert
        finalTx.Should().Be(866000, "current TX is higher");
        finalRx.Should().Be(1200000, "snapshot RX is higher (captured during active upload)");
    }

    #endregion
}
