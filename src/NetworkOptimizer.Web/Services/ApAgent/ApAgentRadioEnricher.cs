using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Copies onto the console-sourced AP snapshots what only the AP Agent reports about a radio.
/// Today that is the operating block center, which the console omits and which is the one fact
/// that says which 320 MHz block a 6 GHz radio is in. Live state only; nothing is stored.
/// </summary>
public static class ApAgentRadioEnricher
{
    /// <summary>
    /// Sets <see cref="RadioSnapshot.CenterChannel"/> from the collector's latest radios, matched
    /// by AP MAC and radio name (falling back to the band when it holds one radio). The center is
    /// taken only while the agent and the console agree on the primary, so a channel change in
    /// flight never pairs a new primary with the old block.
    /// </summary>
    public static void Apply(IEnumerable<AccessPointSnapshot> aps, Func<string, IReadOnlyList<ApAgentRadioAirtime>> radiosFor)
    {
        foreach (var ap in aps)
        {
            if (string.IsNullOrEmpty(ap.Mac)) continue;
            var agentRadios = radiosFor(ap.Mac);
            if (agentRadios.Count == 0) continue;

            foreach (var radio in ap.Radios)
            {
                if (radio.Channel is not { } channel || (radio.ChannelWidth ?? 20) < 40) continue;
                var match = Match(agentRadios, radio);
                if (match?.CenterMhz is not { } centerMhz || match.Channel != channel) continue;
                radio.CenterChannel = ChannelSpanHelper.CenterChannelFromMhz(radio.Band, centerMhz);
            }
        }
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
