using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for monitoring interfaces (ONT/modem management access routes
/// deployed to the gateway).
/// </summary>
public class MonitoringInterfaceRepository : IMonitoringInterfaceRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<MonitoringInterfaceRepository> _logger;

    public MonitoringInterfaceRepository(NetworkOptimizerDbContext context, ILogger<MonitoringInterfaceRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<MonitoringInterface>> GetMonitoringInterfacesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .OrderBy(m => m.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interfaces");
            throw;
        }
    }

    public async Task<MonitoringInterface?> GetMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interface {Id}", id);
            throw;
        }
    }

    public async Task<MonitoringInterface?> GetByEffectiveIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.MonitoringInterfaces
                .AsNoTracking()
                .FirstOrDefaultAsync(m => (m.AliasIp ?? m.TargetIp) == ip, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get monitoring interface for effective IP {Ip}", ip);
            throw;
        }
    }

    public async Task SaveMonitoringInterfaceAsync(MonitoringInterface config, CancellationToken cancellationToken = default)
    {
        // Uniqueness across rows (effective IP, GatewayLocalIp, cross-set disjointness)
        // can't be expressed as simple per-column indexes, so it's checked and saved inside
        // one transaction. SQLite's default (non-deferred) transaction is BEGIN IMMEDIATE,
        // which serializes writers - this closes the real race where two Blazor Server
        // circuits (e.g. two browser tabs) could otherwise both pass an app-level check
        // before either saves.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var others = await _context.MonitoringInterfaces
                .AsNoTracking()
                .Where(m => m.Id != config.Id)
                .ToListAsync(cancellationToken);

            var effectiveIp = config.AliasIp ?? config.TargetIp;
            foreach (var other in others)
            {
                var otherEffectiveIp = other.AliasIp ?? other.TargetIp;

                if (other.Name == config.Name)
                    throw new InvalidOperationException(
                        $"An interface named \"{config.Name}\" already exists. Interface names must be unique " +
                        "(the boot script and macvlan interface are both keyed on the name) - pick a different one.");

                if (otherEffectiveIp == effectiveIp)
                    throw new InvalidOperationException(
                        $"{effectiveIp} is already the polled IP for monitoring interface \"{other.Name}\". " +
                        "Two interfaces can't poll the same address - use a different Alias IP.");

                if (other.GatewayLocalIp == config.GatewayLocalIp)
                    throw new InvalidOperationException(
                        $"{config.GatewayLocalIp} is already the gateway-local IP for monitoring interface \"{other.Name}\". " +
                        "Pick a different gateway-local IP.");

                if (config.AliasIp != null &&
                    (config.AliasIp == other.TargetIp || config.AliasIp == other.GatewayLocalIp))
                    throw new InvalidOperationException(
                        $"Alias IP {config.AliasIp} collides with monitoring interface \"{other.Name}\"'s target or gateway-local IP.");

                if (config.TargetIp == other.AliasIp || config.GatewayLocalIp == other.AliasIp)
                    throw new InvalidOperationException(
                        $"{(config.TargetIp == other.AliasIp ? config.TargetIp : config.GatewayLocalIp)} " +
                        $"collides with monitoring interface \"{other.Name}\"'s alias IP.");

                // A gateway-local (macvlan) address must not equal another interface's polled
                // target, or that interface would silently poll this gateway macvlan instead of
                // its real device (local addresses win over routes). Check both directions.
                if (config.GatewayLocalIp == other.TargetIp)
                    throw new InvalidOperationException(
                        $"Gateway-local IP {config.GatewayLocalIp} collides with monitoring interface \"{other.Name}\"'s target IP. " +
                        "Pick a different gateway-local IP - otherwise that interface would poll this gateway address instead of its device.");

                if (config.TargetIp == other.GatewayLocalIp)
                    throw new InvalidOperationException(
                        $"Target IP {config.TargetIp} collides with monitoring interface \"{other.Name}\"'s gateway-local IP. " +
                        "This interface would poll that gateway address instead of a real device - use a different target or alias IP.");
            }

            if (config.Id > 0)
            {
                var existing = await _context.MonitoringInterfaces
                    .FirstOrDefaultAsync(m => m.Id == config.Id, cancellationToken);
                if (existing != null)
                {
                    existing.Name = config.Name;
                    existing.WanIfName = config.WanIfName;
                    existing.WanVlanId = config.WanVlanId;
                    existing.WanKey = config.WanKey;
                    existing.TargetIp = config.TargetIp;
                    existing.SubnetPrefix = config.SubnetPrefix;
                    existing.GatewayLocalIp = config.GatewayLocalIp;
                    existing.AliasIp = config.AliasIp;
                    existing.SnatEnabled = config.SnatEnabled;
                    existing.WatchdogIntervalMinutes = config.WatchdogIntervalMinutes;
                    existing.IsManuallyDeployed = config.IsManuallyDeployed;
                    existing.LastError = config.LastError;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
            }
            else
            {
                config.CreatedAt = DateTime.UtcNow;
                config.UpdatedAt = DateTime.UtcNow;
                _context.MonitoringInterfaces.Add(config);
            }

            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _logger.LogDebug("Saved monitoring interface {Name} ({TargetIp})", config.Name, config.TargetIp);
        }
        catch (InvalidOperationException)
        {
            throw; // Uniqueness violation - already a clear, user-facing message.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save monitoring interface {Name}", config.Name);
            throw;
        }
    }

    public async Task DeleteMonitoringInterfaceAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.MonitoringInterfaces.FindAsync([id], cancellationToken);
            if (config != null)
            {
                _context.MonitoringInterfaces.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogDebug("Deleted monitoring interface {Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete monitoring interface {Id}", id);
            throw;
        }
    }
}
