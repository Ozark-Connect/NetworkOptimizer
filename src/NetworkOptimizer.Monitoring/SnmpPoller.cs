using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;

// SNMP v3 protocol requires supporting legacy authentication/encryption for device compatibility.
// MD5, SHA1, and DES are marked obsolete by the library but are still required for many network devices.
// The GetRequestMessage/GetNextRequestMessage constructors are also marked obsolete but are the
// correct way to send authenticated SNMP v3 requests per library documentation.
#pragma warning disable CS0618 // Type or member is obsolete - required for SNMP v3 protocol compatibility

namespace NetworkOptimizer.Monitoring;

/// <summary>
/// Interface for SNMP polling operations
/// </summary>
public interface ISnmpPoller
{
    Task<T?> GetAsync<T>(IPAddress ip, string oid);
    Task<List<Variable>> WalkAsync(IPAddress ip, string oid);
    Task<IList<Variable>> GetMultipleAsync(IPAddress ip, IList<string> oids);
    Task<List<Variable>> BulkWalkAsync(IPAddress ip, string oid, int maxRepetitions = 25);
    Task<DeviceMetrics> GetDeviceMetricsAsync(IPAddress ip, string? hostname = null);
    Task<List<InterfaceMetrics>> GetInterfaceMetricsAsync(IPAddress ip, string? hostname = null);
    Task<(string hostname, string description, long uptime)> GetSystemInfoAsync(IPAddress ip);
}

/// <summary>
/// SNMP poller with support for v1/v2c/v3, batched multi-OID GET, GETBULK walk, and V3 discovery caching.
/// </summary>
public class SnmpPoller : ISnmpPoller
{
    private readonly SnmpConfiguration _config;
    private readonly ILogger<SnmpPoller> _logger;
    private readonly ConcurrentDictionary<string, (ISnmpMessage Report, DateTime CachedAt)> _discoveryCache = new();
    private readonly ConcurrentDictionary<string, InterfaceMetadataCache> _ifMetadataCache = new();
    private const int DiscoveryCacheTtlSeconds = 60;
    private const int InterfaceMetadataCacheTtlSeconds = 60;

    public SnmpPoller(SnmpConfiguration config, ILogger<SnmpPoller> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config.Validate();
    }

