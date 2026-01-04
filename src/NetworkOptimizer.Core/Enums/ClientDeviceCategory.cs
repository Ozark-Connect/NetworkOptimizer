namespace NetworkOptimizer.Core.Enums;

/// <summary>
/// Categories for network client devices, used for security audit purposes.
/// Determines VLAN placement recommendations and security policies.
/// </summary>
public enum ClientDeviceCategory
{
    /// <summary>
    /// Unknown or unidentified device
    /// </summary>
    Unknown = 0,

    // Surveillance/Security (1-9)
    /// <summary>
    /// IP cameras, NVRs, doorbells with video
    /// </summary>
    Camera = 1,

    /// <summary>
    /// Alarm panels, security hubs
    /// </summary>
    SecuritySystem = 2,

    // IoT/Smart Home (10-29)
    /// <summary>
    /// Smart bulbs, light strips (Hue, IKEA, LIFX)
    /// </summary>
    SmartLighting = 10,

    /// <summary>
    /// Smart outlets, power strips (Kasa, TP-Link)
    /// </summary>
    SmartPlug = 11,

    /// <summary>
    /// Nest, Ecobee thermostats
    /// </summary>
    SmartThermostat = 12,

    /// <summary>
    /// Smart door locks (August, Yale)
    /// </summary>
    SmartLock = 13,

    /// <summary>
    /// Motion, door/window, water sensors
    /// </summary>
    SmartSensor = 14,

    /// <summary>
    /// Smart refrigerators, washers, etc.
    /// </summary>
    SmartAppliance = 15,

    /// <summary>
    /// SmartThings, Hubitat hubs
    /// </summary>
    SmartHub = 16,

    /// <summary>
    /// Roomba, Roborock vacuums
    /// </summary>
    RoboticVacuum = 17,

    /// <summary>
    /// Catch-all for other IoT devices
    /// </summary>
    IoTGeneric = 19,

    // Media/Entertainment (20-29)
    /// <summary>
    /// Samsung, LG, Sony smart TVs
    /// </summary>
    SmartTV = 20,

    /// <summary>
    /// Roku, Apple TV, Fire TV, Chromecast
    /// </summary>
    StreamingDevice = 21,

    /// <summary>
    /// Alexa, Google Home, HomePod
    /// </summary>
    SmartSpeaker = 22,

    /// <summary>
    /// Sonos, other audio devices
    /// </summary>
    MediaPlayer = 23,

    // Gaming (30-39)
    /// <summary>
    /// PlayStation, Xbox, Nintendo
    /// </summary>
    GameConsole = 30,

    // Computing (40-49)
    /// <summary>
    /// Desktop computers
    /// </summary>
    Desktop = 40,

    /// <summary>
    /// Laptop computers
    /// </summary>
    Laptop = 41,

    /// <summary>
    /// Servers
    /// </summary>
    Server = 42,

    /// <summary>
    /// Network attached storage (Synology, QNAP)
    /// </summary>
    NAS = 43,

    // Mobile (50-59)
    /// <summary>
    /// Mobile phones
    /// </summary>
    Smartphone = 50,

    /// <summary>
    /// Tablets (iPad, Android tablets)
    /// </summary>
    Tablet = 51,

    // Communication (60-69)
    /// <summary>
    /// VoIP phones
    /// </summary>
    VoIP = 60,

    // Network Infrastructure (70-79)
    /// <summary>
    /// Wireless access points
    /// </summary>
    AccessPoint = 70,

    /// <summary>
    /// Network switches
    /// </summary>
    Switch = 71,

    /// <summary>
    /// Routers
    /// </summary>
    Router = 72,

    /// <summary>
    /// Gateways (USG, UDM)
    /// </summary>
    Gateway = 73,

    // Peripherals (80-89)
    /// <summary>
    /// Printers
    /// </summary>
    Printer = 80,

    /// <summary>
    /// Scanners
    /// </summary>
    Scanner = 81
}

