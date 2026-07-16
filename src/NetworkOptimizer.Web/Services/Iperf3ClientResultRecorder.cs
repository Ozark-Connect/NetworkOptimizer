using System.Text.Json;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Parses a completed iperf3 server-side <c>-J</c> JSON result - a client-initiated LAN test that a
/// managed <c>iperf3 -s</c> captured - and records it via the given site's
/// <see cref="ClientSpeedTestService"/>. Shared by the central server's <see cref="Iperf3ServerService"/>
/// (default site, parsing its own iperf3 stdout) and the agent-relay endpoint (secondary sites,
/// parsing the JSON the agent forwards) so client-initiated iperf3 results are stored identically
/// everywhere. This is distinct from the NO-initiated LAN test (Iperf3SpeedTestService), which the
/// server orchestrates and stores separately.
/// </summary>
public static class Iperf3ClientResultRecorder
{
    /// <summary>
    /// Parses <paramref name="json"/> and records the client-initiated iperf3 result against
    /// <paramref name="clientSpeedTest"/>'s site. Swallows parse/record errors (logged) so a bad
    /// result never tears down the caller.
    /// </summary>
    public static async Task RecordAsync(ClientSpeedTestService clientSpeedTest, string json, ILogger logger)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for errors
            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString();
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    logger.LogDebug("iperf3 test error: {Error}", errorMsg);
                    return;
                }
            }

            // Extract client IP and server's local IP from connection info
            string? clientIp = null;
            string? serverLocalIp = null;
            if (root.TryGetProperty("start", out var start) &&
                start.TryGetProperty("connected", out var connected) &&
                connected.GetArrayLength() > 0)
            {
                var firstConn = connected[0];
                if (firstConn.TryGetProperty("remote_host", out var remoteHost))
                    clientIp = NetworkUtilities.NormalizeToIPv4String(remoteHost.GetString());
                if (firstConn.TryGetProperty("local_host", out var localHost))
                    serverLocalIp = localHost.GetString();
            }

            if (string.IsNullOrEmpty(clientIp))
            {
                logger.LogWarning("Could not extract client IP from iperf3 result");
                return;
            }

            // Extract test parameters
            int durationSeconds = 10;
            int parallelStreams = 1;
            if (root.TryGetProperty("start", out var startInfo) &&
                startInfo.TryGetProperty("test_start", out var testStart))
            {
                if (testStart.TryGetProperty("duration", out var dur))
                    durationSeconds = dur.GetInt32();
                if (testStart.TryGetProperty("num_streams", out var streams))
                    parallelStreams = streams.GetInt32();
            }

            // Parse end results - from SERVER perspective:
            // sum_received = data server received FROM client = "From Device"
            // sum_sent = data server sent TO client = "To Device"
            //
            // For bidir tests, _bidir_reverse fields carry the second direction.
            // Prefer sum_received variants for bps (accurate goodput from receiver's
            // measurement). Retransmits come from sum_sent (sender tracks them).
            double fromDeviceBps = 0;
            double toDeviceBps = 0;
            long fromDeviceBytes = 0;
            long toDeviceBytes = 0;
            int? fromDeviceRetransmits = null;
            int? toDeviceRetransmits = null;

            if (root.TryGetProperty("end", out var end))
            {
                // From Device: sum_received = server received from client (goodput)
                if (end.TryGetProperty("sum_received", out var sumReceived))
                {
                    fromDeviceBps = sumReceived.GetProperty("bits_per_second").GetDouble();
                    if (sumReceived.TryGetProperty("bytes", out var bytes))
                        fromDeviceBytes = bytes.GetInt64();
                    if (sumReceived.TryGetProperty("retransmits", out var rt))
                        fromDeviceRetransmits = rt.GetInt32();
                }

                // To Device: sum_sent = server sent to client
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    toDeviceBps = sumSent.GetProperty("bits_per_second").GetDouble();
                    if (sumSent.TryGetProperty("bytes", out var bytes))
                        toDeviceBytes = bytes.GetInt64();
                    if (sumSent.TryGetProperty("retransmits", out var rt))
                        toDeviceRetransmits = rt.GetInt32();
                }

                // Bidir: to-device from _bidir_reverse fields
                // Server-side JSON only has sum_sent_bidir_reverse (sender's view);
                // sum_received_bidir_reverse is zero on the server side.
                if (end.TryGetProperty("sum_sent_bidir_reverse", out var sumSentReverse))
                {
                    var reverseBps = sumSentReverse.GetProperty("bits_per_second").GetDouble();
                    if (reverseBps > 0)
                    {
                        toDeviceBps = reverseBps;
                        if (sumSentReverse.TryGetProperty("bytes", out var bytes))
                            toDeviceBytes = bytes.GetInt64();
                        if (sumSentReverse.TryGetProperty("retransmits", out var rt))
                            toDeviceRetransmits = rt.GetInt32();
                    }
                }
            }

            // Only record if we got meaningful data
            if (fromDeviceBps > 0 || toDeviceBps > 0)
            {
                await clientSpeedTest.RecordIperf3ClientResultAsync(
                    clientIp,
                    fromDeviceBps,   // DownloadBitsPerSecond = From Device
                    toDeviceBps,     // UploadBitsPerSecond = To Device
                    fromDeviceBytes, // DownloadBytes = From Device
                    toDeviceBytes,   // UploadBytes = To Device
                    fromDeviceRetransmits,
                    toDeviceRetransmits,
                    durationSeconds,
                    parallelStreams,
                    json,
                    serverLocalIp);  // Actual server interface IP from iperf3

                logger.LogInformation(
                    "Recorded iperf3 client test from {ClientIp}: From Device {FromDevice:F1} Mbps, To Device {ToDevice:F1} Mbps",
                    clientIp, fromDeviceBps / 1_000_000, toDeviceBps / 1_000_000);
            }
            else
            {
                logger.LogDebug("iperf3 test from {ClientIp} had no measurable data", clientIp);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse iperf3 server JSON output");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing iperf3 server test result");
        }
    }
}