    #region Core SNMP Operations

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
                _logger.LogDebug(ex, "SNMP Get failed for {Ip}:{Oid}", ip, oid);
                return default;
            }
        });
    }

    public async Task<IList<Variable>> GetMultipleAsync(IPAddress ip, IList<string> oids)
    {
        if (oids.Count == 0) return Array.Empty<Variable>();

        // V1 only supports single-variable GET; fall back to sequential
        if (_config.Version == SnmpVersion.V1)
        {
            return await GetMultipleSequentialAsync(ip, oids);
        }

        return await Task.Run(() =>
        {
            try
            {
                DebugLog($"SNMP Multi-Get: {ip}:{_config.Port} OIDs={oids.Count} Version={_config.Version}");

                var endpoint = new IPEndPoint(ip, _config.Port);
                var variables = oids.Select(oid => new Variable(new ObjectIdentifier(oid))).ToList();

                IList<Variable> result;

                if (_config.Version == SnmpVersion.V3)
                {
                    result = GetV3(endpoint, variables);
                }
                else
                {
                    result = GetV1V2c(endpoint, variables);
                }

                DebugLog($"Multi-Get returned {result.Count} variables");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SNMP Multi-Get failed for {Ip} ({Count} OIDs)", ip, oids.Count);
                return (IList<Variable>)Array.Empty<Variable>();
            }
        });
    }

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
                _logger.LogDebug(ex, "SNMP Walk failed for {Ip}:{Oid}", ip, oid);
                return new List<Variable>();
            }
        });
    }

    public async Task<List<Variable>> BulkWalkAsync(IPAddress ip, string oid, int maxRepetitions = 25)
    {
        // V1 doesn't support GETBULK; fall back to regular walk
        if (_config.Version == SnmpVersion.V1)
        {
            return await WalkAsync(ip, oid);
        }

        return await Task.Run(() =>
        {
            var list = new List<Variable>();
            try
            {
                DebugLog($"SNMP BulkWalk: {ip}:{_config.Port} OID={oid} MaxRep={maxRepetitions}");

                var endpoint = new IPEndPoint(ip, _config.Port);
                var table = new ObjectIdentifier(oid);

                if (_config.Version == SnmpVersion.V3)
                {
                    var report = GetCachedDiscoveryReport(endpoint);
                    var auth = GetAuthenticationProvider();
                    var priv = GetPrivacyProvider(auth);

                    Messenger.BulkWalk(
                        VersionCode.V3,
                        endpoint,
                        new OctetString(_config.Username),
                        new OctetString(_config.ContextName ?? ""),
                        table,
                        list,
                        _config.Timeout,
                        maxRepetitions,
                        WalkMode.WithinSubtree,
                        priv,
                        report
                    );
                }
                else
                {
                    var community = new OctetString(_config.Community);

                    Messenger.BulkWalk(
                        VersionCode.V2,
                        endpoint,
                        community,
                        new OctetString(""),
                        table,
                        list,
                        _config.Timeout,
                        maxRepetitions,
                        WalkMode.WithinSubtree,
                        null,
                        null
                    );
                }

                DebugLog($"BulkWalk returned {list.Count} variables for {oid}");
                return list;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SNMP BulkWalk failed for {Ip}:{Oid} (partial: {Count} variables)", ip, oid, list.Count);
                return list;
            }
        });
    }

    #endregion

    #region Device Metrics Collection

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
            var interfacesTask = GetInterfaceMetricsAsync(ip, metrics.Hostname);

            await Task.WhenAll(
                GetSystemMetrics(ip, metrics),
                GetResourceMetrics(ip, metrics),
                GetUniFiMetrics(ip, metrics),
                interfacesTask
            );

            metrics.Interfaces = await interfacesTask;
            metrics.InterfaceCount = metrics.Interfaces.Count;

            metrics.IsReachable = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get device metrics for {Ip}", ip);
            metrics.IsReachable = false;
            metrics.ErrorMessage = ex.Message;
        }

        return metrics;
    }

    public async Task<List<InterfaceMetrics>> GetInterfaceMetricsAsync(IPAddress ip, string? hostname = null)
    {
        var interfaces = new List<InterfaceMetrics>();

        try
        {
            var cacheKey = ip.ToString();
            var metadata = await GetOrRefreshInterfaceMetadataAsync(ip, cacheKey);
            if (metadata == null || metadata.DescrByIdx.Count == 0)
            {
                _logger.LogDebug("No interfaces found on device {Ip}", ip);
                return interfaces;
            }

            // Only walk counters + operStatus on each call (the hot path)
            var operStatusWalk = await BulkWalkAsync(ip, UniFiOids.IfOperStatus);
            var operByIdx = IndexByIfIndex(operStatusWalk, UniFiOids.IfOperStatus);

            // Counter walks - try HC first, fall back to 32-bit
            var hcInOctets = await BulkWalkAsync(ip, UniFiOids.IfHCInOctets);
            var hcOutOctets = await BulkWalkAsync(ip, UniFiOids.IfHCOutOctets);

            bool needFallback = hcInOctets.Count == 0;
            List<Variable>? inOctets32 = null, outOctets32 = null;
            if (needFallback)
            {
                inOctets32 = await BulkWalkAsync(ip, UniFiOids.IfInOctets);
                outOctets32 = await BulkWalkAsync(ip, UniFiOids.IfOutOctets);
            }

            // Error counters
            var inErrors = await BulkWalkAsync(ip, UniFiOids.IfInErrors);
            var outErrors = await BulkWalkAsync(ip, UniFiOids.IfOutErrors);
            var inDiscards = await BulkWalkAsync(ip, UniFiOids.IfInDiscards);
            var outDiscards = await BulkWalkAsync(ip, UniFiOids.IfOutDiscards);

            var hcInOctetsByIdx = IndexByIfIndex(hcInOctets, UniFiOids.IfHCInOctets);
            var hcOutOctetsByIdx = IndexByIfIndex(hcOutOctets, UniFiOids.IfHCOutOctets);
            var inOctets32ByIdx = needFallback ? IndexByIfIndex(inOctets32!, UniFiOids.IfInOctets) : null;
            var outOctets32ByIdx = needFallback ? IndexByIfIndex(outOctets32!, UniFiOids.IfOutOctets) : null;
            var inErrorsByIdx = IndexByIfIndex(inErrors, UniFiOids.IfInErrors);
            var outErrorsByIdx = IndexByIfIndex(outErrors, UniFiOids.IfOutErrors);
            var inDiscardsByIdx = IndexByIfIndex(inDiscards, UniFiOids.IfInDiscards);
            var outDiscardsByIdx = IndexByIfIndex(outDiscards, UniFiOids.IfOutDiscards);

            // Build interface metrics using cached metadata + fresh counters
            foreach (var (idx, descr) in metadata.DescrByIdx)
            {
                if (!int.TryParse(idx, out var index)) continue;

                var speed = ParseLong(metadata.SpeedByIdx, idx);
                var highSpeed = ParseLong(metadata.HighSpeedByIdx, idx);
                var useHC = _config.UseHighCapacityCounters && !needFallback &&
                           (highSpeed >= _config.HighCapacityThresholdMbps || (speed / 1_000_000) >= _config.HighCapacityThresholdMbps);

                var metrics = new InterfaceMetrics
                {
                    Index = index,
                    DeviceIp = ip.ToString(),
                    DeviceHostname = hostname ?? string.Empty,
                    Timestamp = DateTime.UtcNow,
                    Description = descr,
                    Name = ResolveIfName(GetString(metadata.AliasByIdx, idx), GetString(metadata.NameByIdx, idx)),
                    PortId = GetString(metadata.NameByIdx, idx) ?? string.Empty,
                    Type = ParseInt(metadata.TypeByIdx, idx),
                    Mtu = ParseInt(metadata.MtuByIdx, idx),
                    Speed = speed,
                    HighSpeed = highSpeed,
                    PhysicalAddress = GetString(metadata.PhysAddrByIdx, idx) ?? string.Empty,
                    AdminStatus = ParseInt(metadata.AdminByIdx, idx),
                    OperStatus = ParseInt(operByIdx, idx),
                    LastChange = ParseLong(metadata.LastChangeByIdx, idx),
                    InOctets = useHC ? ParseLong(hcInOctetsByIdx, idx) : ParseLong(inOctets32ByIdx ?? hcInOctetsByIdx, idx),
                    OutOctets = useHC ? ParseLong(hcOutOctetsByIdx, idx) : ParseLong(outOctets32ByIdx ?? hcOutOctetsByIdx, idx),
                    InDiscards = ParseLong(inDiscardsByIdx, idx),
                    InErrors = ParseLong(inErrorsByIdx, idx),
                    OutDiscards = ParseLong(outDiscardsByIdx, idx),
                    OutErrors = ParseLong(outErrorsByIdx, idx),
                };

                if (metrics.ShouldMonitor())
                {
                    interfaces.Add(metrics);
                }
                else
                {
                    _logger.LogDebug("ShouldMonitor rejected ifIndex {Idx} desc={Desc} name={Name} on {Ip}", idx, descr, metrics.Name, ip);
                }
            }

            DebugLog($"Collected metrics for {interfaces.Count} interfaces from {metadata.DescrByIdx.Count} in metadata");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get interface metrics for {Ip}", ip);
        }

        return interfaces;
    }

    public async Task<(string hostname, string description, long uptime)> GetSystemInfoAsync(IPAddress ip)
    {
        var oids = new List<string> { UniFiOids.SysName, UniFiOids.SysDescr, UniFiOids.SysUpTime };
        var results = await GetMultipleAsync(ip, oids);

        string hostname = string.Empty, description = string.Empty;
        long uptime = 0;

        foreach (var v in results)
        {
            if (IsNoSuchOrEndOfMib(v)) continue;
            var oid = v.Id.ToString();
            if (oid == UniFiOids.SysName) hostname = v.Data.ToString();
            else if (oid == UniFiOids.SysDescr) description = v.Data.ToString();
            else if (oid == UniFiOids.SysUpTime) uptime = ConvertSnmpValue<long>(v.Data);
        }

        return (hostname, description, uptime);
    }

    #endregion

    #region Private Helper Methods

    private async Task GetSystemMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        var oids = new List<string>
        {
            UniFiOids.SysName,
            UniFiOids.SysDescr,
            UniFiOids.SysLocation,
            UniFiOids.SysContact,
            UniFiOids.SysUpTime,
            UniFiOids.SysObjectID
        };

        var results = await GetMultipleAsync(ip, oids);

        foreach (var v in results)
        {
            if (IsNoSuchOrEndOfMib(v)) continue;
            var oid = v.Id.ToString();
            if (oid == UniFiOids.SysName) metrics.Hostname = v.Data.ToString();
            else if (oid == UniFiOids.SysDescr) metrics.Description = v.Data.ToString();
            else if (oid == UniFiOids.SysLocation) metrics.Location = v.Data.ToString();
            else if (oid == UniFiOids.SysContact) metrics.Contact = v.Data.ToString();
            else if (oid == UniFiOids.SysUpTime) metrics.Uptime = ConvertSnmpValue<long>(v.Data);
            else if (oid == UniFiOids.SysObjectID) metrics.ObjectId = v.Data.ToString();
        }
    }

    private async Task GetResourceMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        var oids = new List<string>
        {
            UniFiOids.SsCpuIdle,
            UniFiOids.MemTotalReal,
            UniFiOids.MemAvailReal,
            UniFiOids.MemCached
        };

        var results = await GetMultipleAsync(ip, oids);

        double cpuIdle = 0;
        long totalMem = 0, availMem = 0, cachedMem = 0;

        foreach (var v in results)
        {
            if (IsNoSuchOrEndOfMib(v)) continue;
            var oid = v.Id.ToString();
            if (oid == UniFiOids.SsCpuIdle) cpuIdle = ConvertSnmpValue<double>(v.Data);
            else if (oid == UniFiOids.MemTotalReal) totalMem = ConvertSnmpValue<long>(v.Data);
            else if (oid == UniFiOids.MemAvailReal) availMem = ConvertSnmpValue<long>(v.Data);
            else if (oid == UniFiOids.MemCached) cachedMem = ConvertSnmpValue<long>(v.Data);
        }

        if (cpuIdle > 0)
        {
            metrics.CpuUsage = 100.0 - cpuIdle;
        }
        else
        {
            var cores = await BulkWalkAsync(ip, UniFiOids.HrProcessorLoad);
            if (cores.Count > 0)
            {
                var sum = 0.0;
                foreach (var v in cores)
                {
                    if (int.TryParse(v.Data.ToString(), out var load))
                        sum += load;
                }
                metrics.CpuUsage = sum / cores.Count;
            }
        }

        if (totalMem > 0)
        {
            metrics.TotalMemory = totalMem * 1024;
            metrics.FreeMemory = availMem * 1024;
            var actualUsedKb = totalMem - availMem - Math.Max(0, cachedMem);
            metrics.UsedMemory = Math.Max(0, actualUsedKb) * 1024;
            metrics.MemoryUsage = (double)metrics.UsedMemory / metrics.TotalMemory * 100.0;
        }
        else
        {
            var storageVars = await BulkWalkAsync(ip, UniFiOids.HrStorageTable);
            ParseHostResourcesMemory(storageVars, metrics);
        }
    }

    private async Task GetUniFiMetrics(IPAddress ip, DeviceMetrics metrics)
    {
        var oids = new List<string>
        {
            UniFiOids.UniFiModel,
            UniFiOids.UniFiFirmwareVersion,
            UniFiOids.UniFiMacAddress,
            UniFiOids.LmSensorsCpuTemp,
            UniFiOids.UniFiTemperature
        };

        var results = await GetMultipleAsync(ip, oids);

        double lmTemp = 0, unifiTemp = 0;

        foreach (var v in results)
        {
            if (IsNoSuchOrEndOfMib(v)) continue;
            var oid = v.Id.ToString();
            if (oid == UniFiOids.UniFiModel) metrics.Model = v.Data.ToString();
            else if (oid == UniFiOids.UniFiFirmwareVersion) metrics.FirmwareVersion = v.Data.ToString();
            else if (oid == UniFiOids.UniFiMacAddress) metrics.MacAddress = v.Data.ToString();
            else if (oid == UniFiOids.LmSensorsCpuTemp) lmTemp = ConvertSnmpValue<double>(v.Data);
            else if (oid == UniFiOids.UniFiTemperature) unifiTemp = ConvertSnmpValue<double>(v.Data);
        }

        if (lmTemp > 0)
        {
            metrics.Temperature = lmTemp / 1000.0;
        }
        else if (unifiTemp > 0 && unifiTemp < 200)
        {
            metrics.Temperature = unifiTemp;
        }

        metrics.DeviceType = DetermineDeviceType(metrics.Model, metrics.Description);
    }

    private async Task<InterfaceMetadataCache?> GetOrRefreshInterfaceMetadataAsync(IPAddress ip, string cacheKey)
    {
        if (_ifMetadataCache.TryGetValue(cacheKey, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalSeconds < InterfaceMetadataCacheTtlSeconds)
        {
            return cached;
        }

        var descrWalk = await BulkWalkAsync(ip, UniFiOids.IfDescr);
        if (descrWalk.Count == 0) return null;

        var nameWalk = await BulkWalkAsync(ip, UniFiOids.IfName);
        var aliasWalk = await BulkWalkAsync(ip, UniFiOids.IfAlias);
        var typeWalk = await BulkWalkAsync(ip, UniFiOids.IfType);
        var mtuWalk = await BulkWalkAsync(ip, UniFiOids.IfMtu);
        var speedWalk = await BulkWalkAsync(ip, UniFiOids.IfSpeed);
        var highSpeedWalk = await BulkWalkAsync(ip, UniFiOids.IfHighSpeed);
        var physAddrWalk = await BulkWalkAsync(ip, UniFiOids.IfPhysAddress);
        var adminStatusWalk = await BulkWalkAsync(ip, UniFiOids.IfAdminStatus);
        var lastChangeWalk = await BulkWalkAsync(ip, UniFiOids.IfLastChange);

        var metadata = new InterfaceMetadataCache
        {
            CachedAt = DateTime.UtcNow,
            DescrByIdx = IndexByIfIndex(descrWalk, UniFiOids.IfDescr),
            NameByIdx = IndexByIfIndex(nameWalk, UniFiOids.IfName),
            AliasByIdx = IndexByIfIndex(aliasWalk, UniFiOids.IfAlias),
            TypeByIdx = IndexByIfIndex(typeWalk, UniFiOids.IfType),
            MtuByIdx = IndexByIfIndex(mtuWalk, UniFiOids.IfMtu),
            SpeedByIdx = IndexByIfIndex(speedWalk, UniFiOids.IfSpeed),
            HighSpeedByIdx = IndexByIfIndex(highSpeedWalk, UniFiOids.IfHighSpeed),
            PhysAddrByIdx = IndexByIfIndex(physAddrWalk, UniFiOids.IfPhysAddress),
            AdminByIdx = IndexByIfIndex(adminStatusWalk, UniFiOids.IfAdminStatus),
            LastChangeByIdx = IndexByIfIndex(lastChangeWalk, UniFiOids.IfLastChange),
        };

        _ifMetadataCache[cacheKey] = metadata;
        return metadata;
    }

    private static void ParseHostResourcesMemory(List<Variable> storageVars, DeviceMetrics metrics)
    {
        // Parse Host Resources storage table for memory information
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

    private ISnmpMessage GetCachedDiscoveryReport(IPEndPoint endpoint)
    {
        var key = endpoint.ToString();

        if (_discoveryCache.TryGetValue(key, out var cached) &&
            (DateTime.UtcNow - cached.CachedAt).TotalSeconds < DiscoveryCacheTtlSeconds)
        {
            return cached.Report;
        }

        var discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu);
        var report = discovery.GetResponse(_config.Timeout, endpoint);
        _discoveryCache[key] = (report, DateTime.UtcNow);
        return report;
    }

    private IList<Variable> GetV3(IPEndPoint endpoint, List<Variable> variables)
    {
        var report = GetCachedDiscoveryReport(endpoint);
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
        var report = GetCachedDiscoveryReport(endpoint);
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

    private async Task<IList<Variable>> GetMultipleSequentialAsync(IPAddress ip, IList<string> oids)
    {
        var results = new List<Variable>();
        foreach (var oid in oids)
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    var endpoint = new IPEndPoint(ip, _config.Port);
                    var variables = new List<Variable> { new Variable(new ObjectIdentifier(oid)) };
                    return GetV1V2c(endpoint, variables);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SNMP V1 sequential Get failed for {Ip}:{Oid}", ip, oid);
                    return (IList<Variable>)Array.Empty<Variable>();
                }
            });
            foreach (var v in result) results.Add(v);
        }
        return results;
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

    #region Value Conversion and Indexing

    private static bool IsNoSuchOrEndOfMib(Variable v)
    {
        var tc = (int)v.Data.TypeCode;
        return tc >= 0x80;
    }

    private static Dictionary<string, string> IndexByIfIndex(List<Variable> variables, string baseOid)
    {
        var dict = new Dictionary<string, string>();
        var prefix = baseOid + ".";

        foreach (var v in variables)
        {
            var oid = v.Id.ToString();
            if (oid.StartsWith(prefix))
            {
                var idx = oid.Substring(prefix.Length);
                dict[idx] = v.Data.ToString();
            }
        }

        return dict;
    }

    private static readonly System.Text.RegularExpressions.Regex EthNRegex = new(@"^eth\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string ResolveIfName(string? ifAlias, string? ifName)
    {
        if (!string.IsNullOrEmpty(ifName)
            && EthNRegex.IsMatch(ifName)
            && (string.IsNullOrEmpty(ifAlias) || !EthNRegex.IsMatch(ifAlias)))
            return ifName;
        return ifAlias ?? ifName ?? string.Empty;
    }

    private static string? GetString(Dictionary<string, string> dict, string idx)
    {
        return dict.TryGetValue(idx, out var val) ? val : null;
    }

    private static int ParseInt(Dictionary<string, string> dict, string idx)
    {
        if (dict.TryGetValue(idx, out var val) && int.TryParse(val, out var result))
            return result;
        return 0;
    }

    private static long ParseLong(Dictionary<string, string> dict, string idx)
    {
        if (dict.TryGetValue(idx, out var val) && long.TryParse(val, out var result))
            return result;
        return 0;
    }

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

internal sealed class InterfaceMetadataCache
{
    public DateTime CachedAt { get; init; }
    public Dictionary<string, string> DescrByIdx { get; init; } = new();
    public Dictionary<string, string> NameByIdx { get; init; } = new();
    public Dictionary<string, string> AliasByIdx { get; init; } = new();
    public Dictionary<string, string> TypeByIdx { get; init; } = new();
    public Dictionary<string, string> MtuByIdx { get; init; } = new();
    public Dictionary<string, string> SpeedByIdx { get; init; } = new();
    public Dictionary<string, string> HighSpeedByIdx { get; init; } = new();
    public Dictionary<string, string> PhysAddrByIdx { get; init; } = new();
    public Dictionary<string, string> AdminByIdx { get; init; } = new();
    public Dictionary<string, string> LastChangeByIdx { get; init; } = new();
}
