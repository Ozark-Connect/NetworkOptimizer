# NetworkOptimizer.Web

Blazor Server .NET 10 web application for the Network Optimizer for UniFi.

## Overview

This is the main web UI for Network Optimizer, providing a modern, responsive interface for:
- Network dashboard and device monitoring
- Security and configuration auditing
- Adaptive SQM (Smart Queue Management) configuration
- Distributed agent deployment and management
- Professional report generation
- System settings and configuration

## Project Structure

```
NetworkOptimizer.Web/
├── Components/
│   ├── Layout/
│   │   ├── MainLayout.razor       # Main app layout with sidebar and header
│   │   └── NavMenu.razor          # Navigation menu component
│   ├── Pages/
│   │   ├── Dashboard.razor        # Network overview dashboard
│   │   ├── Audit.razor            # Security audit interface
│   │   ├── Sqm.razor              # SQM management interface
│   │   ├── Agents.razor           # Agent deployment and management
│   │   ├── Reports.razor          # Report generation interface
│   │   └── Settings.razor         # Application settings
│   └── Shared/
│       ├── DeviceCard.razor       # Device status card component
│       ├── SecurityScoreGauge.razor # Security score visualization
│       ├── SqmStatusPanel.razor   # SQM status panel
│       ├── AgentStatusTable.razor # Agent health table
│       └── AlertsList.razor       # Alerts list component
├── Services/
│   ├── DashboardService.cs        # Dashboard data aggregation
│   ├── AuditService.cs            # Audit execution and results
│   ├── SqmService.cs              # SQM operations
│   └── AgentService.cs            # Agent management
├── wwwroot/
│   ├── css/
│   │   └── app.css                # Main stylesheet (dark mode)
│   └── favicon.ico
├── Program.cs                      # Application entry point and DI configuration
├── App.razor                       # Root component
└── appsettings.json               # Configuration
```

## Features

### 1. Dashboard
- Real-time device count and status
- Security posture score with visual gauge
- SQM status and performance metrics
- Recent alerts and issues
- Quick access to all features

### 2. Security Audit
- Comprehensive network security analysis
- Firewall rule validation
- VLAN security checks
- Port security analysis
- DNS leak detection
- Exportable audit reports

### 3. SQM Manager
- Adaptive bandwidth optimization
- Learning mode with baseline tracking
- Speedtest history and scheduling
- Real-time latency monitoring
- SSH deployment to UCG/UDM devices

### 4. Agent Management
- Deploy monitoring agents via SSH
- Support for UDM/UCG, Linux, and SNMP agents
- Agent health monitoring
- Metrics collection status
- Manual script generation option

### 5. Report Generation
- Professional PDF reports
- Markdown reports for documentation
- HTML reports for email delivery
- Customizable report sections
- White-label branding (MSP license)

### 6. Settings
- UniFi Controller connection configuration
- SSH gateway configuration
- Admin password management
- Application preferences

## Technology Stack

- **Framework**: ASP.NET Core 10.0
- **UI**: Blazor Server with Interactive Server components
- **Styling**: Custom CSS with dark mode design
- **Dependencies**:
  - NetworkOptimizer.Sqm - SQM management
  - NetworkOptimizer.Agents - Agent deployment
  - NetworkOptimizer.Reports - Report generation

## Configuration

### appsettings.json

```json
{
  "UniFiController": {
    "Url": "https://192.168.1.1",
    "Username": "",
    "Password": ""
  }
}
```

Note: Most configuration is stored in the SQLite database and managed via the Settings UI, including the SSL certificate validation option.

## Running the Application

### Development
```bash
cd src/NetworkOptimizer.Web
dotnet run
```

Navigate to: `https://localhost:5001`

### Docker
```bash
# From project root
cd docker
docker compose up -d
```

Access at: http://localhost:8042

## API Endpoints

The application exposes these endpoints for agent communication:

- `POST /api/metrics` - Metrics ingestion from agents
- `GET /api/health` - Health check endpoint

## UI Design

### Color Scheme
- **Primary**: Blue (#3b82f6) - Navigation and primary actions
- **Success**: Green (#10b981) - Active status, good scores
- **Warning**: Orange (#f59e0b) - Warnings, moderate issues
- **Danger**: Red (#ef4444) - Critical issues, errors
- **Info**: Cyan (#06b6d4) - Informational items

### Dark Mode Theme
The application uses a dark color scheme optimized for reduced eye strain:
- Background: Dark slate (#0f172a)
- Cards: Medium slate (#1e293b)
- Text: Light gray (#f1f5f9)

### Responsive Design
- Desktop: Full sidebar navigation
- Tablet: Collapsed sidebar
- Mobile: Drawer navigation

## Integration Points

### NetworkOptimizer.Sqm
Used for:
- SQM script generation
- Baseline calculation
- Speedtest integration

### NetworkOptimizer.Agents
Used for:
- SSH deployment
- Agent health monitoring
- Script template rendering

### NetworkOptimizer.Reports
Used for:
- PDF report generation
- Markdown formatting
- Report customization

## Development Notes

### Service Layer
All business logic is abstracted into service classes injected via DI:
- `DashboardService` - Aggregates data from multiple sources
- `AuditService` - Executes security audits
- `SqmService` - Manages SQM operations
- `AgentService` - Handles agent lifecycle

### Component Architecture
Components follow Blazor best practices:
- Interactive Server render mode for real-time updates
- Parameter binding for component communication
- Scoped services for data access
- Proper lifecycle management

### Future Enhancements
- WebSocket for real-time metric updates
- SignalR for agent status notifications
- Client-side caching for improved performance
- Progressive Web App (PWA) support
- Multi-language support

## Security Considerations

- **Connection credentials** (UniFi, SSH): AES-256 encrypted with machine-specific key (reversible for connections)
- **Admin password**: PBKDF2-SHA256 hashed with 600K iterations and 16-byte salt (not reversible)
- HTTPS enforced in production
- Input validation on all forms
- SQL injection prevention via parameterized queries
- XSS protection via Blazor's automatic encoding

## License

Business Source License 1.1. See [LICENSE](../../LICENSE) in the repository root.

© 2026 Ozark Connect
