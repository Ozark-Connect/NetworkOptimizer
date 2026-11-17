using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.LanFlowMap;

/// <summary>How large the 3D map draws a client's body up close, relative to other clients.</summary>
public enum ClientBodySize
{
    /// <summary>Phones, plugs, sensors, cameras, and anything undetected.</summary>
    Small = 0,
    /// <summary>Tablets, laptops, speakers, consoles, printers.</summary>
    Medium = 1,
    /// <summary>TVs, desktops, servers, appliances.</summary>
    Large = 2,
}

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

/// <summary>
/// The 3D map's body for each kind of client. Sizes are relative, not to scale: a TV is clearly
/// larger than a phone, but true proportion would make the small ones too hard to find.
/// </summary>
public static class ClientBodyClass
{
    /// <summary>The size class of a client category. Undetected clients are small, where a wrong guess costs least.</summary>
    public static ClientBodySize SizeOf(ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.SmartTV or ClientDeviceCategory.Desktop or ClientDeviceCategory.Server
            or ClientDeviceCategory.SmartAppliance => ClientBodySize.Large,
        ClientDeviceCategory.Tablet or ClientDeviceCategory.Laptop or ClientDeviceCategory.SmartSpeaker
            or ClientDeviceCategory.GameConsole or ClientDeviceCategory.Printer or ClientDeviceCategory.Scanner
            or ClientDeviceCategory.NAS or ClientDeviceCategory.RoboticVacuum or ClientDeviceCategory.MediaPlayer => ClientBodySize.Medium,
        _ => ClientBodySize.Small,
    };

    /// <summary>The depth class of a client category.</summary>
    public static ClientBodyDepth DepthOf(ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.Desktop or ClientDeviceCategory.Server or ClientDeviceCategory.SmartAppliance => ClientBodyDepth.Deep,
        ClientDeviceCategory.SmartLighting or ClientDeviceCategory.SmartPlug or ClientDeviceCategory.SmartHub
            or ClientDeviceCategory.Camera or ClientDeviceCategory.CloudCamera
            or ClientDeviceCategory.SecuritySystem or ClientDeviceCategory.CloudSecuritySystem
            or ClientDeviceCategory.StreamingDevice or ClientDeviceCategory.VoIP
            or ClientDeviceCategory.SmartSpeaker or ClientDeviceCategory.MediaPlayer or ClientDeviceCategory.GameConsole
            or ClientDeviceCategory.Printer or ClientDeviceCategory.Scanner or ClientDeviceCategory.NAS
            or ClientDeviceCategory.RoboticVacuum => ClientBodyDepth.Mid,
        _ => ClientBodyDepth.Thin,
    };

    /// <summary>Whether the body is a big flat panel that hangs on or stands against a wall, so the map turns it parallel to the nearest one.</summary>
    public static bool IsWallFlat(ClientDeviceCategory category) => category == ClientDeviceCategory.SmartTV;
}
