using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

public interface ILocalRepository
{
    // Audit History
    Task<int> SaveAuditResultAsync(AuditResult audit, CancellationToken cancellationToken = default);
    Task<AuditResult?> GetAuditResultAsync(int auditId, CancellationToken cancellationToken = default);
    Task<List<AuditResult>> GetAuditHistoryAsync(string? deviceId = null, int limit = 100, CancellationToken cancellationToken = default);
    Task DeleteOldAuditsAsync(DateTime olderThan, CancellationToken cancellationToken = default);

    // SQM Baselines
    Task<int> SaveSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task<SqmBaseline?> GetSqmBaselineAsync(string deviceId, string interfaceId, CancellationToken cancellationToken = default);
    Task<List<SqmBaseline>> GetAllSqmBaselinesAsync(string? deviceId = null, CancellationToken cancellationToken = default);
    Task UpdateSqmBaselineAsync(SqmBaseline baseline, CancellationToken cancellationToken = default);
    Task DeleteSqmBaselineAsync(int baselineId, CancellationToken cancellationToken = default);

    // Agent Configuration
    Task<int> SaveAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default);
    Task<AgentConfiguration?> GetAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);
    Task<List<AgentConfiguration>> GetAllAgentConfigsAsync(CancellationToken cancellationToken = default);
    Task UpdateAgentConfigAsync(AgentConfiguration config, CancellationToken cancellationToken = default);
    Task DeleteAgentConfigAsync(string agentId, CancellationToken cancellationToken = default);

    // License Information
    Task<int> SaveLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default);
    Task<LicenseInfo?> GetLicenseAsync(CancellationToken cancellationToken = default);
    Task UpdateLicenseAsync(LicenseInfo license, CancellationToken cancellationToken = default);
}
