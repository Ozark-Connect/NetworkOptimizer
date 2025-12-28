# Agents Feature Cleanup TODO

## Issues to Fix

### 1. Use Shared SSH Settings
- [ ] Remove `NetworkOptimizer.Agents.Models.SshCredentials` class
- [ ] Modify `AgentDeployer` to accept `UniFiSshService` or `GatewaySshSettings` instead of per-agent credentials
- [ ] Update `AgentService` to get SSH settings from `UniFiSshService` (same as SQM deployment)

### 2. Remove Redundant UDM/UCG Agent Type
- [ ] Remove UDM/UCG agent type from `Agents.razor` UI
- [ ] Remove `install-udm.sh.template` and related UDM templates
- [ ] Keep only Linux agent type (for non-UniFi servers)
- [ ] SQM deployment already handles gateway script deployment

### 3. Implement AgentService Methods
Current state: mostly TODOs returning mock data

- [ ] `GetAgentSummaryAsync()` - Query real data from repository
- [ ] `GetAllAgentsAsync()` - Return actual agent configurations
- [ ] `TestConnectionAsync()` - Use shared SSH service to test
- [ ] `DeployAgentAsync()` - Wire up to `AgentDeployer`
- [ ] `RemoveAgentAsync()` - Implement agent removal via SSH
- [ ] `RestartAgentAsync()` - Implement agent restart via SSH

### 4. Clean Up Duplicate Models
- [ ] Consolidate `NetworkOptimizer.Agents.Models.AgentConfiguration` (deployment) with `NetworkOptimizer.Storage.Models.AgentConfiguration` (DB entity)
- [ ] Or clearly separate their purposes with better naming

### 5. UI Polish
- [ ] Remove SNMP agent type placeholder (not implemented)
- [ ] Add proper error handling and user feedback
- [ ] Wire up AgentStatusTable to real data

## Files to Modify
- `src/NetworkOptimizer.Agents/AgentDeployer.cs`
- `src/NetworkOptimizer.Agents/Models/SshCredentials.cs` (delete)
- `src/NetworkOptimizer.Web/Services/AgentService.cs`
- `src/NetworkOptimizer.Web/Components/Pages/Agents.razor`
