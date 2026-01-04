using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for agent configurations
/// </summary>
public class AgentRepository : IAgentRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<AgentRepository> _logger;

    public AgentRepository(NetworkOptimizerDbContext context, ILogger<AgentRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Saves a new agent configuration.
    /// </summary>
    /// <param name="config">The agent configuration to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>1 on success.</returns>
    public async Task<int> SaveAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            config.CreatedAt = DateTime.UtcNow;
            config.UpdatedAt = DateTime.UtcNow;
            _context.AgentConfigurations.Add(config);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved agent configuration for {AgentId}", config.AgentId);
            return 1; // Return success indicator
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save agent configuration for {AgentId}", config.AgentId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves an agent configuration by agent ID.
    /// </summary>
    /// <param name="agentId">The unique agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent configuration, or null if not found.</returns>
    public async Task<AgentConfiguration?> GetAgentConfigAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AgentConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent configuration for {AgentId}", agentId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves all agent configurations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of agent configurations ordered by name.</returns>
    public async Task<List<AgentConfiguration>> GetAllAgentConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.AgentConfigurations
                .AsNoTracking()
                .OrderBy(a => a.AgentName)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent configurations");
            throw;
        }
    }

    /// <summary>
    /// Updates an existing agent configuration.
    /// </summary>
    /// <param name="config">The agent configuration with updated values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default)
    {
        try
        {
            config.UpdatedAt = DateTime.UtcNow;
            _context.AgentConfigurations.Update(config);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated agent configuration for {AgentId}", config.AgentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update agent configuration for {AgentId}", config.AgentId);
            throw;
        }
    }

    /// <summary>
    /// Deletes an agent configuration by agent ID.
    /// </summary>
    /// <param name="agentId">The agent ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteAgentConfigAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _context.AgentConfigurations
                .FirstOrDefaultAsync(a => a.AgentId == agentId, cancellationToken);
            if (config != null)
            {
                _context.AgentConfigurations.Remove(config);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted agent configuration for {AgentId}", agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete agent configuration for {AgentId}", agentId);
            throw;
        }
    }
}
