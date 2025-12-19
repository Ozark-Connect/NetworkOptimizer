# GitHub Readiness Plan

Generated from codebase analysis by 4 parallel agents.

---

## 🚨 CRITICAL ISSUES (Must Fix Before Sharing)

### 1. Real Credentials Tracked in Git
- **File:** `docker/data/unifi-connection.json`
- **Problem:** Contains actual UniFi controller credentials (username, password, URL)
- **Action:**
  1. Add to `.gitignore`
  2. Remove from git: `git rm --cached docker/data/unifi-connection.json`
  3. Consider scrubbing git history with `git filter-branch` or BFG Repo Cleaner

### 2. Internal Domain Exposed
- **File:** `CLAUDE.md` line 74
- **Problem:** `https://optimizer.seaturtle.minituna.us` reveals your actual domain
- **Action:** Replace with `https://your-domain.example.com` or remove

### 3. SSH Server References
- **File:** `CLAUDE.md` lines 34, 45, 50, 55, 60, 66, 78, 83
- **Problem:** `root@nas` and `/opt/network-optimizer` reveal internal infrastructure
- **Action:** Replace with generic placeholders like `root@your-server`

### 4. Missing README.md
- **Location:** Repository root
- **Problem:** No entry point for new users/testers
- **Action:** Create README with project overview, features, quick start guide

### 5. Missing LICENSE File
- **Location:** Repository root
- **Problem:** Legal ambiguity for testers/contributors
- **Action:** Add BSL 1.1 license (as planned in PRODUCT-MARKET-ANALYSIS.md)

---

## ⚠️ HIGH PRIORITY

### 6. Zero Test Coverage
- **Problem:** No unit tests in entire codebase
- **Risk:** Contributors may break things without knowing
- **Action:** Create test project scaffold with xUnit

### 7. Hardcoded Example Password
- **File:** `src/NetworkOptimizer.Agents/Example.cs` line 40
- **Problem:** `Password = "ubnt"` sets bad precedent
- **Action:** Add warning comment or use placeholder

### 8. Dev Notes File Naming
- **File:** `CLAUDE.md`
- **Problem:** Name implies internal AI tooling notes
- **Action:** Consider renaming to `DEVELOPMENT.md` or `CONTRIBUTING.md`

---

## ✅ ALREADY GOOD (No Action Needed)

- `.gitignore` correctly excludes `docker/.env`
- Excellent component READMEs in each `src/NetworkOptimizer.*` project
- Clean modular architecture (9 well-separated projects)
- Consistent naming conventions (PascalCase classes, _camelCase fields)
- Excellent structured logging with `ILogger<T>`
- Modern .NET 9 with nullable reference types enabled
- Proper async/await patterns with CancellationToken
- Good dependency injection setup in Program.cs

---

## 📋 PROPOSED CODE STANDARDS

### Naming Conventions
- Classes/Methods: `PascalCase`
- Private fields: `_camelCase` (underscore prefix)
- Interfaces: `I` prefix (e.g., `ILocalRepository`)
- Async methods: `Async` suffix (e.g., `GetDevicesAsync`)
- Constants: `PascalCase` or `UPPER_SNAKE_CASE`

### Error Handling
```csharp
// Use custom exceptions instead of generic Exception
public class UniFiConnectionException : Exception { }
public class ValidationException : Exception { }
public class AuditException : Exception { }
```

### Logging Standard
```csharp
// Always use structured logging with named parameters
_logger.LogInformation("Audit completed for {DeviceId} with score {Score}", deviceId, score);
_logger.LogError(ex, "Failed to connect to {ControllerUrl}", url);
```

### Dependency Injection
- Constructor injection only
- Never use `new()` for DI-registered services
- Register all analyzers/services in Program.cs

### Async/Await
- Always accept `CancellationToken` parameter
- Suffix async methods with `Async`
- Never use `.Result` or `.GetAwaiter().GetResult()`

### Testing (To Add)
- Framework: xUnit
- Mocking: Moq
- Assertions: FluentAssertions
- Target: 50%+ coverage for core business logic

### Configuration
- Add `.editorconfig` for consistent formatting
- Extract magic strings to constants classes
- Move hardcoded values to `appsettings.json`

---

## 📝 ACTION ITEMS

### Immediate (Before Sharing)
- [ ] Remove `docker/data/unifi-connection.json` from git
- [ ] Add `docker/data/unifi-connection.json` to `.gitignore`
- [ ] Create `README.md` at repository root
- [ ] Add `LICENSE` file with BSL 1.1 text
- [ ] Sanitize `CLAUDE.md` (remove real hostnames/domains)

### Short Term
- [ ] Create `.editorconfig` file
- [ ] Add test project scaffold (`NetworkOptimizer.Tests`)
- [ ] Rename `CLAUDE.md` to `DEVELOPMENT.md`
- [ ] Add warning comments to example code with hardcoded values

### Optional Enhancements
- [ ] Create `CONTRIBUTING.md` with guidelines
- [ ] Create `SECURITY.md` explaining warranty protection
- [ ] Add GitHub issue templates
- [ ] Set up GitHub Actions for CI/CD

---

## 📁 FILES TO CREATE

1. `README.md` - Project overview for testers
2. `LICENSE` - BSL 1.1 license text
3. `.editorconfig` - Code style rules
4. `CONTRIBUTING.md` - How to contribute (optional)

## 📁 FILES TO MODIFY

1. `.gitignore` - Add `docker/data/unifi-connection.json`
2. `CLAUDE.md` - Remove internal hostnames, possibly rename

## 📁 FILES TO REMOVE FROM GIT

1. `docker/data/unifi-connection.json` - Contains real credentials

---

## DOCUMENTATION ASSESSMENT

### Excellent (Keep As-Is)
- `NETWORK-OPTIMIZER-SPEC.md` - Comprehensive product spec
- `PRODUCT-MARKET-ANALYSIS.md` - Market research
- `src/NetworkOptimizer.Web/README.md` - Web UI docs
- `src/NetworkOptimizer.Audit/README.md` - Audit engine docs
- `src/NetworkOptimizer.UniFi/README.md` - API client docs
- `docker/README.md` - Docker setup guide
- `docker/DEPLOYMENT.md` - Multi-platform deployment

### Missing (Create)
- `README.md` - Root entry point
- `LICENSE` - Legal license file
- `GETTING-STARTED.md` - Beginner guide (optional)

---

## NEXT STEPS

Ready to proceed? I can:
1. Fix critical issues (credentials, README, LICENSE)
2. Create .editorconfig and coding standards
3. Set up test project scaffold

Let me know which to tackle first, or say "do all" to run them in parallel.
