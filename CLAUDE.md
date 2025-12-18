# Claude Code Project Notes

## Local Development (Windows)

**Note:** Local Docker container removed for security. Use NAS deployment for testing.

### Run with Docker
```bash
cd docker
docker compose build --no-cache network-optimizer
docker compose up -d
```

### Run without Docker
```bash
cd src/NetworkOptimizer.Web
dotnet run
```
Access at http://localhost:5000

### Build Only
```bash
dotnet build
```

### Publish
```bash
cd src/NetworkOptimizer.Web
dotnet publish -c Release -o C:/NetworkOptimizer
```

## Production Deployment

**Production Server:** `root@nas:/opt/network-optimizer`

### Deploy Process

1. Commit and push changes:
   ```bash
   git add -A && git commit -m "message" && git push
   ```

2. SSH to NAS and pull:
   ```bash
   ssh root@nas "cd /opt/network-optimizer && git pull"
   ```

3. Rebuild and restart Docker:
   ```bash
   ssh root@nas "cd /opt/network-optimizer/docker && docker compose build --no-cache network-optimizer && docker compose up -d network-optimizer"
   ```

### Quick Deploy (all-in-one)
```bash
git push && ssh root@nas "cd /opt/network-optimizer && git pull && cd docker && docker compose build --no-cache network-optimizer && docker compose up -d network-optimizer"
```

### First-Time Setup
Create `.env` from template:
```bash
ssh root@nas "cd /opt/network-optimizer/docker && cp .env.example .env"
# Then edit .env to set secure passwords
```

### Services

| Service | Port | URL |
|---------|------|-----|
| Web UI | 8042 | https://optimizer.seaturtle.minituna.us (via Caddy) |

### Check Status
```bash
ssh root@nas "docker ps --filter 'name=network-optimizer'"
```

### View Logs
```bash
ssh root@nas "docker logs -f network-optimizer"
```

## Project Structure

- `src/NetworkOptimizer.Web` - Blazor web UI (main app)
- `src/NetworkOptimizer.Audit` - Security audit engine
- `src/NetworkOptimizer.UniFi` - UniFi API client
- `src/NetworkOptimizer.Storage` - SQLite database models
- `src/NetworkOptimizer.Monitoring` - SNMP/SSH polling
- `docker/` - Docker deployment files

## Key Files

- `docker/docker-compose.yml` - Production (app only, host networking, behind Caddy)
- `docker/docker-compose.local.yml` - Local dev (app + InfluxDB + Grafana)
- `docker/Dockerfile` - Container build

## Database Migrations (EF Core)

When modifying database models, you must create a proper EF Core migration. Migrations run automatically on app startup via `db.Database.Migrate()` in Program.cs.

### Required Files for Each Migration

Every migration requires THREE files in `src/NetworkOptimizer.Storage/Migrations/`:

1. **Migration file**: `YYYYMMDDHHMMSS_MigrationName.cs` - Contains `Up()` and `Down()` methods
2. **Designer file**: `YYYYMMDDHHMMSS_MigrationName.Designer.cs` - Contains model snapshot at migration time
3. **Update snapshot**: `NetworkOptimizerDbContextModelSnapshot.cs` - Must include the new property/table

### Creating a Migration Manually

1. Create the migration file with `Up()` and `Down()` methods:
```csharp
// 20251217200000_AddNewColumn.cs
public partial class AddNewColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NewColumn",
            table: "TableName",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "NewColumn", table: "TableName");
    }
}
```

2. Create the designer file (copy structure from previous migration, update class name and migration ID)

3. Update `NetworkOptimizerDbContextModelSnapshot.cs` to include the new property in the entity definition

### Common Mistake

If you forget the `.Designer.cs` file or don't update the snapshot, you'll get errors like:
```
SQLite Error 1: 'no such column: d.NewColumn'
```

## Static File Downloads

Files in `src/NetworkOptimizer.Web/wwwroot/downloads/` are served at `/downloads/`

Example: `/downloads/iperf3_3.18-1_mips-3.4.ipk`
