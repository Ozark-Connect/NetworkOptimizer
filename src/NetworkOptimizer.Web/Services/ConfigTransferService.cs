using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Export and import NetworkOptimizer configuration as encrypted .nopt files.
/// </summary>
public class ConfigTransferService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<ConfigTransferService> _logger;
    private readonly string _dataDirectory;
    private readonly string _sshKeysDirectory;
    private readonly string _tempDirectory;

    // AES-256-CBC semi-obfuscation key (not real security - credential encryption is the real layer)
    private static readonly byte[] EncryptionKey =
    [
        0x4E, 0x65, 0x74, 0x4F, 0x70, 0x74, 0x69, 0x6D,
        0x69, 0x7A, 0x65, 0x72, 0x43, 0x6F, 0x6E, 0x66,
        0x69, 0x67, 0x54, 0x72, 0x61, 0x6E, 0x73, 0x66,
        0x65, 0x72, 0x4B, 0x65, 0x79, 0x21, 0x40, 0x23
    ];

    // Tables that contain history data (excluded from settings-only exports)
    private static readonly string[] HistoryTables =
    [
        "AuditResults",
        "DismissedIssues",
        "Iperf3Results",
        "SqmBaselines",
        "ClientSignalLogs"
    ];

    // Pending import state
    private string? _pendingImportPath;
    private ImportPreview? _pendingPreview;

    public ConfigTransferService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        IHostApplicationLifetime appLifetime,
        ILogger<ConfigTransferService> logger)
    {
        _dbFactory = dbFactory;
        _appLifetime = appLifetime;
        _logger = logger;
        _dataDirectory = GetDataDirectory();
        _sshKeysDirectory = Path.Combine(Path.GetDirectoryName(_dataDirectory)!, "ssh-keys");
        _tempDirectory = Path.Combine(_dataDirectory, "temp");
    }

    private static string GetDataDirectory()
    {
        var isDocker = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true", StringComparison.OrdinalIgnoreCase);

        if (isDocker)
            return "/app/data";
        if (OperatingSystem.IsWindows())
            return Path.Combine(AppContext.BaseDirectory, "data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetworkOptimizer");
    }

    /// <summary>
    /// Clean up any leftover temp files from previous sessions.
    /// </summary>
    public void CleanupTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
                _logger.LogInformation("Cleaned up config transfer temp directory");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up config transfer temp directory");
        }
    }

    /// <summary>
    /// Export configuration to an encrypted .nopt file.
    /// </summary>
    public async Task<byte[]> ExportAsync(ExportType type)
    {
        _logger.LogInformation("Starting {Type} config export", type);
        _logger.LogDebug("Data directory: {DataDir}, Temp directory: {TempDir}", _dataDirectory, _tempDirectory);

        Directory.CreateDirectory(_tempDirectory);
        var tempDbPath = Path.Combine(_tempDirectory, $"export-{Guid.NewGuid()}.db");

        try
        {
            // VACUUM INTO creates a clean, standalone copy (no WAL/SHM files)
            // This is the only reliable way to copy a SQLite DB while it's in use
            var vacuumPath = tempDbPath.Replace('\\', '/');
            _logger.LogDebug("Creating DB copy via VACUUM INTO: {Path}", vacuumPath);
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO @path";
                var param = cmd.CreateParameter();
                param.ParameterName = "@path";
                param.Value = vacuumPath;
                cmd.Parameters.Add(param);
                await cmd.ExecuteNonQueryAsync();
            }
            var dbFileSize = new FileInfo(tempDbPath).Length;
            _logger.LogDebug("DB copy created: {Size} bytes", dbFileSize);

            // For settings-only: delete history tables and vacuum
            if (type == ExportType.SettingsOnly)
            {
                _logger.LogDebug("Pruning history tables for settings-only export");
                await PruneHistoryTablesAsync(tempDbPath);
                var prunedSize = new FileInfo(tempDbPath).Length;
                _logger.LogDebug("DB after pruning: {Size} bytes (was {OriginalSize})", prunedSize, dbFileSize);
            }

            // Build the ZIP archive in memory
            using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                // Add manifest
                var manifest = BuildManifest(type, tempDbPath);
                _logger.LogDebug("Manifest built: {TableCount} tables, type={ExportType}", manifest.TableCounts?.Count, manifest.ExportType);
                var manifestEntry = archive.CreateEntry("manifest.json");
                await using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    await writer.WriteAsync(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                }

                // Add database
                var dbEntry = archive.CreateEntry("network_optimizer.db");
                await using (var dbStream = dbEntry.Open())
                await using (var sourceStream = File.OpenRead(tempDbPath))
                {
                    await sourceStream.CopyToAsync(dbStream);
                }

                // Add credential key if it exists
                var credKeyPath = Path.Combine(_dataDirectory, ".credential_key");
                if (File.Exists(credKeyPath))
                {
                    _logger.LogDebug("Including .credential_key");
                    var keyEntry = archive.CreateEntry(".credential_key");
                    await using var keyStream = keyEntry.Open();
                    await using var sourceKeyStream = File.OpenRead(credKeyPath);
                    await sourceKeyStream.CopyToAsync(keyStream);
                }
                else
                {
                    _logger.LogDebug("No .credential_key found at {Path}", credKeyPath);
                }

                // Both export types: include data protection keys and SSH keys
                var keysDir = Path.Combine(_dataDirectory, "keys");
                _logger.LogDebug("Including data protection keys from {Path} (exists={Exists})", keysDir, Directory.Exists(keysDir));
                await AddDirectoryToArchiveAsync(archive, keysDir, "keys");
                _logger.LogDebug("Including SSH keys from {Path} (exists={Exists})", _sshKeysDirectory, Directory.Exists(_sshKeysDirectory));
                await AddDirectoryToArchiveAsync(archive, _sshKeysDirectory, "ssh-keys");

                // Full export only: include floor plans
                if (type == ExportType.Full)
                {
                    var fpDir = Path.Combine(_dataDirectory, "floor-plans");
                    _logger.LogDebug("Including floor-plans from {Path} (exists={Exists})", fpDir, Directory.Exists(fpDir));
                    await AddDirectoryToArchiveAsync(archive, fpDir, "floor-plans");
                }
            }

            // Encrypt the ZIP
            zipStream.Position = 0;
            _logger.LogDebug("ZIP archive size before encryption: {Size} bytes", zipStream.Length);
            var encrypted = Encrypt(zipStream.ToArray());

            _logger.LogInformation("Export complete: {Size} bytes ({Type})", encrypted.Length, type);
            return encrypted;
        }
        finally
        {
            // Clean up temp DB
            try { File.Delete(tempDbPath); } catch { /* ignore */ }
            // Also delete WAL/SHM files that SQLite may have created
            try { File.Delete(tempDbPath + "-wal"); } catch { /* ignore */ }
            try { File.Delete(tempDbPath + "-shm"); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Validate an uploaded .nopt file and return a preview without applying it.
    /// </summary>
    public async Task<ImportPreview> ValidateImportAsync(Stream uploadStream)
    {
        _logger.LogDebug("Starting import validation");
        Directory.CreateDirectory(_tempDirectory);
        var importPath = Path.Combine(_tempDirectory, $"import-{Guid.NewGuid()}.nopt");

        // Save uploaded file
        await using (var fileStream = File.Create(importPath))
        {
            await uploadStream.CopyToAsync(fileStream);
        }
        var uploadSize = new FileInfo(importPath).Length;
        _logger.LogDebug("Saved uploaded file: {Size} bytes at {Path}", uploadSize, importPath);

        try
        {
            // Decrypt and read manifest
            _logger.LogDebug("Decrypting .nopt file");
            var encrypted = await File.ReadAllBytesAsync(importPath);
            var decrypted = Decrypt(encrypted);
            _logger.LogDebug("Decrypted: {EncryptedSize} bytes -> {DecryptedSize} bytes", encrypted.Length, decrypted.Length);

            using var zipStream = new MemoryStream(decrypted);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            _logger.LogDebug("ZIP archive contains {Count} entries: {Entries}",
                archive.Entries.Count, string.Join(", ", archive.Entries.Select(e => e.FullName)));

            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidOperationException("Invalid .nopt file: missing manifest");

            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<ExportManifest>(manifestStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Invalid .nopt file: corrupt manifest");

            _logger.LogDebug("Manifest: version={Version}, type={Type}, date={Date}, tables={TableCount}",
                manifest.AppVersion, manifest.ExportType, manifest.ExportDate, manifest.TableCounts?.Count);

            // Version compatibility check
            var currentVersion = GetAppVersion();
            var isCompatible = IsVersionCompatible(manifest.AppVersion, currentVersion);
            _logger.LogDebug("Version check: export={ExportVersion}, current={CurrentVersion}, compatible={Compatible}",
                manifest.AppVersion, currentVersion, isCompatible);

            var preview = new ImportPreview
            {
                ExportDate = manifest.ExportDate,
                ExportType = manifest.ExportType,
                AppVersion = manifest.AppVersion,
                CurrentAppVersion = currentVersion,
                IsCompatible = isCompatible,
                TableCounts = manifest.TableCounts ?? new Dictionary<string, int>(),
                HasCredentialKey = archive.GetEntry(".credential_key") != null,
                HasFloorPlans = archive.Entries.Any(e => e.FullName.StartsWith("floor-plans/")),
                HasDataProtectionKeys = archive.Entries.Any(e => e.FullName.StartsWith("keys/")),
                HasSshKeys = archive.Entries.Any(e => e.FullName.StartsWith("ssh-keys/"))
            };
            _logger.LogDebug("Preview: credentialKey={HasKey}, floorPlans={HasFP}, dataProtectionKeys={HasKeys}, sshKeys={HasSsh}",
                preview.HasCredentialKey, preview.HasFloorPlans, preview.HasDataProtectionKeys, preview.HasSshKeys);

            // Store pending state
            _pendingImportPath = importPath;
            _pendingPreview = preview;

            _logger.LogInformation("Import validation complete: {Type} export from {Version} ({Date})",
                preview.ExportType, preview.AppVersion, preview.ExportDate);
            return preview;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import validation failed");
            // Clean up on error
            try { File.Delete(importPath); } catch { /* ignore */ }
            throw;
        }
    }

    /// <summary>
    /// Apply the previously validated import.
    /// </summary>
    public async Task ApplyImportAsync()
    {
        if (_pendingImportPath == null || _pendingPreview == null)
            throw new InvalidOperationException("No pending import to apply");

        if (!File.Exists(_pendingImportPath))
            throw new InvalidOperationException("Pending import file not found");

        _logger.LogInformation("Applying {Type} config import from {Version}",
            _pendingPreview.ExportType, _pendingPreview.AppVersion);

        var stagingDir = Path.Combine(_tempDirectory, $"staging-{Guid.NewGuid()}");
        Directory.CreateDirectory(stagingDir);
        _logger.LogDebug("Staging directory: {StagingDir}", stagingDir);

        try
        {
            // Decrypt and extract
            _logger.LogDebug("Decrypting pending import: {Path}", _pendingImportPath);
            var encrypted = await File.ReadAllBytesAsync(_pendingImportPath);
            var decrypted = Decrypt(encrypted);
            _logger.LogDebug("Decrypted: {Size} bytes", decrypted.Length);

            using var zipStream = new MemoryStream(decrypted);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            // Extract everything to staging
            _logger.LogDebug("Extracting {Count} entries to staging", archive.Entries.Count);
            archive.ExtractToDirectory(stagingDir);

            var importedDbPath = Path.Combine(stagingDir, "network_optimizer.db");
            if (!File.Exists(importedDbPath))
                throw new InvalidOperationException("Import archive missing database file");

            var importedDbSize = new FileInfo(importedDbPath).Length;
            _logger.LogDebug("Imported DB size: {Size} bytes", importedDbSize);

            // If settings-only import: preserve existing history by copying it into the imported DB
            if (_pendingPreview.ExportType == "SettingsOnly")
            {
                _logger.LogDebug("Settings-only import: preserving existing history tables");
                await PreserveHistoryAsync(importedDbPath);
                var afterHistorySize = new FileInfo(importedDbPath).Length;
                _logger.LogDebug("DB after history preservation: {Size} bytes (was {OriginalSize})", afterHistorySize, importedDbSize);
            }

            // Replace files
            var targetDbPath = Path.Combine(_dataDirectory, "network_optimizer.db");
            _logger.LogDebug("Replacing DB: {Target}", targetDbPath);
            File.Copy(importedDbPath, targetDbPath, overwrite: true);
            _logger.LogDebug("DB replaced successfully");

            // Replace credential key
            var importedKeyPath = Path.Combine(stagingDir, ".credential_key");
            var targetKeyPath = Path.Combine(_dataDirectory, ".credential_key");
            if (File.Exists(importedKeyPath))
            {
                _logger.LogDebug("Replacing .credential_key");
                File.Copy(importedKeyPath, targetKeyPath, overwrite: true);
            }
            else
            {
                _logger.LogDebug("No .credential_key in import archive");
            }

            // Replace floor plans (full export only - they'd only exist in full exports)
            var importedFloorPlans = Path.Combine(stagingDir, "floor-plans");
            if (Directory.Exists(importedFloorPlans))
            {
                var fpFiles = Directory.GetFiles(importedFloorPlans, "*", SearchOption.AllDirectories);
                _logger.LogDebug("Replacing floor-plans: {Count} files", fpFiles.Length);
                var targetFloorPlans = Path.Combine(_dataDirectory, "floor-plans");
                if (Directory.Exists(targetFloorPlans))
                    Directory.Delete(targetFloorPlans, recursive: true);
                CopyDirectory(importedFloorPlans, targetFloorPlans);
            }
            else
            {
                _logger.LogDebug("No floor-plans in import archive");
            }

            // Replace data protection keys (full export only)
            var importedKeys = Path.Combine(stagingDir, "keys");
            if (Directory.Exists(importedKeys))
            {
                var keyFiles = Directory.GetFiles(importedKeys, "*", SearchOption.AllDirectories);
                _logger.LogDebug("Replacing data protection keys: {Count} files", keyFiles.Length);
                var targetKeys = Path.Combine(_dataDirectory, "keys");
                if (Directory.Exists(targetKeys))
                    Directory.Delete(targetKeys, recursive: true);
                CopyDirectory(importedKeys, targetKeys);
            }
            else
            {
                _logger.LogDebug("No data protection keys in import archive");
            }

            // Replace SSH keys (full export only)
            var importedSshKeys = Path.Combine(stagingDir, "ssh-keys");
            if (Directory.Exists(importedSshKeys))
            {
                var sshKeyFiles = Directory.GetFiles(importedSshKeys, "*", SearchOption.AllDirectories);
                _logger.LogDebug("Replacing SSH keys: {Count} files", sshKeyFiles.Length);
                if (Directory.Exists(_sshKeysDirectory))
                    Directory.Delete(_sshKeysDirectory, recursive: true);
                CopyDirectory(importedSshKeys, _sshKeysDirectory);
            }
            else
            {
                _logger.LogDebug("No SSH keys in import archive");
            }

            _logger.LogInformation("Config import applied successfully, scheduling restart");

            // Clean up
            _pendingImportPath = null;
            _pendingPreview = null;
            CleanupTempFiles();

            // Schedule app restart after a short delay to let the HTTP response go out
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                _logger.LogInformation("Restarting application after config import");
                _appLifetime.StopApplication();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply config import");
            throw;
        }
        finally
        {
            // Clean up staging (but not pending file, in case we need to retry)
            try { Directory.Delete(stagingDir, recursive: true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Cancel a pending import and clean up temp files.
    /// </summary>
    public void CancelPendingImport()
    {
        if (_pendingImportPath != null)
        {
            try { File.Delete(_pendingImportPath); } catch { /* ignore */ }
        }
        _pendingImportPath = null;
        _pendingPreview = null;
    }

    private async Task PruneHistoryTablesAsync(string dbPath)
    {
        _logger.LogDebug("Pruning history tables from temp DB copy: {Path}", dbPath);
        var connStr = $"Data Source={dbPath}";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        // Switch out of WAL mode so VACUUM produces a single clean file
        await using (var walCmd = conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=DELETE";
            await walCmd.ExecuteNonQueryAsync();
        }

        foreach (var table in HistoryTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM [{table}]";
            try
            {
                var deleted = await cmd.ExecuteNonQueryAsync();
                _logger.LogDebug("Pruned {Table}: {Count} rows deleted", table, deleted);
            }
            catch (SqliteException ex)
            {
                _logger.LogDebug("Skipped pruning {Table} (may not exist): {Error}", table, ex.Message);
            }
        }

        _logger.LogDebug("Running VACUUM on pruned DB");
        await using var vacuumCmd = conn.CreateCommand();
        vacuumCmd.CommandText = "VACUUM";
        await vacuumCmd.ExecuteNonQueryAsync();
    }

    private async Task PreserveHistoryAsync(string importedDbPath)
    {
        var currentDbPath = Path.Combine(_dataDirectory, "network_optimizer.db");
        _logger.LogDebug("Preserving history: attaching current DB {CurrentDb} to imported DB {ImportedDb}",
            currentDbPath, importedDbPath);
        var connStr = $"Data Source={importedDbPath}";

        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        // Attach the current database
        await using (var attachCmd = conn.CreateCommand())
        {
            attachCmd.CommandText = $"ATTACH DATABASE @path AS current_db";
            attachCmd.Parameters.AddWithValue("@path", currentDbPath);
            await attachCmd.ExecuteNonQueryAsync();
        }
        _logger.LogDebug("Current DB attached successfully");

        // Copy history tables from current DB into the imported DB
        foreach (var table in HistoryTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT OR IGNORE INTO main.[{table}] SELECT * FROM current_db.[{table}]";
            try
            {
                var inserted = await cmd.ExecuteNonQueryAsync();
                _logger.LogDebug("Preserved {Table}: {Count} rows copied from current DB", table, inserted);
            }
            catch (SqliteException ex)
            {
                _logger.LogDebug("Skipping history table {Table} during import: {Error}", table, ex.Message);
            }
        }

        await using (var detachCmd = conn.CreateCommand())
        {
            detachCmd.CommandText = "DETACH DATABASE current_db";
            await detachCmd.ExecuteNonQueryAsync();
        }
        _logger.LogDebug("History preservation complete, current DB detached");
    }

    private ExportManifest BuildManifest(ExportType type, string dbPath)
    {
        var tableCounts = new Dictionary<string, int>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // Get all user tables
        using var tablesCmd = conn.CreateCommand();
        tablesCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__EFMigrationsHistory'";
        using var reader = tablesCmd.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
            tables.Add(reader.GetString(0));
        reader.Close();

        foreach (var table in tables)
        {
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            tableCounts[table] = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        return new ExportManifest
        {
            AppVersion = GetAppVersion(),
            ExportDate = DateTime.UtcNow,
            ExportType = type == ExportType.Full ? "Full" : "SettingsOnly",
            TableCounts = tableCounts
        };
    }

    private static string GetAppVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";
    }

    private static bool IsVersionCompatible(string exportVersion, string currentVersion)
    {
        // Strip metadata after '+' (e.g., "1.4.0+abc123" -> "1.4.0")
        var exportClean = exportVersion.Split('+')[0].Split('-')[0];
        var currentClean = currentVersion.Split('+')[0].Split('-')[0];

        if (!Version.TryParse(exportClean, out var export) || !Version.TryParse(currentClean, out var current))
            return true; // Can't determine - allow it

        // Reject if export is from a newer major version
        return export.Major <= current.Major;
    }

    private static async Task AddDirectoryToArchiveAsync(ZipArchive archive, string sourceDir, string archivePrefix)
    {
        if (!Directory.Exists(sourceDir))
            return;

        foreach (var filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, filePath).Replace('\\', '/');
            var entryName = $"{archivePrefix}/{relativePath}";
            var entry = archive.CreateEntry(entryName);
            await using var entryStream = entry.Open();
            await using var fileStream = File.OpenRead(filePath);
            await fileStream.CopyToAsync(entryStream);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    internal static byte[] Encrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(data, 0, data.Length);

        // [16-byte IV][encrypted payload]
        var result = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);
        return result;
    }

    internal static byte[] Decrypt(byte[] data)
    {
        if (data.Length < 17) // 16 IV + at least 1 byte
            throw new InvalidOperationException("Invalid .nopt file: too small");

        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var iv = new byte[16];
        Buffer.BlockCopy(data, 0, iv, 0, 16);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 16, data.Length - 16);
    }
}

public enum ExportType
{
    Full,
    SettingsOnly
}

public class ExportManifest
{
    public string AppVersion { get; set; } = "";
    public DateTime ExportDate { get; set; }
    public string ExportType { get; set; } = "";
    public Dictionary<string, int>? TableCounts { get; set; }
}

public class ImportPreview
{
    public DateTime ExportDate { get; set; }
    public string ExportType { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string CurrentAppVersion { get; set; } = "";
    public bool IsCompatible { get; set; }
    public Dictionary<string, int> TableCounts { get; set; } = new();
    public bool HasCredentialKey { get; set; }
    public bool HasFloorPlans { get; set; }
    public bool HasDataProtectionKeys { get; set; }
    public bool HasSshKeys { get; set; }
}
