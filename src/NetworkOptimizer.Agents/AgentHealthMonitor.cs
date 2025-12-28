using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Agents.Models;
using System.Data;

namespace NetworkOptimizer.Agents;

/// <summary>
/// Monitors agent health and tracks heartbeats
/// </summary>
public class AgentHealthMonitor : IDisposable
{
    private readonly ILogger<AgentHealthMonitor> _logger;
    private readonly string _connectionString;
    private readonly TimeSpan _offlineThreshold;

    public AgentHealthMonitor(
        ILogger<AgentHealthMonitor> logger,
        string databasePath,
        TimeSpan? offlineThreshold = null)
    {
        _logger = logger;
        _connectionString = $"Data Source={databasePath}";
        _offlineThreshold = offlineThreshold ?? TimeSpan.FromMinutes(5);

        InitializeDatabase();
    }

    /// <summary>
    /// Records a heartbeat from an agent
    /// </summary>
    public async Task RecordHeartbeatAsync(string agentId, string deviceName, AgentType agentType, Dictionary<string, string>? metadata = null)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var metadataJson = metadata != null ? System.Text.Json.JsonSerializer.Serialize(metadata) : null;

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO agent_heartbeats (agent_id, device_name, agent_type, last_heartbeat, metadata)
                VALUES (@agentId, @deviceName, @agentType, @lastHeartbeat, @metadata)";

            command.Parameters.AddWithValue("@agentId", agentId);
            command.Parameters.AddWithValue("@deviceName", deviceName);
            command.Parameters.AddWithValue("@agentType", agentType.ToString());
            command.Parameters.AddWithValue("@lastHeartbeat", DateTime.UtcNow);
            command.Parameters.AddWithValue("@metadata", (object?)metadataJson ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();

            _logger.LogDebug("Recorded heartbeat for agent {AgentId} ({DeviceName})", agentId, deviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record heartbeat for agent {AgentId}", agentId);
            throw;
        }
    }

    /// <summary>
    /// Gets the status of a specific agent
    /// </summary>
    public async Task<AgentStatus?> GetAgentStatusAsync(string agentId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT agent_id, device_name, agent_type, last_heartbeat, metadata, first_seen
                FROM agent_heartbeats
                WHERE agent_id = @agentId";

            command.Parameters.AddWithValue("@agentId", agentId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadAgentStatus(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get status for agent {AgentId}", agentId);
            throw;
        }
    }

    /// <summary>
    /// Gets all registered agents
    /// </summary>
    public async Task<List<AgentStatus>> GetAllAgentsAsync()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT agent_id, device_name, agent_type, last_heartbeat, metadata, first_seen
                FROM agent_heartbeats
                ORDER BY last_heartbeat DESC";

            var agents = new List<AgentStatus>();

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                agents.Add(ReadAgentStatus(reader));
            }

