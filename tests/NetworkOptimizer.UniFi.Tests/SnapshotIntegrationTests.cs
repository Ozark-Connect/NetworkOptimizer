using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Integration tests for the wireless rate snapshot feature.
/// Tests the full flow from captured rates through path analysis to directional rate selection.
/// </summary>
public class SnapshotIntegrationTests
{
    #region Asymmetric Wireless Client Path Tests

    [Fact]
    public void AsymmetricWirelessPath_PreservesDirectionalRates()
    {
        // Arrange - Create path with asymmetric TX/RX rates
        var path = NetworkTestData.CreateAsymmetricWirelessClientPath(
            wirelessTxRateMbps: 1200,  // ToDevice (AP transmits to client)
            wirelessRxRateMbps: 866);  // FromDevice (AP receives from client)

        // Assert - Path should have wireless connection
        path.HasWirelessConnection.Should().BeTrue();

        // Get the wireless client hop
        var clientHop = path.Hops.First(h => h.Type == HopType.WirelessClient);
        clientHop.WirelessTxRateMbps.Should().Be(1200);
        clientHop.WirelessRxRateMbps.Should().Be(866);

        // Verify IsAsymmetric detection
        var isAsymmetric = PathAnalysisResult.IsAsymmetric(
            clientHop.WirelessRxRateMbps * 1000L,  // RX in Kbps
            clientHop.WirelessTxRateMbps * 1000L); // TX in Kbps
        isAsymmetric.Should().BeTrue("28% difference between 866 and 1200 Mbps");
    }

    [Fact]
    public void SymmetricWirelessPath_NotDetectedAsAsymmetric()
    {
        // Arrange - Create path with symmetric rates
        var path = NetworkTestData.CreateWirelessClientPath(
            wirelessRateMbps: 866,
            wiredSpeedMbps: 1000);

        // Get the wireless hop
        var clientHop = path.Hops.First(h => h.IsWirelessEgress);

        // Assert - Should not be asymmetric
        var isAsymmetric = PathAnalysisResult.IsAsymmetric(
            clientHop.WirelessRxRateMbps * 1000L,
            clientHop.WirelessTxRateMbps * 1000L);
        isAsymmetric.Should().BeFalse("rates are equal");
    }

    [Theory]
    [InlineData(1000, 910, false)]  // 9% difference - at threshold
    [InlineData(1000, 909, true)]   // 9.1% difference - just over
    [InlineData(1000, 800, true)]   // 20% difference - clearly asymmetric
    [InlineData(1200, 866, true)]   // Real-world asymmetric scenario
    public void AsymmetricDetection_ThresholdBehavior(int txMbps, int rxMbps, bool expectedAsymmetric)
    {
        // Act
        var isAsymmetric = PathAnalysisResult.IsAsymmetric(rxMbps * 1000L, txMbps * 1000L);

        // Assert
        isAsymmetric.Should().Be(expectedAsymmetric);
    }

    #endregion

    #region Mesh AP Target Path Tests

    [Fact]
    public void MeshApTargetPath_GetDirectionalRates_FlipsChildPerspective()
    {
        // Arrange - Create mesh AP path with asymmetric backhaul
        // Child AP's perspective: TX=1200 (sends to parent), RX=866 (receives from parent)
        var path = NetworkTestData.CreateMeshApTargetPath(
            meshTxRateMbps: 1200,
            meshRxRateMbps: 866);

        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 400,
            FromDeviceEfficiencyPercent = 90,
            ToDeviceEfficiencyPercent = 85
        };

        // Act
        var (rxKbps, txKbps) = result.GetDirectionalRatesFromPath();

