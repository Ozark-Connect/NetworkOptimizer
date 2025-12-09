using System.Net;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Interface for SNMP polling operations
/// </summary>
public interface ISnmpPoller
{
    /// <summary>
    /// Get a single SNMP value
    /// </summary>
    Task<T?> GetAsync<T>(IPAddress ip, string oid);

    /// <summary>
    /// Walk an SNMP OID tree
    /// </summary>
    Task<List<Variable>> WalkAsync(IPAddress ip, string oid);

    /// <summary>
    /// Get complete device metrics
    /// </summary>
    Task<DeviceMetrics> GetDeviceMetricsAsync(IPAddress ip, string? hostname = null);

    /// <summary>
    /// Get interface metrics for all interfaces
    /// </summary>
    Task<List<InterfaceMetrics>> GetInterfaceMetricsAsync(IPAddress ip, string? hostname = null);

    /// <summary>
    /// Get system information
    /// </summary>
    Task<(string hostname, string description, long uptime)> GetSystemInfoAsync(IPAddress ip);
}

/// <summary>
/// SNMP poller with support for v1/v2c/v3 and comprehensive metric collection
/// </summary>
public class SnmpPoller : ISnmpPoller
{
    private readonly SnmpConfiguration _config;
    private readonly ILogger<SnmpPoller> _logger;

    public SnmpPoller(SnmpConfiguration config, ILogger<SnmpPoller> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config.Validate();
    }

    #region Core SNMP Operations

