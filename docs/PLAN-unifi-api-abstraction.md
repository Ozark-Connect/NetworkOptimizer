# Plan: UniFi API Abstraction Layer

## Background

Currently, the Network Optimizer "hijacks" the UniFi Controller's internal web UI API endpoints. This works but:
- API endpoints can change between UniFi versions without notice
- No official documentation or stability guarantees
- Rate limiting and session management mimic browser behavior
- Device data structure may change unexpectedly

When/if Ubiquiti provides a proper documented API, we need to easily swap implementations.

## Objective

Create an abstraction layer that:
1. Decouples business logic from UniFi API implementation details
2. Enables easy swap to official API when available
3. Maintains backward compatibility with current "web UI" approach
4. Provides a clean interface for device/client/network data

## Current Architecture

```
┌─────────────────────┐     ┌──────────────────────┐
│  ConfigAuditEngine  │────>│  SecurityAuditEngine │
└─────────────────────┘     └──────────────────────┘
          │                           │
          v                           v
┌─────────────────────────────────────────────────┐
│               UniFiApiClient                     │
│  (Direct HTTP calls to controller endpoints)     │
└─────────────────────────────────────────────────┘
          │
          v
┌─────────────────────────────────────────────────┐
│         UniFi Controller (Web UI API)            │
└─────────────────────────────────────────────────┘
```

## Proposed Architecture

```
┌─────────────────────┐     ┌──────────────────────┐
│  ConfigAuditEngine  │────>│  SecurityAuditEngine │
└─────────────────────┘     └──────────────────────┘
          │                           │
          v                           v
┌─────────────────────────────────────────────────┐
│            IUniFiDataProvider                    │
│  (Abstract interface for UniFi data)             │
└─────────────────────────────────────────────────┘
          │
          ├──────────────────┐
          │                  │
          v                  v
┌───────────────────┐  ┌─────────────────────┐
│ WebUIDataProvider │  │ OfficialAPIProvider │
│ (Current impl)    │  │ (Future impl)       │
└───────────────────┘  └─────────────────────┘
          │                       │
          v                       v
┌─────────────────┐    ┌────────────────────────┐
│  Controller     │    │  UniFi Official API    │
│  (Web UI API)   │    │  (When available)      │
└─────────────────┘    └────────────────────────┘
```

## Implementation Steps

### Phase 1: Define Core Interfaces

**File: `src/NetworkOptimizer.UniFi/Abstractions/IUniFiDataProvider.cs`**

```csharp
public interface IUniFiDataProvider
{
    // Connection
    Task<bool> ConnectAsync(string host, string username, string password);
    Task DisconnectAsync();
    bool IsConnected { get; }

    // Site discovery
    Task<List<UniFiSite>> GetSitesAsync();

    // Device data
    Task<List<UniFiDevice>> GetDevicesAsync(string siteId);
    Task<List<UniFiClient>> GetClientsAsync(string siteId);
    Task<List<UniFiNetwork>> GetNetworksAsync(string siteId);

    // Fingerprint database
    Task<UniFiFingerprintDatabase?> GetFingerprintDatabaseAsync();
}
```

### Phase 2: Define Domain Models

Create clean domain models separate from JSON response DTOs:

**File: `src/NetworkOptimizer.UniFi/Domain/`**

- `UniFiDevice.cs` - Normalized device info (switches, APs, gateways)
- `UniFiClient.cs` - Normalized client info (wired/wireless)
- `UniFiNetwork.cs` - Normalized network/VLAN info
- `UniFiSite.cs` - Site info

### Phase 3: Refactor Current Implementation

1. Move current `UniFiApiClient` logic into `WebUIDataProvider`
2. Have it implement `IUniFiDataProvider`
3. Map JSON responses to domain models internally

### Phase 4: Update Consumers

1. Update `ConfigAuditEngine` to use `IUniFiDataProvider`
2. Update `SecurityAuditEngine` to work with domain models
3. Remove direct JSON parsing from audit logic

### Phase 5: Dependency Injection

```csharp
// Program.cs
services.AddScoped<IUniFiDataProvider, WebUIDataProvider>();

// Future:
// services.AddScoped<IUniFiDataProvider, OfficialAPIProvider>();
```

## Key Domain Models

### UniFiDevice
```csharp
public class UniFiDevice
{
    public string Id { get; set; }
    public string Mac { get; set; }
    public string Name { get; set; }
    public string Model { get; set; }
    public DeviceType Type { get; set; } // Gateway, Switch, AP
    public string IpAddress { get; set; }
    public bool IsOnline { get; set; }

    // For switches/gateways
    public List<UniFiPort>? Ports { get; set; }

    // For APs
    public bool IsAccessPoint { get; set; }
}
```

### UniFiClient
```csharp
public class UniFiClient
{
    public string Mac { get; set; }
    public string? Name { get; set; }
    public string? Hostname { get; set; }
    public bool IsWired { get; set; }

    // Wired client info
    public string? SwitchMac { get; set; }
    public int? SwitchPort { get; set; }

    // Wireless client info
    public string? AccessPointMac { get; set; }

    // Network assignment
    public string? NetworkId { get; set; }

    // Fingerprint data
    public int? DeviceCategory { get; set; }
    public int? DeviceFamily { get; set; }
    public int? DeviceIdOverride { get; set; }
    public string? Oui { get; set; }
}
```

## Migration Strategy

1. **Phase 1-2**: Define interfaces and domain models (non-breaking)
2. **Phase 3**: Create `WebUIDataProvider` alongside existing code
3. **Phase 4**: Gradually migrate consumers one at a time
4. **Phase 5**: Remove old direct API code when migration complete

## Benefits

- **Testability**: Mock `IUniFiDataProvider` for unit tests
- **Flexibility**: Swap implementations without code changes
- **Maintainability**: API-specific quirks isolated in providers
- **Future-proof**: Ready for official API when available

## Open Questions

1. Should we support multiple providers simultaneously? (e.g., different controllers)
2. How to handle provider-specific features not in interface?
3. Caching strategy - at provider or consumer level?

## Timeline Estimate

| Phase | Description | Effort |
|-------|-------------|--------|
| 1 | Define interfaces | 2-4 hours |
| 2 | Create domain models | 4-6 hours |
| 3 | Implement WebUIDataProvider | 8-12 hours |
| 4 | Migrate consumers | 8-12 hours |
| 5 | DI setup and testing | 4-6 hours |

**Total: 26-40 hours**
