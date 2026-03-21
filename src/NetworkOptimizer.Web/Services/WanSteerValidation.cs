using System.Text.Json;
using System.Text.RegularExpressions;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Validation and formatting helpers for WAN Steering traffic rules.
/// Extracted from the Razor component so they can be unit tested.
/// </summary>
internal static class WanSteerValidation
{
    private static readonly Regex IpRegex = new(
        @"^(\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);

    private static readonly Regex CidrRegex = new(
        @"^(\d{1,3}\.){3}\d{1,3}/\d{1,2}$", RegexOptions.Compiled);

    private static readonly Regex IpRangeRegex = new(
        @"^(\d{1,3}\.){3}\d{1,3}-(\d{1,3}\.){3}\d{1,3}$", RegexOptions.Compiled);

    private static readonly Regex MacRegex = new(
        @"^([0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}$", RegexOptions.Compiled);

    private static readonly Regex PortRegex = new(
        @"^\d{1,5}(-\d{1,5})?$", RegexOptions.Compiled);

    /// <summary>Validate all fields of a traffic class rule.</summary>
    public static List<string> ValidateRule(WanSteerTrafficClass rule)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(rule.Name))
            errors.Add("Name is required.");

        if (string.IsNullOrWhiteSpace(rule.TargetWanKey))
            errors.Add("Target WAN is required.");

        if (rule.Probability <= 0 || rule.Probability > 1)
            errors.Add("Probability must be between 1 and 100%.");

        bool hasSrc = !string.IsNullOrWhiteSpace(rule.SrcCidrsJson) || !string.IsNullOrWhiteSpace(rule.SrcMacsJson);
        bool hasDst = !string.IsNullOrWhiteSpace(rule.DstCidrsJson);
        bool hasSrcPortOrProto = !string.IsNullOrWhiteSpace(rule.SrcPortsJson) || !string.IsNullOrWhiteSpace(rule.Protocol);
        bool hasDstPortOrProto = !string.IsNullOrWhiteSpace(rule.DstPortsJson) || !string.IsNullOrWhiteSpace(rule.Protocol);
        bool hasPorts = !string.IsNullOrWhiteSpace(rule.SrcPortsJson) || !string.IsNullOrWhiteSpace(rule.DstPortsJson);
        bool hasProtocol = !string.IsNullOrWhiteSpace(rule.Protocol);

        if (!hasSrc && !hasDst && !hasSrcPortOrProto && !hasDstPortOrProto)
            errors.Add("At least one match criterion is required: source IP/CIDR/MAC, destination IP/CIDR, or protocol/ports.");

        if (hasPorts && !hasProtocol)
            errors.Add("Protocol (TCP or UDP) is required when ports are specified.");

        if (!string.IsNullOrWhiteSpace(rule.SrcCidrsJson))
            ValidateCidrList(rule.SrcCidrsJson!, "Source", errors);
        if (!string.IsNullOrWhiteSpace(rule.DstCidrsJson))
            ValidateCidrList(rule.DstCidrsJson!, "Destination", errors);

        if (!string.IsNullOrWhiteSpace(rule.SrcMacsJson))
            ValidateMacList(rule.SrcMacsJson!, errors);

        if (!string.IsNullOrWhiteSpace(rule.SrcPortsJson))
            ValidatePortList(rule.SrcPortsJson!, "Source port", errors);
        if (!string.IsNullOrWhiteSpace(rule.DstPortsJson))
            ValidatePortList(rule.DstPortsJson!, "Destination port", errors);

        return errors;
    }

    /// <summary>Validate a JSON array of CIDRs, IPs, and IP ranges.</summary>
    public static void ValidateCidrList(string json, string label, List<string> errors)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<List<string>>(json);
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var trimmed = entry.Trim();
                bool isCidr = CidrRegex.IsMatch(trimmed);
                bool isIp = IpRegex.IsMatch(trimmed);
                bool isRange = IpRangeRegex.IsMatch(trimmed);

                if (!isCidr && !isIp && !isRange)
                {
                    errors.Add($"{label} \"{trimmed}\" is not valid. Use an IP (1.2.3.4), CIDR (1.2.3.0/24), or range (1.2.3.1-1.2.3.50).");
                    return;
                }

                var ipsToCheck = isRange ? trimmed.Split('-') : new[] { isCidr ? trimmed.Split('/')[0] : trimmed };
                foreach (var ip in ipsToCheck)
                {
                    var octets = ip.Split('.');
                    if (octets.Any(o => !int.TryParse(o, out var v) || v < 0 || v > 255))
                    {
                        errors.Add($"{label} \"{trimmed}\" has invalid IP octets (0-255).");
                        return;
                    }
                }

                if (isCidr && int.TryParse(trimmed.Split('/')[1], out var prefix) && (prefix < 0 || prefix > 32))
                {
                    errors.Add($"{label} \"{trimmed}\" has invalid prefix length (0-32).");
                    return;
                }
            }
        }
        catch { errors.Add($"{label} format is invalid."); }
    }

    /// <summary>Validate a JSON array of MAC addresses.</summary>
    public static void ValidateMacList(string json, List<string> errors)
    {
        try
        {
            var macs = JsonSerializer.Deserialize<List<string>>(json);
            if (macs == null) return;
            foreach (var mac in macs)
            {
                if (!MacRegex.IsMatch(mac.Trim()))
                {
                    errors.Add($"MAC address \"{mac}\" is not valid. Use format: aa:bb:cc:dd:ee:ff");
                    return;
                }
            }
        }
        catch { errors.Add("MAC address format is invalid."); }
    }

    /// <summary>Validate a JSON array of ports or port ranges.</summary>
    public static void ValidatePortList(string json, string label, List<string> errors)
    {
        try
        {
            var ports = JsonSerializer.Deserialize<List<string>>(json);
            if (ports == null) return;
            foreach (var port in ports)
            {
                if (!PortRegex.IsMatch(port.Trim()))
                {
                    errors.Add($"{label} \"{port}\" is not valid. Use a number (443) or range (27015-27030).");
                    return;
                }
                foreach (var p in port.Trim().Split('-'))
                {
                    if (int.TryParse(p, out var v) && (v < 1 || v > 65535))
                    {
                        errors.Add($"{label} \"{port}\" is out of range (1-65535).");
                        return;
                    }
                }
            }
        }
        catch { errors.Add($"{label} format is invalid."); }
    }

    /// <summary>Convert newline-separated IPs/CIDRs/ranges to JSON array, appending /32 to bare IPs.</summary>
    public static string? ToJsonArrayNormalizeCidrs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var items = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s =>
            {
                s = s.Trim();
                if (s.Contains('-')) return s;
                if (s.Contains('/')) return s;
                return s + "/32";
            })
            .ToList();
        return items.Count > 0 ? JsonSerializer.Serialize(items) : null;
    }
}