    /// <summary>
    /// Get a single SNMP value
    /// </summary>
    public async Task<T?> GetAsync<T>(IPAddress ip, string oid)
    {
        return await Task.Run(() =>
        {
            try
            {
                DebugLog($"SNMP Get: {ip}:{_config.Port} OID={oid} Version={_config.Version}");

                var endpoint = new IPEndPoint(ip, _config.Port);
                var objectId = new ObjectIdentifier(oid);
                var variables = new List<Variable> { new Variable(objectId) };

                IList<Variable> result;

                if (_config.Version == SnmpVersion.V3)
                {
                    result = GetV3(endpoint, variables);
                }
                else
                {
                    result = GetV1V2c(endpoint, variables);
                }

                var firstResult = result.FirstOrDefault();
                if (firstResult == null)
                {
                    DebugLog($"No response for OID {oid}");
                    return default;
                }

                DebugLog($"Response value: {firstResult.Data} (Type: {firstResult.Data.TypeCode})");
                return ConvertSnmpValue<T>(firstResult.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SNMP Get failed for {Ip}:{Oid}", ip, oid);
                return default;
            }
        });
    }

    /// <summary>
    /// Walk an SNMP OID tree
    /// </summary>
    public async Task<List<Variable>> WalkAsync(IPAddress ip, string oid)
    {
        return await Task.Run(() =>
        {
            try
            {
                DebugLog($"SNMP Walk: {ip}:{_config.Port} OID={oid}");

                var endpoint = new IPEndPoint(ip, _config.Port);
                var table = new ObjectIdentifier(oid);
                var list = new List<Variable>();

                if (_config.Version == SnmpVersion.V3)
                {
                    WalkV3(endpoint, table, list);
                }
                else
                {
                    WalkV1V2c(endpoint, table, list);
                }

                DebugLog($"Walk returned {list.Count} variables");
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SNMP Walk failed for {Ip}:{Oid}", ip, oid);
                return new List<Variable>();
            }
        });
    }

    #endregion

    #region Device Metrics Collection

    /// <summary>
    /// Get complete device metrics including system info and interfaces
    /// </summary>
    public async Task<DeviceMetrics> GetDeviceMetricsAsync(IPAddress ip, string? hostname = null)
    {
        var metrics = new DeviceMetrics
        {
            IpAddress = ip.ToString(),
            Hostname = hostname ?? string.Empty,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // Get system information
            await GetSystemMetrics(ip, metrics);

            // Get CPU and memory metrics
            await GetResourceMetrics(ip, metrics);

            // Get UniFi-specific metrics
            await GetUniFiMetrics(ip, metrics);

            // Get interface metrics
            metrics.Interfaces = await GetInterfaceMetricsAsync(ip, metrics.Hostname);
            metrics.InterfaceCount = metrics.Interfaces.Count;

            metrics.IsReachable = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get device metrics for {Ip}", ip);
            metrics.IsReachable = false;
            metrics.ErrorMessage = ex.Message;
        }

        return metrics;
    }

    /// <summary>
    /// Get interface metrics for all interfaces
    /// </summary>
    public async Task<List<InterfaceMetrics>> GetInterfaceMetricsAsync(IPAddress ip, string? hostname = null)
    {
        var interfaces = new List<InterfaceMetrics>();

        try
        {
            // Get number of interfaces
            var ifNumber = await GetAsync<int>(ip, UniFiOids.IfNumber);
            if (ifNumber <= 0)
            {
                _logger.LogWarning("No interfaces found on device {Ip}", ip);
                return interfaces;
            }

            DebugLog($"Device has {ifNumber} interfaces");

            // Walk interface table to get all interface indices
            var ifIndices = await WalkAsync(ip, UniFiOids.IfIndex);

            foreach (var variable in ifIndices)
            {
                try
                {
                    var index = Convert.ToInt32(variable.Data.ToString());
                    var interfaceMetrics = await GetInterfaceMetricsForIndex(ip, index, hostname);

                    if (interfaceMetrics != null && interfaceMetrics.ShouldMonitor())
                    {
                        interfaces.Add(interfaceMetrics);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get metrics for interface on {Ip}", ip);
                }
            }

            DebugLog($"Collected metrics for {interfaces.Count} interfaces");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get interface metrics for {Ip}", ip);
        }

        return interfaces;
    }

    /// <summary>
    /// Get system information
    /// </summary>
    public async Task<(string hostname, string description, long uptime)> GetSystemInfoAsync(IPAddress ip)
    {
        var hostname = await GetAsync<string>(ip, UniFiOids.SysName) ?? string.Empty;
        var description = await GetAsync<string>(ip, UniFiOids.SysDescr) ?? string.Empty;
        var uptime = await GetAsync<long>(ip, UniFiOids.SysUpTime);

        return (hostname, description, uptime);
    }

    #endregion

    #region Private Helper Methods

    private async Task GetSystemMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        metrics.Hostname = await GetAsync<string>(ip, UniFiOids.SysName) ?? metrics.Hostname;
        metrics.Description = await GetAsync<string>(ip, UniFiOids.SysDescr) ?? string.Empty;
        metrics.Location = await GetAsync<string>(ip, UniFiOids.SysLocation) ?? string.Empty;
        metrics.Contact = await GetAsync<string>(ip, UniFiOids.SysContact) ?? string.Empty;
        metrics.Uptime = await GetAsync<long>(ip, UniFiOids.SysUpTime);
        metrics.ObjectId = await GetAsync<string>(ip, UniFiOids.SysObjectID) ?? string.Empty;
    }

    private async Task GetResourceMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        // Try UCD-SNMP CPU metrics first
        var cpuIdle = await GetAsync<double>(ip, UniFiOids.SsCpuIdle);
        if (cpuIdle > 0)
        {
            metrics.CpuUsage = 100.0 - cpuIdle;
        }
        else
        {
            // Try Host Resources CPU
            var cpuLoad = await GetAsync<double>(ip, UniFiOids.HrProcessorLoad);
            if (cpuLoad > 0)
            {
                metrics.CpuUsage = cpuLoad;
            }
        }

        // Try UCD-SNMP memory metrics
        var totalMem = await GetAsync<long>(ip, UniFiOids.MemTotalReal);
        var availMem = await GetAsync<long>(ip, UniFiOids.MemAvailReal);

        if (totalMem > 0)
        {
            metrics.TotalMemory = totalMem * 1024; // Convert KB to bytes
            metrics.FreeMemory = availMem * 1024;
            metrics.UsedMemory = metrics.TotalMemory - metrics.FreeMemory;
            metrics.MemoryUsage = (double)metrics.UsedMemory / metrics.TotalMemory * 100.0;
        }
        else
        {
            // Try Host Resources memory
            var storageVars = await WalkAsync(ip, UniFiOids.HrStorageTable);
            await ParseHostResourcesMemory(storageVars, metrics);
        }
    }