            return agents;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all agents");
            throw;
        }
    }

    /// <summary>
    /// Gets all offline agents
    /// </summary>
    public async Task<List<AgentStatus>> GetOfflineAgentsAsync()
    {
        var allAgents = await GetAllAgentsAsync();
        return allAgents.Where(a => !a.IsOnline).ToList();
    }

    /// <summary>
    /// Gets all online agents
    /// </summary>
    public async Task<List<AgentStatus>> GetOnlineAgentsAsync()
    {
        var allAgents = await GetAllAgentsAsync();
        return allAgents.Where(a => a.IsOnline).ToList();
    }

    /// <summary>
    /// Removes an agent from monitoring
    /// </summary>
    public async Task RemoveAgentAsync(string agentId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_heartbeats WHERE agent_id = @agentId";
            command.Parameters.AddWithValue("@agentId", agentId);

            var rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Removed agent {AgentId} from monitoring", agentId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove agent {AgentId}", agentId);
            throw;
        }
    }

    /// <summary>
    /// Gets statistics about agent health
    /// </summary>
    public async Task<AgentHealthStats> GetHealthStatsAsync()
    {
        var allAgents = await GetAllAgentsAsync();

        return new AgentHealthStats
        {
            TotalAgents = allAgents.Count,
            OnlineAgents = allAgents.Count(a => a.IsOnline),
            OfflineAgents = allAgents.Count(a => !a.IsOnline),
            AgentsByType = allAgents
                .GroupBy(a => a.AgentType)
                .ToDictionary(g => g.Key, g => g.Count()),
            OldestHeartbeat = allAgents.Any() ? allAgents.Min(a => a.LastHeartbeat) : null,
            NewestHeartbeat = allAgents.Any() ? allAgents.Max(a => a.LastHeartbeat) : null
        };
    }

    /// <summary>
    /// Cleans up old heartbeat records
    /// </summary>
    public async Task CleanupOldRecordsAsync(TimeSpan retentionPeriod)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var cutoffDate = DateTime.UtcNow - retentionPeriod;

            var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM agent_heartbeats WHERE last_heartbeat < @cutoffDate";
            command.Parameters.AddWithValue("@cutoffDate", cutoffDate);

            var rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                _logger.LogInformation("Cleaned up {Count} old agent records", rowsAffected);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup old records");
            throw;
        }
    }

    /// <summary>
    /// Initializes the SQLite database
    /// </summary>
    private void InitializeDatabase()
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS agent_heartbeats (
                    agent_id TEXT PRIMARY KEY,
                    device_name TEXT NOT NULL,
                    agent_type TEXT NOT NULL,
                    last_heartbeat TEXT NOT NULL,
                    first_seen TEXT NOT NULL DEFAULT (datetime('now')),
                    metadata TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_last_heartbeat ON agent_heartbeats(last_heartbeat);
                CREATE INDEX IF NOT EXISTS idx_agent_type ON agent_heartbeats(agent_type);
            ";

            command.ExecuteNonQuery();

            _logger.LogDebug("Initialized agent health database");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    /// <summary>
    /// Reads an AgentStatus from a data reader
    /// </summary>
    private AgentStatus ReadAgentStatus(SqliteDataReader reader)
    {
        var agentId = reader.GetString(0);
        var deviceName = reader.GetString(1);
        var agentTypeStr = reader.GetString(2);
        var lastHeartbeat = reader.GetDateTime(3);
        var metadataJson = reader.IsDBNull(4) ? null : reader.GetString(4);
        var firstSeen = reader.GetDateTime(5);

        var agentType = Enum.Parse<AgentType>(agentTypeStr);

        var metadata = metadataJson != null
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson)
            : null;

        var timeSinceLastHeartbeat = DateTime.UtcNow - lastHeartbeat;
        var isOnline = timeSinceLastHeartbeat <= _offlineThreshold;

        return new AgentStatus
        {
            AgentId = agentId,
            DeviceName = deviceName,
            AgentType = agentType,
            LastHeartbeat = lastHeartbeat,
            FirstSeen = firstSeen,
            IsOnline = isOnline,
            SecondsSinceLastHeartbeat = (int)timeSinceLastHeartbeat.TotalSeconds,
            Metadata = metadata ?? new Dictionary<string, string>()
        };
    }

    public void Dispose()
    {
        // Clean up any resources if needed
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Current status of an agent
/// </summary>
public class AgentStatus
{
    public required string AgentId { get; set; }
    public required string DeviceName { get; set; }
    public required AgentType AgentType { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public DateTime FirstSeen { get; set; }
    public bool IsOnline { get; set; }
    public int SecondsSinceLastHeartbeat { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Overall health statistics for all agents
/// </summary>
public class AgentHealthStats
{
    public int TotalAgents { get; set; }
    public int OnlineAgents { get; set; }
    public int OfflineAgents { get; set; }
    public Dictionary<AgentType, int> AgentsByType { get; set; } = new();
    public DateTime? OldestHeartbeat { get; set; }
    public DateTime? NewestHeartbeat { get; set; }

    public double OnlinePercentage => TotalAgents > 0
        ? (double)OnlineAgents / TotalAgents * 100
        : 0;
}
