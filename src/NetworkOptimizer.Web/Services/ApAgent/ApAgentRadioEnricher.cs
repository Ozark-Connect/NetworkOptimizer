using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One radio on an agent-covered AP: what the enricher did with it, for the debug log.</summary>
public sealed record ApAgentRadioCenterTrace(
    string ApName, string Radio, RadioBand Band, int Channel, int Width, int? CenterChannel, string Outcome,
    int? Utilization = null, int? SelfAirtime = null, int? Interference = null, int? NoiseFloor = null)
{
    /// <inheritdoc />
    public override string ToString()
    {
        var span = ChannelSpanHelper.GetChannelSpan(Band, Channel, Width, CenterChannel);
        var center = CenterChannel is { } c ? $"center {c}" : "no center";
        var text = $"{Radio} {Band.ToDisplayString()} ch {Channel}/{Width} {center} -> block {span.Low}-{span.High} ({Outcome})";
        if (Utilization.HasValue)
            text += $"; airtime {Utilization}% (self {SelfAirtime ?? 0}%, other {Interference ?? 0}%), floor {(NoiseFloor is { } nf ? $"{nf} dBm" : "-")}";
        return text;
    }
}

/// <summary>
/// Copies onto the console-sourced AP snapshots what only the AP Agent reports about a radio:
/// the operating block center, which the console omits and which is the one fact that says
/// which 320 MHz block a 6 GHz radio is in, and the radio's own airtime split and noise floor.
/// Live state only; nothing is stored.
/// </summary>
public static class ApAgentRadioEnricher
{
    /// <summary>A reading older than this is not copied; the console's figures stand.</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Sets <see cref="RadioSnapshot.CenterChannel"/> and the <c>Measured*</c> fields from the
    /// collector's latest radios, matched by AP MAC and radio name (falling back to the band when
    /// it holds one radio). The center is taken only while the agent and the console agree on the
    /// primary, so a channel change in flight never pairs a new primary with the old block.
    /// Returns one trace per matched radio on an agent-covered AP; an AP no agent covers
    /// contributes nothing.
    /// </summary>
    /// <param name="aps">The snapshots to enrich.</param>
    /// <param name="radiosFor">The collector's latest radios for an AP MAC.</param>
    /// <param name="hourFloorFor">The last hour's median noise floor for (AP MAC, radio name), or null.</param>
    /// <param name="now">The clock, for the freshness check; defaults to UTC now.</param>
    public static List<ApAgentRadioCenterTrace> Apply(
        IEnumerable<AccessPointSnapshot> aps,
        Func<string, IReadOnlyList<ApAgentRadioAirtime>> radiosFor,
        Func<string, string, int?>? hourFloorFor = null,
        DateTime? now = null)
    {
        var clock = now ?? DateTime.UtcNow;
        var traces = new List<ApAgentRadioCenterTrace>();
        foreach (var ap in aps)
        {
            if (string.IsNullOrEmpty(ap.Mac)) continue;
            var agentRadios = radiosFor(ap.Mac);
            if (agentRadios.Count == 0) continue;

            foreach (var radio in ap.Radios)
            {
                if (radio.Channel is not { } channel) continue;
                var match = Match(agentRadios, radio);
                if (match == null) continue;
                var width = radio.ChannelWidth ?? 20;

                if (clock - match.At <= FreshWindow)
                    CopyMeasured(radio, match, hourFloorFor?.Invoke(ap.Mac, match.Radio));

                string outcome;
                if (width < 40)
                    outcome = "narrow radio, no block to resolve";
                else if (match.CenterMhz is not { } centerMhz)
                    outcome = "agent reported no center";
                else if (match.Channel != channel)
                    outcome = $"agent still on ch {match.Channel}, waiting for it to agree";
                else if (ChannelSpanHelper.CenterChannelFromMhz(radio.Band, centerMhz) is not { } center)
                    outcome = $"center {centerMhz} MHz is off the band's grid";
                else
                {
                    radio.CenterChannel = center;
                    outcome = $"measured, {centerMhz} MHz";
                }

                traces.Add(new ApAgentRadioCenterTrace(ap.Name, radio.Name, radio.Band, channel, width, radio.CenterChannel, outcome,
                    radio.MeasuredUtilization, radio.MeasuredSelfAirtime, radio.MeasuredInterference, radio.MeasuredNoiseFloor));
            }
        }
        return traces;
    }

    /// <summary>The airtime split and floor, from the counters the collector retains.</summary>
    private static void CopyMeasured(RadioSnapshot radio, ApAgentRadioAirtime match, int? hourFloor)
    {
        if (match.Counters.TryGetValue("cu_total", out var total) && total is >= 0 and <= 100)
        {
            radio.MeasuredUtilization = (int)total;
            var selfTx = match.Counters.TryGetValue("cu_self_tx", out var tx) ? tx : 0;
            var selfRx = match.Counters.TryGetValue("cu_self_rx", out var rx) ? rx : 0;
            radio.MeasuredSelfAirtime = (int)Math.Clamp(selfTx + selfRx, 0, 100);
            radio.MeasuredInterference = match.Counters.TryGetValue("cu_interf", out var interf) ? (int)Math.Clamp(interf, 0, 100) : null;
        }
        if (match.NoiseFloor is < 0 and > -120)
            radio.MeasuredNoiseFloor = match.NoiseFloor;
        radio.MeasuredNoiseFloorHour = hourFloor;
        radio.MeasuredAt = match.At;
    }

    private static ApAgentRadioAirtime? Match(IReadOnlyList<ApAgentRadioAirtime> agentRadios, RadioSnapshot radio)
    {
        var byName = agentRadios.FirstOrDefault(r => string.Equals(r.Radio, radio.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        var token = BandToken(radio.Band);
        if (token == null) return null;
        var inBand = agentRadios.Where(r => r.Band == token).ToList();
        return inBand.Count == 1 ? inBand[0] : null;
    }

    /// <summary>The agent's band token, as the Go side's bandForRadio emits it.</summary>
    private static string? BandToken(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => "2.4",
        RadioBand.Band5GHz => "5",
        RadioBand.Band6GHz => "6",
        _ => null
    };
}
