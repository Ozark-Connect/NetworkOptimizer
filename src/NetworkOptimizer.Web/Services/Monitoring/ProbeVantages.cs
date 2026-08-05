namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// One place a Network Tools probe can be run from, as offered in the vantage picker.
/// </summary>
/// <param name="Key">Picker value: "server", "agent:{id}", or "agent:{id}:{vantageId}".</param>
/// <param name="Label">What the user reads.</param>
/// <param name="AgentId">The agent to run on, or null for the server vantage.</param>
/// <param name="SourceBind">
/// What this vantage's probes bind to on the way out - its WAN vantage's interface or source IP.
/// Null when the vantage probes on its own route, which is every agent with no vantage.
/// </param>
public sealed record ProbeVantageOption(string Key, string Label, int? AgentId, string? SourceBind);

/// <summary>
/// One WAN vantage an agent probes for. An agent can hold several, and each one binds differently,
/// so each is its own place to probe from rather than a detail of the agent.
/// </summary>
/// <param name="VantageId">Row id of the WAN vantage, which makes the picker key unique.</param>
/// <param name="Name">The vantage's name, used when the console cannot name its WAN.</param>
/// <param name="WanLabel">Its WAN in UniFi's own wording ("Yelcot Cable WAN4"), if known.</param>
/// <param name="SourceBind">The interface or source IP its probes leave by.</param>
public sealed record ProbeVantageBinding(int VantageId, string Name, string? WanLabel, string? SourceBind);

/// <summary>
/// What is known about one of a site's connected agents while the vantage list is built.
/// </summary>
/// <param name="AgentId">Registry id of the agent.</param>
/// <param name="Name">Agent name as enrolled.</param>
/// <param name="OnGateway">Whether this agent's own address is one of the gateway's.</param>
/// <param name="Vantages">The WAN vantages assigned to this agent, if any.</param>
public sealed record ProbeVantageAgent(
    int AgentId,
    string Name,
    bool OnGateway,
    IReadOnlyList<ProbeVantageBinding> Vantages);

/// <summary>
/// Builds the Network Tools vantage list: where a probe can originate on this site, and what
/// each origin binds its probes to.
/// </summary>
public static class ProbeVantages
{
    /// <summary>Picker value for the server vantage - the value Network Tools has always used.</summary>
    public const string ServerKey = "server";

    /// <summary>
    /// The probe origins worth offering a choice between. Returns an EMPTY list when the site has
    /// at most one, which is every site with a single probe origin: the page then shows the single
    /// origin it always has, with no picker chrome and nothing new to read.
    ///
    /// An agent holding several WAN vantages contributes ONE ENTRY PER VANTAGE, because each binds
    /// its probes to a different interface or address - they are different places to probe from,
    /// not one place described several ways. Listed per agent instead, something had to choose one
    /// of the bindings, and a probe run "from" that agent left by whichever vantage happened to
    /// sort first.
    ///
    /// An agent running on the gateway is listed as its own origin even though the gateway is also
    /// offered as an SSH vantage. That is deliberate: same box, two different execution paths
    /// (UniFi SSH versus the agent binary), and a disagreement between them is what separates an
    /// agent-side binding or environment problem from a network one. They are labeled so the
    /// relationship is visible, never collapsed into one entry.
    /// </summary>
    /// <param name="serverProbesSite">Whether this server itself probes the site (false when its agent does).</param>
    /// <param name="serverLabel">Existing label for the server vantage, unchanged by this list.</param>
    /// <param name="agents">The site's connected agents.</param>
    public static List<ProbeVantageOption> ForPicker(
        bool serverProbesSite,
        string serverLabel,
        IEnumerable<ProbeVantageAgent> agents)
    {
        var options = new List<ProbeVantageOption>();
        if (serverProbesSite)
            options.Add(new ProbeVantageOption(ServerKey, serverLabel, null, null));

        foreach (var agent in agents.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (agent.Vantages.Count == 0)
            {
                options.Add(new ProbeVantageOption(
                    $"agent:{agent.AgentId}", LabelFor(agent, null), agent.AgentId, null));
                continue;
            }

            foreach (var vantage in agent.Vantages.OrderBy(v => v.WanLabel ?? v.Name, StringComparer.OrdinalIgnoreCase))
                options.Add(new ProbeVantageOption(
                    $"agent:{agent.AgentId}:{vantage.VantageId}",
                    LabelFor(agent, vantage),
                    agent.AgentId,
                    vantage.SourceBind));
        }

        return options.Count > 1 ? options : new List<ProbeVantageOption>();
    }

    /// <summary>
    /// An agent vantage's label: the agent, the WAN it probes for, and whether it runs on the
    /// gateway. The WAN's own name only - a vantage is named after its WAN, so printing the
    /// vantage name as well put the same words on screen twice inside nested brackets.
    /// </summary>
    internal static string LabelFor(ProbeVantageAgent agent, ProbeVantageBinding? vantage)
    {
        var wan = vantage is null
            ? null
            : string.IsNullOrWhiteSpace(vantage.WanLabel) ? vantage.Name : vantage.WanLabel;

        var label = string.IsNullOrWhiteSpace(wan) ? agent.Name : $"{agent.Name} - {wan}";
        return agent.OnGateway ? $"{label} (gateway)" : label;
    }
}