    private async Task GetUniFiMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        metrics.Model = await GetAsync<string>(ip, UniFiOids.UniFiModel) ?? string.Empty;
        metrics.FirmwareVersion = await GetAsync<string>(ip, UniFiOids.UniFiFirmwareVersion) ?? string.Empty;
        metrics.MacAddress = await GetAsync<string>(ip, UniFiOids.UniFiMacAddress) ?? string.Empty;

        var temp = await GetAsync<double>(ip, UniFiOids.UniFiTemperature);
        if (temp > 0 && temp < 200) // Sanity check
        {
            metrics.Temperature = temp;
        }

        // Determine device type from model or description
        metrics.DeviceType = DetermineDeviceType(metrics.Model, metrics.Description);
    }

    private async Task<InterfaceMetrics?> GetInterfaceMetricsForIndex(IPAddress ip, int index, string? hostname)
    {
        var metrics = new InterfaceMetrics
        {
            Index = index,
            DeviceIp = ip.ToString(),
            DeviceHostname = hostname ?? string.Empty,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // Basic interface info
            metrics.Description = await GetAsync<string>(ip, $"{UniFiOids.IfDescr}.{index}") ?? string.Empty;
            metrics.Name = await GetAsync<string>(ip, $"{UniFiOids.IfAlias}.{index}") ??
                          await GetAsync<string>(ip, $"{UniFiOids.IfName}.{index}") ?? string.Empty;
            metrics.Type = await GetAsync<int>(ip, $"{UniFiOids.IfType}.{index}");
            metrics.Mtu = await GetAsync<int>(ip, $"{UniFiOids.IfMtu}.{index}");
            metrics.Speed = await GetAsync<long>(ip, $"{UniFiOids.IfSpeed}.{index}");
            metrics.HighSpeed = await GetAsync<long>(ip, $"{UniFiOids.IfHighSpeed}.{index}");
            metrics.PhysicalAddress = await GetAsync<string>(ip, $"{UniFiOids.IfPhysAddress}.{index}") ?? string.Empty;

            // Status
            metrics.AdminStatus = await GetAsync<int>(ip, $"{UniFiOids.IfAdminStatus}.{index}");
            metrics.OperStatus = await GetAsync<int>(ip, $"{UniFiOids.IfOperStatus}.{index}");
            metrics.LastChange = await GetAsync<long>(ip, $"{UniFiOids.IfLastChange}.{index}");

            // Determine whether to use high-capacity counters
            var useHC = _config.UseHighCapacityCounters &&
                       (metrics.HighSpeed >= _config.HighCapacityThresholdMbps || metrics.SpeedMbps >= _config.HighCapacityThresholdMbps);

            if (useHC)
            {
                // Use 64-bit high-capacity counters
                metrics.InOctets = await GetAsync<long>(ip, $"{UniFiOids.IfHCInOctets}.{index}");
                metrics.OutOctets = await GetAsync<long>(ip, $"{UniFiOids.IfHCOutOctets}.{index}");
                metrics.InUcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCInUcastPkts}.{index}");
                metrics.OutUcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCOutUcastPkts}.{index}");
                metrics.InMulticastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCInMulticastPkts}.{index}");
                metrics.OutMulticastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCOutMulticastPkts}.{index}");
                metrics.InBroadcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCInBroadcastPkts}.{index}");
                metrics.OutBroadcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfHCOutBroadcastPkts}.{index}");
            }
            else
            {
                // Use 32-bit counters
                metrics.InOctets = await GetAsync<long>(ip, $"{UniFiOids.IfInOctets}.{index}");
                metrics.OutOctets = await GetAsync<long>(ip, $"{UniFiOids.IfOutOctets}.{index}");
                metrics.InUcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfInUcastPkts}.{index}");
                metrics.OutUcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfOutUcastPkts}.{index}");
                metrics.InMulticastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfInMulticastPkts}.{index}");
                metrics.OutMulticastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfOutMulticastPkts}.{index}");
                metrics.InBroadcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfInBroadcastPkts}.{index}");
                metrics.OutBroadcastPkts = await GetAsync<long>(ip, $"{UniFiOids.IfOutBroadcastPkts}.{index}");
            }

            // Errors and discards (always 32-bit)
            metrics.InDiscards = await GetAsync<long>(ip, $"{UniFiOids.IfInDiscards}.{index}");
            metrics.InErrors = await GetAsync<long>(ip, $"{UniFiOids.IfInErrors}.{index}");
            metrics.InUnknownProtos = await GetAsync<long>(ip, $"{UniFiOids.IfInUnknownProtos}.{index}");
            metrics.OutDiscards = await GetAsync<long>(ip, $"{UniFiOids.IfOutDiscards}.{index}");
            metrics.OutErrors = await GetAsync<long>(ip, $"{UniFiOids.IfOutErrors}.{index}");

            return metrics;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get metrics for interface {Index} on {Ip}", index, ip);
            return null;
        }
    }

    private Task ParseHostResourcesMemory(List<Variable> storageVars, DeviceMetrics metrics)
    {
        // Parse Host Resources storage table for memory information
        // This is more complex as it requires correlating multiple OIDs
        // Implementation would parse the storage table and find RAM entries
        return Task.CompletedTask;
    }

    private DeviceType DetermineDeviceType(string model, string description)
    {
        var combined = $"{model} {description}".ToLowerInvariant();

        if (combined.Contains("usg") || combined.Contains("gateway") || combined.Contains("udm"))
            return DeviceType.Gateway;
        if (combined.Contains("switch") || combined.Contains("usw"))
            return DeviceType.Switch;
        if (combined.Contains("ap") || combined.Contains("access") || combined.Contains("uap"))
            return DeviceType.AccessPoint;
        if (combined.Contains("router"))
            return DeviceType.Router;
        if (combined.Contains("firewall"))
            return DeviceType.Firewall;

        return DeviceType.Unknown;
    }

    #endregion

    #region SNMP Protocol Implementation

    private IList<Variable> GetV3(IPEndPoint endpoint, List<Variable> variables)
    {
        DebugLog($"Using SNMP v3 - Username: {_config.Username}");

        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = discovery.GetResponse(_config.Timeout, endpoint);

        var auth = GetAuthenticationProvider();
        var priv = GetPrivacyProvider(auth);

        var request = new GetRequestMessage(
            VersionCode.V3,
            Messenger.NextMessageId,
            Messenger.NextRequestId,
            new OctetString(_config.Username),
            variables,
            priv,
            Messenger.MaxMessageSize,
            report
        );

        var response = request.GetResponse(_config.Timeout, endpoint);
        return response.Pdu().Variables;
    }

    private IList<Variable> GetV1V2c(IPEndPoint endpoint, List<Variable> variables)
    {
        DebugLog($"Using SNMP v{_config.Version} with community: {_config.Community}");

        var versionCode = _config.Version == SnmpVersion.V1 ? VersionCode.V1 : VersionCode.V2;
        var community = new OctetString(_config.Community);

        return Messenger.Get(
            versionCode,
            endpoint,
            community,
            variables,
            _config.Timeout
        );
    }

    private void WalkV3(IPEndPoint endpoint, ObjectIdentifier table, List<Variable> results)
    {
        var discovery = Messenger.GetNextDiscovery(SnmpType.GetBulkRequestPdu);
        var report = discovery.GetResponse(_config.Timeout, endpoint);

        var auth = GetAuthenticationProvider();
        var priv = GetPrivacyProvider(auth);

        var current = table;

        while (true)
        {
            var variables = new List<Variable> { new Variable(current) };

            var request = new GetNextRequestMessage(
                VersionCode.V3,
                Messenger.NextMessageId,
                Messenger.NextRequestId,
                new OctetString(_config.Username),
                variables,
                priv,
                Messenger.MaxMessageSize,
                report
            );

            var response = request.GetResponse(_config.Timeout, endpoint);
            var variable = response.Pdu().Variables[0];

            if (!variable.Id.ToString().StartsWith(table.ToString()) ||
                variable.Data.TypeCode == SnmpType.EndOfMibView)
                break;

            results.Add(variable);
            current = variable.Id;
        }
    }

    private void WalkV1V2c(IPEndPoint endpoint, ObjectIdentifier table, List<Variable> list)
    {
        var versionCode = _config.Version == SnmpVersion.V1 ? VersionCode.V1 : VersionCode.V2;
        var community = new OctetString(_config.Community);

        Messenger.Walk(
            versionCode,
            endpoint,
            community,
            table,
            list,
            _config.Timeout,
            WalkMode.WithinSubtree
        );
    }

    private IAuthenticationProvider GetAuthenticationProvider()
    {
        if (string.IsNullOrEmpty(_config.AuthenticationPassword))
            return DefaultAuthenticationProvider.Instance;

        var authPassword = new OctetString(_config.AuthenticationPassword);

        return _config.AuthProtocol switch
        {
            AuthenticationProtocol.MD5 => new MD5AuthenticationProvider(authPassword),
            AuthenticationProtocol.SHA1 => new SHA1AuthenticationProvider(authPassword),
            AuthenticationProtocol.SHA256 => new SHA256AuthenticationProvider(authPassword),
            AuthenticationProtocol.SHA384 => new SHA384AuthenticationProvider(authPassword),
            AuthenticationProtocol.SHA512 => new SHA512AuthenticationProvider(authPassword),
            _ => DefaultAuthenticationProvider.Instance
        };
    }

    private IPrivacyProvider GetPrivacyProvider(IAuthenticationProvider auth)
    {
        if (string.IsNullOrEmpty(_config.PrivacyPassword))
            return new DefaultPrivacyProvider(auth);

        var privPassword = new OctetString(_config.PrivacyPassword);

        return _config.PrivProtocol switch
        {
            PrivacyProtocol.DES => new DESPrivacyProvider(privPassword, auth),
            PrivacyProtocol.AES => new AESPrivacyProvider(privPassword, auth),
            PrivacyProtocol.AES192 => new AES192PrivacyProvider(privPassword, auth),
            PrivacyProtocol.AES256 => new AES256PrivacyProvider(privPassword, auth),
            _ => new DefaultPrivacyProvider(auth)
        };
    }

    #endregion

    #region Value Conversion

    private T? ConvertSnmpValue<T>(ISnmpData? data)
    {
        if (data == null) return default;

        var targetType = typeof(T);
        var dataString = data.ToString();

        if (targetType == typeof(string))
            return (T)(object)dataString;

        try
        {
            if (targetType == typeof(int))
            {
                if (int.TryParse(dataString, out var intValue))
                    return (T)(object)intValue;

                if (data.TypeCode == SnmpType.TimeTicks)
                {
                    var match = Regex.Match(dataString, @"(\d+)");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var tickValue))
                        return (T)(object)tickValue;
                }
            }

            if (targetType == typeof(uint))
            {
                if (uint.TryParse(dataString, out var uintValue))
                    return (T)(object)uintValue;

                if (data.TypeCode == SnmpType.TimeTicks)
                {
                    var match = Regex.Match(dataString, @"(\d+)");
                    if (match.Success && uint.TryParse(match.Groups[1].Value, out var tickValue))
                        return (T)(object)tickValue;
                }
            }

            if (targetType == typeof(long))
            {
                if (long.TryParse(dataString, out var longValue))
                    return (T)(object)longValue;

                if (data.TypeCode == SnmpType.TimeTicks)
                {
                    var match = Regex.Match(dataString, @"(\d+)");
                    if (match.Success && long.TryParse(match.Groups[1].Value, out var tickValue))
                        return (T)(object)tickValue;
                }
            }

            if (targetType == typeof(double))
            {
                if (double.TryParse(dataString, out var doubleValue))
                    return (T)(object)doubleValue;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert SNMP value: {Value} to {Type}", dataString, targetType.Name);
        }

        return default;
    }

    #endregion

    #region Logging

    private void DebugLog(string message)
    {
        if (_config.EnableDebugLogging)
            _logger.LogDebug("{Message}", message);
    }

    #endregion
}
