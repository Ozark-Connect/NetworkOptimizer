using System.Net;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class SnmpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/monitoring/snmp/oid-check", async (
            TestOidRequest request,
            UniFiConnectionService connectionService,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            ICredentialProtectionService credentialProtection,
            AgentSnmpQueryService agentSnmpQuery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var oidLog = loggerFactory.CreateLogger("SnmpOidCheck");
            if (string.IsNullOrWhiteSpace(request.DeviceMac) || string.IsNullOrWhiteSpace(request.Oid))
                return Results.BadRequest(new TestOidResponse { ErrorMessage = "Device MAC and OID are required." });

            var hasAgent = !siteContext.IsDefault && agentSnmpQuery.HasAgentForSite(siteContext.Slug);
            oidLog.LogDebug(
                "OID test: site={Slug} isDefault={IsDefault} hasAgent={HasAgent} mac={Mac} oid={Oid}",
                siteContext.Slug, siteContext.IsDefault, hasAgent, request.DeviceMac, request.Oid);

            // Agent-covered site: the server can't reach the device directly, so run the GET
            // through the site's agent. Main and any site without an online agent fall through
            // to the direct poll below (unchanged).
            if (hasAgent)
            {
                var agentDeviceIp = await ResolveDeviceIpAsync(request.DeviceMac, connectionService, ct);
                oidLog.LogDebug("OID test: agent path, resolved device IP={Ip} (connected={Connected})",
                    agentDeviceIp ?? "<null>", connectionService.IsConnected);
                if (agentDeviceIp == null)
                    return Results.BadRequest(new TestOidResponse { ErrorMessage = "Could not resolve device IP." });

                var agentResult = await agentSnmpQuery.QueryAsync(
                    siteContext.Slug, agentDeviceIp, request.Oid, TimeSpan.FromSeconds(10), ct);
                oidLog.LogDebug("OID test: agent result success={Success} value={Value} error={Error}",
                    agentResult?.Success, agentResult?.Value, agentResult?.Error ?? "<null result>");
                if (agentResult != null)
                    return Results.Ok(agentResult.Success
                        ? new TestOidResponse { Success = true, Value = agentResult.Value }
                        : new TestOidResponse { ErrorMessage = string.IsNullOrEmpty(agentResult.Error) ? "No response." : agentResult.Error });
                // agentResult == null: the agent's tunnel dropped between the check and the send;
                // fall through to a direct attempt rather than failing outright.
            }

            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings == null)
                return Results.BadRequest(new TestOidResponse { ErrorMessage = "Monitoring not configured." });

            var poller = BuildPollerFromSettings(settings, credentialProtection, loggerFactory);
            if (poller == null)
                return Results.BadRequest(new TestOidResponse { ErrorMessage = "SNMP credentials not configured." });

            var deviceIp = await ResolveDeviceIpAsync(request.DeviceMac, connectionService, ct);
            if (deviceIp == null)
                return Results.BadRequest(new TestOidResponse { ErrorMessage = "Could not resolve device IP." });

            if (!IPAddress.TryParse(deviceIp, out var ip))
                return Results.BadRequest(new TestOidResponse { ErrorMessage = $"Invalid IP address: {deviceIp}" });

            try
            {
                var result = await poller.GetAsync<string>(ip, request.Oid);
                if (result == null)
                    return Results.Ok(new TestOidResponse { ErrorMessage = "No response (OID may not exist on this device)." });

                return Results.Ok(new TestOidResponse
                {
                    Success = true,
                    Value = result
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new TestOidResponse { ErrorMessage = $"SNMP error: {ex.Message}" });
            }
        });

        app.MapGet("/api/monitoring/snmp/custom-oids/{deviceMac}", async (
            string deviceMac,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            CancellationToken ct) =>
        {
            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var oids = await db.CustomOidConfigurations
                .Where(c => c.DeviceMac == deviceMac)
                .OrderBy(c => c.FieldName)
                .Select(c => new CustomOidDto
                {
                    Id = c.Id,
                    Oid = c.Oid,
                    FieldName = c.FieldName,
                    ValueType = c.ValueType,
                    Scope = c.Scope,
                    Enabled = c.Enabled,
                    Description = c.Description
                })
                .ToListAsync(ct);
            return Results.Ok(oids);
        });

        app.MapPost("/api/monitoring/snmp/custom-oids", async (
            SaveCustomOidRequest request,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.DeviceMac) ||
                string.IsNullOrWhiteSpace(request.Oid) ||
                string.IsNullOrWhiteSpace(request.FieldName))
                return Results.BadRequest("DeviceMac, Oid, and FieldName are required.");

            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var now = DateTime.UtcNow;

            if (request.Id > 0)
            {
                var existing = await db.CustomOidConfigurations.FindAsync(new object[] { request.Id }, ct);
                if (existing == null) return Results.NotFound();

                existing.Oid = request.Oid.Trim();
                existing.FieldName = request.FieldName.Trim();
                existing.ValueType = request.ValueType;
                existing.Scope = request.Scope;
                existing.Enabled = request.Enabled;
                existing.Description = request.Description?.Trim();
                existing.UpdatedAt = now;
            }
            else
            {
                db.CustomOidConfigurations.Add(new CustomOidConfiguration
                {
                    DeviceMac = request.DeviceMac.Trim(),
                    Oid = request.Oid.Trim(),
                    FieldName = request.FieldName.Trim(),
                    ValueType = request.ValueType,
                    Scope = request.Scope,
                    Enabled = request.Enabled,
                    Description = request.Description?.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        app.MapDelete("/api/monitoring/snmp/custom-oids/{id:int}", async (
            int id,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            CancellationToken ct) =>
        {
            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var entry = await db.CustomOidConfigurations.FindAsync(new object[] { id }, ct);
            if (entry == null) return Results.NotFound();

            db.CustomOidConfigurations.Remove(entry);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });
    }

    public static SnmpPoller? BuildPollerFromSettings(
        MonitoringSettings settings,
        ICredentialProtectionService credentialProtection,
        ILoggerFactory loggerFactory)
    {
        try
        {
            var cfg = new SnmpConfiguration();
            if (settings.SnmpVersion == SnmpVersionSetting.V2c)
            {
                cfg.Version = SnmpVersion.V2c;
                cfg.Community = string.IsNullOrEmpty(settings.SnmpCommunity)
                    ? string.Empty
                    : credentialProtection.Decrypt(settings.SnmpCommunity);
                if (string.IsNullOrEmpty(cfg.Community)) return null;
            }
            else
            {
                cfg.Version = SnmpVersion.V3;
                cfg.Username = settings.SnmpV3Username ?? string.Empty;
                cfg.AuthenticationPassword = string.IsNullOrEmpty(settings.SnmpV3AuthPassword)
                    ? string.Empty
                    : credentialProtection.Decrypt(settings.SnmpV3AuthPassword);
                if (string.IsNullOrEmpty(cfg.Username)) return null;
            }
            return new SnmpPoller(cfg, loggerFactory.CreateLogger<SnmpPoller>());
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> ResolveDeviceIpAsync(
        string deviceMac,
        UniFiConnectionService connectionService,
        CancellationToken ct = default)
    {
        if (!connectionService.IsConnected || connectionService.Client == null)
            return null;

        try
        {
            var devices = await connectionService.Client.GetDevicesAsync(ct);
            var device = devices.FirstOrDefault(d =>
                string.Equals(d.Mac, deviceMac, StringComparison.OrdinalIgnoreCase));
            if (device == null) return null;

            if (device.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway)
            {
                var networks = await connectionService.Client.GetNetworkConfigsAsync(ct);
                var defaultLan = networks
                    .Where(n => n.Purpose == "corporate" && n.Enabled)
                    .OrderBy(n => n.Vlan ?? 0)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(defaultLan?.DhcpdGateway))
                    return defaultLan!.DhcpdGateway;
                if (!string.IsNullOrEmpty(defaultLan?.IpSubnet))
                    return defaultLan!.IpSubnet.Split('/')[0];
            }
            return device.Ip;
        }
        catch
        {
            return null;
        }
    }
}

public record TestOidRequest
{
    public string DeviceMac { get; init; } = string.Empty;
    public string Oid { get; init; } = string.Empty;
}

public record TestOidResponse
{
    public bool Success { get; init; }
    public string? Value { get; init; }
    public string? ErrorMessage { get; init; }
}

public record CustomOidDto
{
    public int Id { get; init; }
    public string Oid { get; init; } = string.Empty;
    public string FieldName { get; init; } = string.Empty;
    public CustomOidValueType ValueType { get; init; }
    public CustomOidScope Scope { get; init; }
    public bool Enabled { get; init; }
    public string? Description { get; init; }
}

public record SaveCustomOidRequest
{
    public int Id { get; init; }
    public string DeviceMac { get; init; } = string.Empty;
    public string Oid { get; init; } = string.Empty;
    public string FieldName { get; init; } = string.Empty;
    public CustomOidValueType ValueType { get; init; }
    public CustomOidScope Scope { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
}