        // Assert - Should flip to match direction mapping:
        // FromDevice (RX) = child TX = 1200 Mbps
        // ToDevice (TX) = child RX = 866 Mbps
        rxKbps.Should().Be(1200_000, "FromDevice uses child's TX rate (child sends to parent)");
        txKbps.Should().Be(866_000, "ToDevice uses child's RX rate (child receives from parent)");
    }

    [Fact]
    public void MeshApTargetPath_SymmetricRates_StillReturnsValues()
    {
        // Arrange - Symmetric mesh backhaul
        var path = NetworkTestData.CreateMeshApTargetPath(
            meshTxRateMbps: 866,
            meshRxRateMbps: 866);

        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 500,
            MeasuredToDeviceMbps = 500,
            FromDeviceEfficiencyPercent = 90,
            ToDeviceEfficiencyPercent = 90
        };

        // Act
        var (rxKbps, txKbps) = result.GetDirectionalRatesFromPath();

        // Assert - Even symmetric rates should be returned
        rxKbps.Should().Be(866_000);
        txKbps.Should().Be(866_000);
    }

    [Fact]
    public void WiredApPath_GetDirectionalRates_ReturnsNull()
    {
        // Arrange - Wired AP (no wireless backhaul)
        var path = new NetworkPath
        {
            TargetIsAccessPoint = true,
            Hops = new List<NetworkHop>
            {
                new() { Type = HopType.AccessPoint },
                new() { Type = HopType.Switch }
            }
        };

        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 900,
            MeasuredToDeviceMbps = 900,
            FromDeviceEfficiencyPercent = 95,
            ToDeviceEfficiencyPercent = 95
        };

        // Act
        var (rxKbps, txKbps) = result.GetDirectionalRatesFromPath();

        // Assert - No wireless backhaul means no directional rates
        rxKbps.Should().BeNull();
        txKbps.Should().BeNull();
    }

    #endregion

    #region Snapshot Rate Selection Simulation Tests

    [Fact]
    public void SnapshotComparison_CurrentHigher_UsesCurrentRate()
    {
        // Simulate the snapshot comparison logic from BuildHopList
        // Scenario: Current rates are higher (e.g., interference cleared)
        var snapshotTx = 600_000L;
        var snapshotRx = 500_000L;
        var currentTx = 866_000L;
        var currentRx = 866_000L;

        // Act - Same logic as BuildHopList
        var finalTx = Math.Max(currentTx, snapshotTx);
        var finalRx = Math.Max(currentRx, snapshotRx);

        // Assert
        finalTx.Should().Be(866_000, "current TX is higher");
        finalRx.Should().Be(866_000, "current RX is higher");
    }

    [Fact]
    public void SnapshotComparison_SnapshotHigher_UsesSnapshotRate()
    {
        // Simulate: Rates dropped after traffic stopped (common scenario)
        var snapshotTx = 1200_000L;  // High during active traffic
        var snapshotRx = 1000_000L;
        var currentTx = 600_000L;    // Dropped after traffic
        var currentRx = 400_000L;

        // Act
        var finalTx = Math.Max(currentTx, snapshotTx);
        var finalRx = Math.Max(currentRx, snapshotRx);

        // Assert
        finalTx.Should().Be(1200_000, "snapshot TX is higher");
        finalRx.Should().Be(1000_000, "snapshot RX is higher");
    }

    [Fact]
    public void SnapshotComparison_MixedHigher_PicksBestOfEach()
    {
        // Simulate: Asymmetric scenario where each direction had different peak times
        var snapshotTx = 600_000L;   // Lower TX during snapshot (download phase)
        var snapshotRx = 1200_000L;  // Higher RX during snapshot (upload phase)
        var currentTx = 866_000L;    // Higher TX now
        var currentRx = 400_000L;    // Lower RX now

        // Act
        var finalTx = Math.Max(currentTx, snapshotTx);
        var finalRx = Math.Max(currentRx, snapshotRx);

        // Assert
        finalTx.Should().Be(866_000, "current TX is higher");
        finalRx.Should().Be(1200_000, "snapshot RX is higher");
    }

    #endregion

    #region Roaming Scenario Tests

    [Fact]
    public void ClientRoamed_SnapshotSkipped_UsesCurrentRatesOnly()
    {
        // Arrange - Client roamed between snapshot and current
        var snapshot = new WirelessRateSnapshot();
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (1200_000, 1000_000, "ap-old:mac");

        var currentApMac = "ap-new:mac";  // Different AP
        var currentTx = 600_000L;
        var currentRx = 500_000L;

        // Act - Check roaming (same logic as BuildHopList)
        snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var snapshotRates);
        var roamed = !string.IsNullOrEmpty(snapshotRates.ApMac) &&
                     !string.Equals(snapshotRates.ApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeTrue("client changed APs");

        // When roamed, snapshot should be skipped - use current rates only
        var finalTx = roamed ? currentTx : Math.Max(currentTx, snapshotRates.TxKbps);
        var finalRx = roamed ? currentRx : Math.Max(currentRx, snapshotRates.RxKbps);

        finalTx.Should().Be(currentTx, "snapshot skipped due to roaming");
        finalRx.Should().Be(currentRx, "snapshot skipped due to roaming");
    }

    [Fact]
    public void ClientNotRoamed_SnapshotUsed_PicksMaxRates()
    {
        // Arrange - Client stayed on same AP
        var snapshot = new WirelessRateSnapshot();
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (1200_000, 1000_000, "ap:mac:03");

        var currentApMac = "ap:mac:03";  // Same AP
        var currentTx = 600_000L;
        var currentRx = 800_000L;

        // Act
        snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var snapshotRates);
        var roamed = !string.IsNullOrEmpty(snapshotRates.ApMac) &&
                     !string.Equals(snapshotRates.ApMac, currentApMac, StringComparison.OrdinalIgnoreCase);

        // Assert
        roamed.Should().BeFalse("client stayed on same AP");

        var finalTx = roamed ? currentTx : Math.Max(currentTx, snapshotRates.TxKbps);
        var finalRx = roamed ? currentRx : Math.Max(currentRx, snapshotRates.RxKbps);

        finalTx.Should().Be(1200_000, "snapshot TX is higher");
        finalRx.Should().Be(1000_000, "snapshot RX is higher");
    }

    #endregion

    #region Mesh Device Snapshot Tests

    [Fact]
    public void MeshDevice_SnapshotComparison_PicksMaxRates()
    {
        // Arrange - Mesh AP snapshot captured during active backhaul traffic
        var snapshot = new WirelessRateSnapshot();
        var meshMac = "aa:bb:cc:00:00:04";
        snapshot.MeshUplinkRates[meshMac] = (1200_000, 1000_000);

        // Current rates after traffic stopped
        var currentTx = 866_000L;
        var currentRx = 600_000L;

        // Act
        snapshot.MeshUplinkRates.TryGetValue(meshMac, out var snapshotRates);
        var finalTx = Math.Max(currentTx, snapshotRates.TxKbps);
        var finalRx = Math.Max(currentRx, snapshotRates.RxKbps);

        // Assert
        finalTx.Should().Be(1200_000, "snapshot TX is higher");
        finalRx.Should().Be(1000_000, "snapshot RX is higher");
    }

    [Fact]
    public void MeshDevice_NotInSnapshot_UsesCurrentRatesOnly()
    {
        // Arrange - Mesh device not captured in snapshot (new device, or snapshot issue)
        var snapshot = new WirelessRateSnapshot();
        var meshMac = "aa:bb:cc:00:00:99";  // Not in snapshot

        // Act
        var hasSnapshot = snapshot.MeshUplinkRates.TryGetValue(meshMac, out _);

        // Assert
        hasSnapshot.Should().BeFalse();
        // Current rates (866 Mbps each direction) would be used as-is (no Math.Max comparison)
    }

    #endregion

    #region End-to-End Path Analysis Tests

    [Fact]
    public void PathAnalysis_AsymmetricWirelessClient_DetectsAsymmetry()
    {
        // Arrange - Create asymmetric path and analyze a speed test
        var path = NetworkTestData.CreateAsymmetricWirelessClientPath(
            wirelessTxRateMbps: 1200,  // ToDevice
            wirelessRxRateMbps: 600);  // FromDevice - significant asymmetry

        // Simulated test results
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 350,   // Limited by 600 Mbps RX
            MeasuredToDeviceMbps = 500,     // Better due to 1200 Mbps TX
            FromDeviceEfficiencyPercent = 58,  // 350/600
            ToDeviceEfficiencyPercent = 42    // 500/1200
        };

        // Get the client hop for analysis
        var clientHop = path.Hops.First(h => h.Type == HopType.WirelessClient);

        // Assert - IsAsymmetric should detect the 50% difference
        var isAsymmetric = PathAnalysisResult.IsAsymmetric(
            clientHop.WirelessRxRateMbps * 1000L,
            clientHop.WirelessTxRateMbps * 1000L);
        isAsymmetric.Should().BeTrue();
    }

    [Fact]
    public void PathAnalysis_MeshApTarget_CorrectDirectionalMapping()
    {
        // Arrange - Mesh AP with known asymmetric backhaul
        // Common scenario: 5 GHz backhaul where TX (child to parent) differs from RX
        var path = NetworkTestData.CreateMeshApTargetPath(
            meshTxRateMbps: 800,   // Child sends to parent at 800 Mbps
            meshRxRateMbps: 1200); // Child receives from parent at 1200 Mbps

        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = 450,
            MeasuredToDeviceMbps = 700,
            FromDeviceEfficiencyPercent = 56,  // FromDevice limited by TX (child->parent)
            ToDeviceEfficiencyPercent = 58     // ToDevice benefits from RX (parent->child)
        };

        // Act
        var (rxKbps, txKbps) = result.GetDirectionalRatesFromPath();

        // Assert - Direction mapping flips child perspective:
        // FromDevice uses child TX = 800 Mbps (data flows child->parent->server)
        // ToDevice uses child RX = 1200 Mbps (data flows server->parent->child)
        rxKbps.Should().Be(800_000, "FromDevice = child TX rate");
        txKbps.Should().Be(1200_000, "ToDevice = child RX rate");
    }

    [Fact]
    public void PathAnalysis_WiredClient_NoAsymmetricDetection()
    {
        // Arrange - Wired path (symmetric by nature)
        var path = NetworkTestData.CreateWiredClientPath(linkSpeedMbps: 1000);

        // There should be no wireless hop
        var hasWirelessHop = path.Hops.Any(h => h.IsWirelessEgress || h.IsWirelessIngress);
        hasWirelessHop.Should().BeFalse();

        // No wireless rates to check for asymmetry
        path.HasWirelessConnection.Should().BeFalse();
    }

    #endregion

    #region Snapshot with MLO Client Tests

    [Fact]
    public void MloClient_SnapshotComparison_SummedRatesCompared()
    {
        // Arrange - MLO client with multiple links
        // Snapshot captured during traffic: higher aggregated rates
        var snapshot = new WirelessRateSnapshot();
        // MLO sums all links, so snapshot stores the total
        snapshot.ClientRates["aa:bb:cc:00:01:02"] = (
            TxKbps: 4000_000,  // Sum of all link TX rates
            RxKbps: 3500_000,  // Sum of all link RX rates
            ApMac: "ap:mac:03"
        );

        // Current summed rates (may have dropped on some links)
        var currentSummedTx = 3200_000L;
        var currentSummedRx = 3000_000L;
        var currentApMac = "ap:mac:03";

        // Act - Same roaming check and max selection
        snapshot.ClientRates.TryGetValue("aa:bb:cc:00:01:02", out var snapshotRates);
        var roamed = !string.IsNullOrEmpty(snapshotRates.ApMac) &&
                     !string.Equals(snapshotRates.ApMac, currentApMac, StringComparison.OrdinalIgnoreCase);
        roamed.Should().BeFalse();

        var finalTx = Math.Max(currentSummedTx, snapshotRates.TxKbps);
        var finalRx = Math.Max(currentSummedRx, snapshotRates.RxKbps);

        // Assert - Should pick higher snapshot rates
        finalTx.Should().Be(4000_000, "snapshot summed TX is higher");
        finalRx.Should().Be(3500_000, "snapshot summed RX is higher");
    }

    #endregion
}
