# Development Scripts

Bash scripts for common development tasks. Works with Git Bash on Windows or native bash on macOS/Linux.

## Usage

```bash
# Make scripts executable (first time only)
chmod +x scripts/*.sh

# Run a script
./scripts/test.sh
```

## Available Scripts

| Script | Description |
|--------|-------------|
| `build.sh [Debug\|Release]` | Build the project |
| `test.sh` | Run all tests |
| `coverage.sh` | Run tests with code coverage report |
| `watch.sh` | Run web app with hot reload |
| `clean.sh` | Clean build artifacts and coverage |
| `publish.sh [output-dir]` | Publish for production |
| `docker-build.sh` | Build Docker image |
| `docker-run.sh` | Run container locally (port 8042) |
| `docker-stop.sh` | Stop container |

## Code Coverage

The `coverage.sh` script generates a coverage report:

```bash
./scripts/coverage.sh
```

Coverage results are saved to `./coverage/`. To get an HTML report, install ReportGenerator:

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool
```

Then run `coverage.sh` again - it will automatically generate the HTML report.
