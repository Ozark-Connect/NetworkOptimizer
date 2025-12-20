# GitHub Readiness Plan

Generated from codebase analysis by 4 parallel agents.
**Last updated:** 2025-12-20

---

## 🚨 CRITICAL ISSUES (Must Fix Before Sharing)

### 1. ✅ Real Credentials Tracked in Git
- **File:** `docker/data/unifi-connection.json`
- **Problem:** Contains actual UniFi controller credentials (username, password, URL)
- **Status:** RESOLVED - `docker/data/` added to `.gitignore`, credentials now stored in database

### 2. ✅ Internal Domain Exposed
- **File:** `CLAUDE.md` line 74
- **Problem:** `https://optimizer.seaturtle.minituna.us` reveals your actual domain
- **Status:** RESOLVED - `CLAUDE.md` added to `.gitignore` (local development notes only)

### 3. ✅ SSH Server References
- **File:** `CLAUDE.md` lines 34, 45, 50, 55, 60, 66, 78, 83
- **Problem:** `root@nas` and `/opt/network-optimizer` reveal internal infrastructure
- **Status:** RESOLVED - `CLAUDE.md` excluded from public repo via `.gitignore`

### 4. ✅ Missing README.md
- **Location:** Repository root
- **Problem:** No entry point for new users/testers
- **Status:** RESOLVED - README.md created at repository root

### 5. ❌ Missing LICENSE File
- **Location:** Repository root
- **Problem:** Legal ambiguity for testers/contributors
- **Action:** Add BSL 1.1 license (as planned in PRODUCT-MARKET-ANALYSIS.md)

---

## ⚠️ HIGH PRIORITY

### 6. ❌ Zero Test Coverage
- **Problem:** No unit tests in entire codebase
- **Risk:** Contributors may break things without knowing
- **Action:** Create test project scaffold with xUnit

### 7. ⚠️ Hardcoded Example Password
- **File:** `src/NetworkOptimizer.Agents/Example.cs` line 40
- **Problem:** `Password = "ubnt"` sets bad precedent
- **Status:** ACCEPTABLE - Has comment "// Or use key-based auth" explaining it's example code

### 8. ✅ Dev Notes File Naming
- **File:** `CLAUDE.md`
- **Problem:** Name implies internal AI tooling notes
- **Status:** RESOLVED - File excluded from public repo via `.gitignore`

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
- [x] Remove `docker/data/unifi-connection.json` from git
- [x] Add `docker/data/` to `.gitignore`
- [x] Create `README.md` at repository root
- [ ] Add `LICENSE` file with BSL 1.1 text
- [x] Exclude `CLAUDE.md` from public repo (added to `.gitignore`)

### Short Term
- [ ] Create `.editorconfig` file
- [ ] Add test project scaffold (`NetworkOptimizer.Tests`)
- [x] ~~Rename `CLAUDE.md` to `DEVELOPMENT.md`~~ (excluded from repo instead)
- [x] ~~Add warning comments to example code~~ (already has explanatory comments)

### Optional Enhancements
- [ ] Create `CONTRIBUTING.md` with guidelines
- [ ] Create `SECURITY.md` explaining warranty protection
- [ ] Add GitHub issue templates
- [ ] Set up GitHub Actions for CI/CD

---

## 📁 FILES TO CREATE

1. ~~`README.md` - Project overview for testers~~ ✅ DONE
2. `LICENSE` - BSL 1.1 license text
3. `.editorconfig` - Code style rules (optional)
4. `CONTRIBUTING.md` - How to contribute (optional)

## 📁 FILES TO MODIFY

1. ~~`.gitignore` - Add `docker/data/`~~ ✅ DONE
2. ~~`CLAUDE.md` - Remove internal hostnames~~ ✅ Excluded from repo instead

## 📁 FILES TO REMOVE FROM GIT

1. ~~`docker/data/unifi-connection.json` - Contains real credentials~~ ✅ DONE (now in .gitignore)

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
- ~~`README.md` - Root entry point~~ ✅ DONE
- `LICENSE` - Legal license file
- `GETTING-STARTED.md` - Beginner guide (optional)

---

## NEXT STEPS

### Remaining Items Before Public Release:
1. **Add LICENSE file** - BSL 1.1 license text (required)

### Optional Improvements:
2. Create `.editorconfig` for consistent code formatting
3. Set up test project scaffold (`NetworkOptimizer.Tests`)
4. Add `CONTRIBUTING.md` with guidelines
5. Set up GitHub Actions for CI/CD
