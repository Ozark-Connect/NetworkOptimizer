using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.ClientIcons;

/// <summary>How deep the 3D map extrudes a client's icon into a body.</summary>
public enum ClientBodyDepth
{
    /// <summary>A panel: a phone, a tablet, a TV.</summary>
    Thin = 0,
    /// <summary>A small box: a speaker, a console, a camera.</summary>
    Mid = 1,
    /// <summary>A deep box: a tower, an appliance.</summary>
    Deep = 2,
}

/// <summary>A client's body on the 3D map.</summary>
/// <param name="SizeM">Its longest side in metres when the camera is closest.</param>
/// <param name="Depth">How deep the icon is extruded.</param>
/// <param name="WallFlat">A big flat panel that hangs on or stands against a wall: the map turns it parallel to the nearest one.</param>
public sealed record ClientBody(double SizeM, ClientBodyDepth Depth, bool WallFlat = false);

/// <summary>
/// The 3D map's body for each kind of client. The scale is compressed from the bottom only: a small
/// client is drawn larger than life so it can be found (network devices draw at twice real size),
/// and a big one is drawn near its real size. A TV drawn smaller than an access point reads as wrong.
/// </summary>
public static class ClientBodyClass
{
    /// <summary>An undetected client, and the smallest any client draws: where a wrong guess costs least.</summary>
    public static readonly ClientBody Small = new(0.3, ClientBodyDepth.Thin);

    private static readonly ClientBody SmallBox = new(0.3, ClientBodyDepth.Mid);
    private static readonly ClientBody MediumPanel = new(0.45, ClientBodyDepth.Thin);
    private static readonly ClientBody MediumBox = new(0.45, ClientBodyDepth.Mid);
    private static readonly ClientBody Tower = new(0.5, ClientBodyDepth.Deep);
    // Half again as wide as the speakers it would otherwise be filed with, and along the wall like the TV above it.
    private static readonly ClientBody SoundBar = new(0.7, ClientBodyDepth.Thin, WallFlat: true);
    private static readonly ClientBody BigAppliance = new(0.85, ClientBodyDepth.Deep);
    private static readonly ClientBody Television = new(1.1, ClientBodyDepth.Thin, WallFlat: true);

    // UniFi fingerprint types the audit files under SmartTV that are not a television.
    private static readonly HashSet<int> NotATelevision = [38, 143, 188, 203, 254, 259];

    // UniFi fingerprint types the audit files under SmartAppliance that are floor-standing.
    private static readonly HashSet<int> FloorAppliance = [74, 118, 131, 137, 140, 177];

    private static readonly string[] FloorApplianceWords =
        ["washer", "washing machine", "dryer", "dishwasher", "oven", "range", "stove", "fridge", "refrigerator", "freezer"];

    /// <summary>
    /// The body for a client. The audit's category is too coarse on its own: it files a smart clock
    /// under SmartTV and a kettle under SmartAppliance, so the fingerprint type and the name split those.
    /// </summary>
    /// <param name="category">The Security Audit detector's category.</param>
    /// <param name="devTypeId">UniFi's fingerprint device type, when the client has one.</param>
    /// <param name="name">The client's name, for an appliance the fingerprint does not type.</param>
    [VendorSpecific("UniFi", "fingerprint dev_type_ids: 38 dashboard, 143 picture frame, 188 digital canvas, 203 smart display, 254 smart clock, 259 collaboration display; 74 machine wash, 118 dryer, 131 washing machine, 137 cooktop, 140 dishwasher, 177 oven")]
    public static ClientBody For(ClientDeviceCategory category, int? devTypeId = null, string? name = null) => category switch
    {
        // By name, whatever the detector made of it: a sound bar is a speaker or a media player to the audit.
        _ when NamesASoundBar(name) => SoundBar,
        ClientDeviceCategory.SmartTV =>
            devTypeId is { } type && NotATelevision.Contains(type) ? MediumPanel : Television,
        ClientDeviceCategory.SmartAppliance =>
            (devTypeId is { } type && FloorAppliance.Contains(type)) || NamesAFloorAppliance(name) ? BigAppliance : MediumBox,
        ClientDeviceCategory.Desktop or ClientDeviceCategory.Server => Tower,
        ClientDeviceCategory.Tablet or ClientDeviceCategory.Laptop => MediumPanel,
        ClientDeviceCategory.SmartSpeaker or ClientDeviceCategory.GameConsole or ClientDeviceCategory.Printer
            or ClientDeviceCategory.Scanner or ClientDeviceCategory.NAS or ClientDeviceCategory.RoboticVacuum
            or ClientDeviceCategory.MediaPlayer => MediumBox,
        ClientDeviceCategory.SmartLighting or ClientDeviceCategory.SmartPlug or ClientDeviceCategory.SmartHub
            or ClientDeviceCategory.Camera or ClientDeviceCategory.CloudCamera
            or ClientDeviceCategory.SecuritySystem or ClientDeviceCategory.CloudSecuritySystem
            or ClientDeviceCategory.StreamingDevice or ClientDeviceCategory.VoIP => SmallBox,
        _ => Small,
    };

    private static bool NamesASoundBar(string? name) =>
        !string.IsNullOrEmpty(name)
        && (name.Contains("soundbar", StringComparison.OrdinalIgnoreCase) || name.Contains("sound bar", StringComparison.OrdinalIgnoreCase));

    private static bool NamesAFloorAppliance(string? name) =>
        !string.IsNullOrEmpty(name) && FloorApplianceWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
}
