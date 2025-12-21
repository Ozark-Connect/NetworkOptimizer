using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for agent configurations
/// </summary>
public interface IAgentRepository
{
    Task<int> SaveAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default);
    Task<AgentConfiguration?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);
    Task<List<AgentConfiguration>> GetAllAgentConfigsAsync(CancellationToken cancellationToken = default);
    Task UpdateAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);
}
