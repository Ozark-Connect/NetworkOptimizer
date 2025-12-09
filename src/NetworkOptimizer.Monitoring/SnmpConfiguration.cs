namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Configuration settings for SNMP polling
/// </summary>
public class SnmpConfiguration
{
    /// <summary>
    /// SNMP port (default: 161)
    /// </summary>
    public int Port { get; set; } = 161;

    /// <summary>
    /// Timeout in milliseconds (default: 2000ms)
    /// </summary>
    public int Timeout { get; set; } = 2000;

    /// <summary>
    /// Number of retry attempts (default: 2)
    /// </summary>
    public int RetryCount { get; set; } = 2;

    /// <summary>
    /// SNMP version to use
    /// </summary>
    public SnmpVersion Version { get; set; } = SnmpVersion.V3;

    /// <summary>
    /// Community string for SNMP v1/v2c
    /// </summary>
    public string Community { get; set; } = "public";

    /// <summary>
    /// Username for SNMP v3
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Authentication password for SNMP v3
    /// </summary>
    public string AuthenticationPassword { get; set; } = string.Empty;

    /// <summary>
    /// Privacy password for SNMP v3
    /// </summary>
    public string PrivacyPassword { get; set; } = string.Empty;

    /// <summary>
    /// Authentication protocol for SNMP v3
    /// </summary>
    public AuthenticationProtocol AuthProtocol { get; set; } = AuthenticationProtocol.SHA1;

    /// <summary>
    /// Privacy protocol for SNMP v3
    /// </summary>
    public PrivacyProtocol PrivProtocol { get; set; } = PrivacyProtocol.AES;

    /// <summary>
    /// Context name for SNMP v3
    /// </summary>
    public string ContextName { get; set; } = string.Empty;

    /// <summary>
    /// Engine ID for SNMP v3
    /// </summary>
    public string EngineId { get; set; } = string.Empty;

    /// <summary>
    /// Polling interval in seconds (default: 60)
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether to use high-capacity (64-bit) counters for 10G+ interfaces
    /// </summary>
    public bool UseHighCapacityCounters { get; set; } = true;

    /// <summary>
    /// Interface speed threshold (in Mbps) above which to use HC counters
    /// </summary>
    public int HighCapacityThresholdMbps { get; set; } = 1000;

    /// <summary>
    /// Enable debug logging
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;

    /// <summary>
    /// Maximum number of concurrent SNMP requests
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 10;

    /// <summary>
    /// Interfaces to exclude from monitoring (regex patterns)
    /// </summary>
    public List<string> ExcludeInterfacePatterns { get; set; } = new()
    {
        "^lo$",      // Loopback
        "^br-",      // Bridge interfaces
        "^docker",   // Docker interfaces
        "^veth",     // Virtual Ethernet
        "^ifb",      // Intermediate Functional Block
        "^virbr",    // Virtual Bridge
        "^tun",      // Tunnel interfaces
        "^tap",      // TAP devices
        "^null"      // Null interface
    };

    /// <summary>
    /// Validate the configuration
    /// </summary>
    public void Validate()
    {
        if (Port <= 0 || Port > 65535)
            throw new ArgumentException("Port must be between 1 and 65535", nameof(Port));

        if (Timeout <= 0)
            throw new ArgumentException("Timeout must be greater than 0", nameof(Timeout));

        if (RetryCount < 0)
            throw new ArgumentException("RetryCount cannot be negative", nameof(RetryCount));

        if (PollingIntervalSeconds <= 0)
            throw new ArgumentException("PollingIntervalSeconds must be greater than 0", nameof(PollingIntervalSeconds));

        if (Version == SnmpVersion.V3)
        {
            if (string.IsNullOrWhiteSpace(Username))
                throw new ArgumentException("Username is required for SNMP v3", nameof(Username));

            if (AuthProtocol != AuthenticationProtocol.None && string.IsNullOrWhiteSpace(AuthenticationPassword))
                throw new ArgumentException("AuthenticationPassword is required when using authentication", nameof(AuthenticationPassword));

            if (PrivProtocol != PrivacyProtocol.None && string.IsNullOrWhiteSpace(PrivacyPassword))
                throw new ArgumentException("PrivacyPassword is required when using privacy", nameof(PrivacyPassword));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Community))
                throw new ArgumentException("Community string is required for SNMP v1/v2c", nameof(Community));
        }
    }

    /// <summary>
    /// Create a copy of this configuration
    /// </summary>
    public SnmpConfiguration Clone()
    {
        return new SnmpConfiguration
        {
            Port = Port,
            Timeout = Timeout,
            RetryCount = RetryCount,
            Version = Version,
            Community = Community,
            Username = Username,
            AuthenticationPassword = AuthenticationPassword,
            PrivacyPassword = PrivacyPassword,
            AuthProtocol = AuthProtocol,
            PrivProtocol = PrivProtocol,
            ContextName = ContextName,
            EngineId = EngineId,
            PollingIntervalSeconds = PollingIntervalSeconds,
            UseHighCapacityCounters = UseHighCapacityCounters,
            HighCapacityThresholdMbps = HighCapacityThresholdMbps,
            EnableDebugLogging = EnableDebugLogging,
            MaxConcurrentRequests = MaxConcurrentRequests,
            ExcludeInterfacePatterns = new List<string>(ExcludeInterfacePatterns)
        };
    }
}

/// <summary>
/// SNMP protocol version
/// </summary>
public enum SnmpVersion
{
    /// <summary>
    /// SNMP version 1
    /// </summary>
    V1 = 0,

    /// <summary>
    /// SNMP version 2c
    /// </summary>
    V2c = 1,

    /// <summary>
    /// SNMP version 3
    /// </summary>
    V3 = 3
}

/// <summary>
/// SNMP v3 authentication protocols
/// </summary>
public enum AuthenticationProtocol
{
    /// <summary>
    /// No authentication
    /// </summary>
    None = 0,

    /// <summary>
    /// MD5 authentication (less secure, not recommended)
    /// </summary>
    MD5 = 1,

    /// <summary>
    /// SHA-1 authentication
    /// </summary>
    SHA1 = 2,

    /// <summary>
    /// SHA-256 authentication (recommended)
    /// </summary>
    SHA256 = 3,

    /// <summary>
    /// SHA-384 authentication
    /// </summary>
    SHA384 = 4,

    /// <summary>
    /// SHA-512 authentication (most secure)
    /// </summary>
    SHA512 = 5
}

/// <summary>
/// SNMP v3 privacy/encryption protocols
/// </summary>
public enum PrivacyProtocol
{
    /// <summary>
    /// No privacy/encryption
    /// </summary>
    None = 0,

    /// <summary>
    /// DES encryption (less secure, not recommended)
    /// </summary>
    DES = 1,

    /// <summary>
    /// AES-128 encryption (recommended)
    /// </summary>
    AES = 2,

    /// <summary>
    /// AES-192 encryption
    /// </summary>
    AES192 = 3,

    /// <summary>
    /// AES-256 encryption (most secure)
    /// </summary>
    AES256 = 4
}
