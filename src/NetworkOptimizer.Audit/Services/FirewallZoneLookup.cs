using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Services;

/// <summary>
/// Provides lookup and validation for firewall zones.
/// Maps zone IDs to zone types and validates assumptions about zone assignments.
/// </summary>
public class FirewallZoneLookup
{
    private readonly Dictionary<string, UniFiFirewallZone> _zonesById;
    private readonly Dictionary<string, UniFiFirewallZone> _zonesByKey;
    private readonly ILogger? _logger;

    /// <summary>
    /// All zones indexed by ID.
    /// </summary>
    public IReadOnlyDictionary<string, UniFiFirewallZone> ZonesById => _zonesById;

    /// <summary>
    /// All zones indexed by zone_key (internal, external, dmz, etc.).
    /// </summary>
    public IReadOnlyDictionary<string, UniFiFirewallZone> ZonesByKey => _zonesByKey;

    /// <summary>
    /// Whether zone data was successfully loaded.
    /// </summary>
    public bool HasZoneData => _zonesById.Count > 0;

    /// <summary>
    /// Validation warnings generated during construction or validation.
    /// </summary>
    public List<string> ValidationWarnings { get; } = [];

    public FirewallZoneLookup(IEnumerable<UniFiFirewallZone>? zones, ILogger? logger = null)
    {
        _logger = logger;
        _zonesById = new Dictionary<string, UniFiFirewallZone>(StringComparer.OrdinalIgnoreCase);
        _zonesByKey = new Dictionary<string, UniFiFirewallZone>(StringComparer.OrdinalIgnoreCase);

        if (zones == null)
        {
            _logger?.LogDebug("No firewall zones provided - zone lookup will be unavailable");
            return;
        }

        foreach (var zone in zones)
        {
            if (!string.IsNullOrEmpty(zone.Id))
            {
                _zonesById[zone.Id] = zone;
            }

            if (!string.IsNullOrEmpty(zone.ZoneKey))
            {
                _zonesByKey[zone.ZoneKey] = zone;
            }
        }

        _logger?.LogDebug("Loaded {Count} firewall zones: {ZoneKeys}",
            _zonesById.Count,
            string.Join(", ", _zonesByKey.Keys));
    }

    /// <summary>
    /// Get the zone for a given zone ID.
    /// </summary>
    public UniFiFirewallZone? GetZoneById(string? zoneId)
    {
        if (string.IsNullOrEmpty(zoneId))
            return null;

        return _zonesById.TryGetValue(zoneId, out var zone) ? zone : null;
    }

    /// <summary>
    /// Get the zone for a given zone key (internal, external, dmz, etc.).
    /// </summary>
    public UniFiFirewallZone? GetZoneByKey(string zoneKey)
    {
        return _zonesByKey.TryGetValue(zoneKey, out var zone) ? zone : null;
    }

    /// <summary>
    /// Get the zone key for a given zone ID.
    /// Returns null if zone not found.
    /// </summary>
    public string? GetZoneKey(string? zoneId)
    {
        return GetZoneById(zoneId)?.ZoneKey;
    }

    /// <summary>
    /// Check if a zone ID belongs to the DMZ zone.
    /// </summary>
    public bool IsDmzZone(string? zoneId)
    {
        var zoneKey = GetZoneKey(zoneId);
        return string.Equals(zoneKey, FirewallZoneKeys.Dmz, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if a zone ID belongs to the Hotspot zone.
    /// </summary>
    public bool IsHotspotZone(string? zoneId)
    {
        var zoneKey = GetZoneKey(zoneId);
        return string.Equals(zoneKey, FirewallZoneKeys.Hotspot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if a zone ID belongs to the External/WAN zone.
    /// </summary>
    public bool IsExternalZone(string? zoneId)
    {
        var zoneKey = GetZoneKey(zoneId);
        return string.Equals(zoneKey, FirewallZoneKeys.External, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if a zone ID belongs to the Internal zone.
    /// </summary>
    public bool IsInternalZone(string? zoneId)
    {
        var zoneKey = GetZoneKey(zoneId);
        return string.Equals(zoneKey, FirewallZoneKeys.Internal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the External zone ID.
    /// </summary>
    public string? GetExternalZoneId()
    {
        return GetZoneByKey(FirewallZoneKeys.External)?.Id;
    }

    /// <summary>
    /// Get the DMZ zone ID.
    /// </summary>
    public string? GetDmzZoneId()
    {
        return GetZoneByKey(FirewallZoneKeys.Dmz)?.Id;
    }

    /// <summary>
    /// Get the Hotspot zone ID.
    /// </summary>
    public string? GetHotspotZoneId()
    {
        return GetZoneByKey(FirewallZoneKeys.Hotspot)?.Id;
    }

    /// <summary>
    /// Validate that a WAN network's firewall_zone_id maps to the External zone.
    /// Adds a warning if the assumption doesn't hold.
    /// </summary>
    /// <param name="wanNetworkName">Name of the WAN network for logging</param>
    /// <param name="wanZoneId">The firewall_zone_id from the WAN network config</param>
    /// <returns>True if valid or no zone data; false if mismatch detected</returns>
    public bool ValidateWanZoneAssumption(string? wanNetworkName, string? wanZoneId)
    {
        if (!HasZoneData || string.IsNullOrEmpty(wanZoneId))
            return true; // Can't validate without data

        var zone = GetZoneById(wanZoneId);
        if (zone == null)
        {
            var warning = $"WAN network '{wanNetworkName}' has firewall_zone_id '{wanZoneId}' but this zone was not found in the zone lookup";
            ValidationWarnings.Add(warning);
            _logger?.LogWarning(warning);
            return false;
        }

        if (!string.Equals(zone.ZoneKey, FirewallZoneKeys.External, StringComparison.OrdinalIgnoreCase))
        {
            var warning = $"WAN network '{wanNetworkName}' is assigned to zone '{zone.Name}' (zone_key: {zone.ZoneKey}) but expected zone_key 'external'";
            ValidationWarnings.Add(warning);
            _logger?.LogWarning(warning);
            return false;
        }

        _logger?.LogDebug("WAN network '{WanNetwork}' correctly assigned to External zone", wanNetworkName);
        return true;
    }

    /// <summary>
    /// Validate that our external zone ID matches what we'd get from the zone lookup.
    /// Call this after DetermineExternalZoneId() to cross-check.
    /// </summary>
    /// <param name="determinedExternalZoneId">The external zone ID determined from WAN network</param>
    /// <returns>True if matches or no zone data; false if mismatch</returns>
    public bool ValidateExternalZoneId(string? determinedExternalZoneId)
    {
        if (!HasZoneData || string.IsNullOrEmpty(determinedExternalZoneId))
            return true;

        var expectedExternalZoneId = GetExternalZoneId();
        if (expectedExternalZoneId == null)
        {
            var warning = "Zone lookup does not contain an 'external' zone - this is unexpected";
            ValidationWarnings.Add(warning);
            _logger?.LogWarning(warning);
            return false;
        }

        if (!string.Equals(determinedExternalZoneId, expectedExternalZoneId, StringComparison.OrdinalIgnoreCase))
        {
            var warning = $"Determined external zone ID '{determinedExternalZoneId}' does not match zone lookup external zone ID '{expectedExternalZoneId}'";
            ValidationWarnings.Add(warning);
            _logger?.LogWarning(warning);
            return false;
        }

        return true;
    }
}