/// <summary>
/// Extension methods for ClientDeviceCategory
/// </summary>
public static class ClientDeviceCategoryExtensions
{
    /// <summary>
    /// Check if the category is an IoT device (should be isolated)
    /// </summary>
    public static bool IsIoT(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.SmartLighting => true,
        ClientDeviceCategory.SmartPlug => true,
        ClientDeviceCategory.SmartThermostat => true,
        ClientDeviceCategory.SmartLock => true,
        ClientDeviceCategory.SmartSensor => true,
        ClientDeviceCategory.SmartAppliance => true,
        ClientDeviceCategory.SmartHub => true,
        ClientDeviceCategory.RoboticVacuum => true,
        ClientDeviceCategory.IoTGeneric => true,
        ClientDeviceCategory.SmartSpeaker => true,
        ClientDeviceCategory.SmartTV => true,
        ClientDeviceCategory.StreamingDevice => true,
        ClientDeviceCategory.MediaPlayer => true,
        _ => false
    };

    /// <summary>
    /// Check if the category is surveillance-related
    /// </summary>
    public static bool IsSurveillance(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.Camera => true,
        ClientDeviceCategory.SecuritySystem => true,
        _ => false
    };

    /// <summary>
    /// Check if the category is a low-risk IoT device.
    /// Low-risk devices are entertainment or convenience devices that don't control home security/access.
    /// Users often keep these on their main VLAN for easier access.
    /// </summary>
    public static bool IsLowRiskIoT(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.SmartTV => true,
        ClientDeviceCategory.StreamingDevice => true,
        ClientDeviceCategory.MediaPlayer => true,
        ClientDeviceCategory.GameConsole => true,
        ClientDeviceCategory.SmartLighting => true,
        ClientDeviceCategory.SmartPlug => true,
        ClientDeviceCategory.SmartSpeaker => true,
        ClientDeviceCategory.SmartAppliance => true,
        ClientDeviceCategory.RoboticVacuum => true,
        _ => false
    };

    /// <summary>
    /// Check if the category is a high-risk IoT device.
    /// High-risk devices control home security, access, or critical infrastructure.
    /// These should always be isolated on an IoT VLAN.
    /// </summary>
    public static bool IsHighRiskIoT(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.SmartThermostat => true,
        ClientDeviceCategory.SmartLock => true,
        ClientDeviceCategory.SmartHub => true,
        ClientDeviceCategory.SmartSensor => true,
        ClientDeviceCategory.SmartAppliance => true,
        _ => false
    };

    /// <summary>
    /// Check if the category is network infrastructure
    /// </summary>
    public static bool IsInfrastructure(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.AccessPoint => true,
        ClientDeviceCategory.Switch => true,
        ClientDeviceCategory.Router => true,
        ClientDeviceCategory.Gateway => true,
        _ => false
    };

    /// <summary>
    /// Get display-friendly name for the category
    /// </summary>
    public static string GetDisplayName(this ClientDeviceCategory category) => category switch
    {
        ClientDeviceCategory.SmartLighting => "Smart Lighting",
        ClientDeviceCategory.SmartPlug => "Smart Plug",
        ClientDeviceCategory.SmartThermostat => "Smart Thermostat",
        ClientDeviceCategory.SmartLock => "Smart Lock",
        ClientDeviceCategory.SmartSensor => "Smart Sensor",
        ClientDeviceCategory.SmartAppliance => "Smart Appliance",
        ClientDeviceCategory.SmartHub => "Smart Hub",
        ClientDeviceCategory.RoboticVacuum => "Robotic Vacuum",
        ClientDeviceCategory.IoTGeneric => "IoT Device",
        ClientDeviceCategory.SmartTV => "Smart TV",
        ClientDeviceCategory.StreamingDevice => "Streaming Device",
        ClientDeviceCategory.SmartSpeaker => "Smart Speaker",
        ClientDeviceCategory.MediaPlayer => "Media Player",
        ClientDeviceCategory.GameConsole => "Game Console",
        ClientDeviceCategory.SecuritySystem => "Security System",
        ClientDeviceCategory.AccessPoint => "Access Point",
        _ => category.ToString()
    };
}
