# NetworkOptimizer.Agents

> **Status: Future Project** - This library is planned but not yet implemented. The structure and interfaces below represent the intended design.

Generic agent deployment and monitoring system for Linux systems. This library will provide SSH-based deployment, health monitoring, and template-based configuration management.

## Features

### Agent Deployment
- **SSH-Based Deployment**: Secure deployment via SSH.NET with support for:
  - Password authentication
  - Private key authentication (with or without passphrase)
  - Connection testing before deployment
  - Deployment verification after installation

### Supported Platforms
- **Linux Systems**: Generic Linux servers (Ubuntu, Debian, CentOS, RHEL, Fedora)
  - Deploys as systemd service
  - Collects CPU, memory, disk, and network metrics
  - Optional Docker container monitoring
  - Automatic service management

### Health Monitoring
- Track agent heartbeats in SQLite database
- Detect offline agents
- Monitor agent status and uptime
- View comprehensive health statistics

### Template System
- Scriban-based template rendering
- Dynamic configuration injection

## Project Structure

```
NetworkOptimizer.Agents/
├── Models/
│   ├── AgentConfiguration.cs      # Agent configuration model
│   ├── DeploymentResult.cs        # Deployment result tracking
│   └── SshCredentials.cs          # SSH authentication credentials
├── Templates/
│   ├── linux-agent.sh.template             # Linux agent script
│   ├── linux-agent.service.template        # Systemd service definition
│   └── install-linux.sh.template           # Linux installation
├── AgentDeployer.cs               # Main deployment orchestrator
├── AgentHealthMonitor.cs          # Health monitoring and heartbeat tracking
├── ScriptRenderer.cs              # Template rendering engine
└── NetworkOptimizer.Agents.csproj # Project file
```

## Related Projects

- **NetworkOptimizer.Sqm** - Adaptive SQM (Smart Queue Management) for UniFi gateways
- **NetworkOptimizer.Web/Services/SqmDeploymentService.cs** - SQM script deployment via SSH

## Metrics Collected (Linux Agents)

- **CPU**: Usage percentage, load averages (1m, 5m, 15m)
- **Memory**: Total, used, free, available, swap usage
- **Disk**: Usage, I/O operations (reads/writes)
- **Network**: Per-interface traffic, packets, errors
- **Docker** (optional): Container count, per-container CPU and memory
- **Heartbeats**: Agent online status, system uptime

## Dependencies

- **SSH.NET** (2025.1.0): SSH/SFTP client library
- **Scriban** (6.5.2): Template rendering engine
- **Microsoft.Data.Sqlite** (10.0.1): SQLite database for health monitoring
- **Microsoft.Extensions.Logging.Abstractions** (10.0.1): Logging infrastructure

## .NET Version

Built for **.NET 10.0** with nullable reference types enabled and implicit usings.
