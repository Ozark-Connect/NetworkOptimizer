using Microsoft.AspNetCore.Components.Web;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

namespace NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;

/// <summary>
/// What a value in a Live Traffic row does on click, supplied by the list that hosts the row. A page
/// with filters sets <see cref="IsKept"/>, <see cref="Toggle"/>, and <see cref="Exclude"/>; a list
/// without filters of its own (the Dashboard card) sets <see cref="LinkFor"/> instead, and every value
/// opens Live Traffic filtered to it.
/// </summary>
public sealed class LiveTrafficRowContext
{
    /// <summary>The value is the kept filter: drawn active, with an x.</summary>
    public Func<LiveTrafficField, string, bool> IsKept { get; init; } = (_, _) => false;

    /// <summary>A click on a value: keep it, or let it go again. Alt-click hides it.</summary>
    public Func<MouseEventArgs, LiveTrafficField, string, Task>? Toggle { get; init; }

    /// <summary>Hides rows with the value. Null hides the hide buttons.</summary>
    public Func<LiveTrafficFilter, Task>? Exclude { get; init; }

    /// <summary>Where a value links to, for a list whose values open Live Traffic rather than filter.</summary>
    public Func<LiveTrafficField, string, string?>? LinkFor { get; init; }

    /// <summary>
    /// The viewer may add custom apps on this site (Site Admin): unnamed ports offer Name This App.
    /// Read on each render, since the role is known only once the host has asked.
    /// </summary>
    public Func<bool> CanNameApps { get; init; } = () => false;

    /// <summary>Opens Name This App in place. Null: the list links to Live Traffic, which opens it.</summary>
    public Func<CustomAppSeed, Task>? NameApp { get; init; }

    /// <summary>Values that do nothing on click.</summary>
    public static LiveTrafficRowContext Inert { get; } = new();
}

/// <summary>Where a flow row is drawn, which decides its element and its cells.</summary>
public enum LiveFlowLayout
{
    /// <summary>Live Traffic's desktop Flows table.</summary>
    Table,
    /// <summary>The stacked phone list.</summary>
    List,
    /// <summary>A client's flows under its Dashboard card row, desktop columns.</summary>
    Nested,
    /// <summary>The same, phone columns.</summary>
    NestedPhone,
    /// <summary>History's desktop table.</summary>
    HistoryTable,
    /// <summary>History's phone list.</summary>
    HistoryList,
}

/// <summary>Where a Live DNS row is drawn.</summary>
public enum LiveDnsLayout { Table, List }
