namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// One place a Network Tools probe can be run from, as offered in the vantage picker.
/// </summary>
/// <param name="Key">Picker value: "server", or "agent:{id}".</param>
/// <param name="Label">What the user reads.</param>
/// <param name="AgentId">The agent to run on, or null for the server vantage.</param>
/// <param name="SourceBind">
/// What this vantage's probes bind to on the way out - its WAN context's interface or source IP.
/// Null when the vantage probes on its own route, which is every vantage with no context.
/// </param>
public sealed record ProbeVantageOption(string Key, string Label, int? AgentId, string? SourceBind);

/// <summary>
/// What is known about one of a site's connected agents while the vantage list is built.
/// </summary>
/// <param name="AgentId">Registry id of the agent.</param>
/// <param name="Name">Agent name as enrolled.</param>
/// <param name="OnGateway">Whether this agent's own address is one of the gateway's.</param>
/// <param name="ContextName">Name of the WAN context assigned to this agent, if any.</param>
/// <param name="WanLabel">That context's WAN in UniFi's own wording ("My ISP WAN2"), if known.</param>
/// <param name="SourceBind">The context's interface or source IP, if any.</param>
public sealed record ProbeVantageAgent(
    int AgentId,
    string Name,
    bool OnGateway,
    string? ContextName,
    string? WanLabel,
    string? SourceBind);

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
            options.Add(new ProbeVantageOption($"agent:{agent.AgentId}", LabelFor(agent), agent.AgentId, agent.SourceBind));

        return options.Count > 1 ? options : new List<ProbeVantageOption>();
    }

    /// <summary>
    /// An agent's label: its name, then what distinguishes it - the WAN context it probes for and
    /// that WAN, and whether it runs on the gateway itself.
    /// </summary>
    internal static string LabelFor(ProbeVantageAgent agent)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent.ContextName))
            parts.Add(string.IsNullOrWhiteSpace(agent.WanLabel)
                ? agent.ContextName!
                : $"{agent.ContextName}, {agent.WanLabel}");
        if (agent.OnGateway)
            parts.Add("on the gateway");

        return parts.Count == 0 ? agent.Name : $"{agent.Name} ({string.Join(", ", parts)})";
    }
}
