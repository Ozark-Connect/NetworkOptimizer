using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Persists each mesh child's backhaul PHY as a wifi_client point keyed by its base MAC, so
/// playback can scrub the maps' Link speed the way it scrubs a client's connection. The
/// historic resolver routes these to the DEVICE node - a mesh AP is not a client. Shared by
/// the directly-monitored fast tier and the agent-relayed path so both sites record
/// identically.
/// </summary>
public static class MeshBackhaulPhyRecorder
{
    public static void Record(IReadOnlyList<UniFiDeviceResponse> devices, MonitoringInfluxClient influx, DateTime now)
    {
        var claims = UniFiDiscovery.BuildMeshParentByChild(devices);
        foreach (var dev in devices)
        {
            if (string.IsNullOrEmpty(dev.Mac)) continue;
            if (!string.Equals(dev.Uplink?.Type, "wireless", StringComparison.OrdinalIgnoreCase)) continue;

            long txKbps = dev.Uplink?.TxRate ?? 0;
            long rxKbps = dev.Uplink?.RxRate ?? 0;
            var parentMac = dev.Uplink?.UplinkMac;
            var bandCode = dev.Uplink?.RadioBand;
            if (claims.TryGetValue(dev.Mac, out var meshClaim) && !string.IsNullOrEmpty(meshClaim.ParentMac))
            {
                if (meshClaim.Contradicts(dev.Uplink?.UplinkMac))
                {
                    // The child's own uplink block is stale; the parent's claim is the link.
                    // Claim rates are the parent's perspective, so they map inverted.
                    parentMac = meshClaim.ParentMac;
                    txKbps = meshClaim.RxRateKbps;
                    rxKbps = meshClaim.TxRateKbps;
                    bandCode ??= meshClaim.Links.Count > 0 ? meshClaim.Links[0].Radio : null;
                }
                else if (meshClaim.IsMlo)
                {
                    txKbps = Math.Max(txKbps, meshClaim.RxRateKbps);
                    rxKbps = Math.Max(rxKbps, meshClaim.TxRateKbps);
                }
            }
            // A radio code MapBand doesn't know (a UDB Pro's 60 GHz, say) must not skip the
            // write: "unknown" keeps the PHY scrubbable, and the reader normalizes it to null
            // rather than fabricating a band.
            var band = MonitoringCollectionAgent.MapBand(bandCode);
            if (string.IsNullOrEmpty(band)) band = "unknown";
            if ((txKbps <= 0 && rxKbps <= 0) || string.IsNullOrEmpty(parentMac)) continue;

            _ = influx.WriteWifiClientThroughputAsync(
                apMac: parentMac,
                band: band,
                clientMac: dev.Mac,
                txThroughputBps: null,
                rxThroughputBps: null,
                signalDbm: dev.Uplink?.Signal,
                timestamp: now,
                txRateKbps: txKbps > 0 ? txKbps : null,
                rxRateKbps: rxKbps > 0 ? rxKbps : null);
        }
    }
}
