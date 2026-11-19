using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Runs a context's migrations once per test run, then copies the result into every later test
/// database of the same context type. Migrating 170+ migrations on a fresh SQLite file costs seconds
/// per test; the copy costs milliseconds.
/// </summary>
/// <remarks>
/// The real <c>Migrate</c> still runs after every copy, so EF checks the history table and applies
/// anything the template lacks. A target that already has content (two contexts sharing one file)
/// is migrated for real, as before, because a copy would overwrite it.
/// </remarks>
public static class SqliteMigrationTemplate
{
    private sealed class Template
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public SqliteConnection? Connection; // held open: an in-memory database lives as long as a connection does
    }

    private static readonly ConcurrentDictionary<string, Template> Templates = new();

    /// <summary>Drop-in for <c>Database.Migrate()</c> on a SQLite test database.</summary>
    public static void MigrateFromTemplate(this DatabaseFacade database) =>
        database.MigrateFromTemplateAsync().GetAwaiter().GetResult();

    /// <summary>Drop-in for <c>Database.MigrateAsync()</c> on a SQLite test database.</summary>
    public static async Task MigrateFromTemplateAsync(this DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        var path = new SqliteConnectionStringBuilder(database.GetConnectionString()).DataSource;
        var isEmpty = !string.IsNullOrEmpty(path) && (!File.Exists(path) || new FileInfo(path).Length == 0);
        if (!isEmpty)
        {
            await database.MigrateAsync(cancellationToken);
            return;
        }

        // The history table is part of the key: the same context type registered with a custom one
        // (AddNetOptIdentityCore) and without one (test-local factories) yields different files.
        var services = ((IInfrastructure<IServiceProvider>)database).Instance;
        var contextType = services.GetRequiredService<ICurrentDbContext>().Context.GetType();
        var historyTable = RelationalOptionsExtension.Extract(services.GetRequiredService<IDbContextOptions>())
            .MigrationsHistoryTableName ?? "__EFMigrationsHistory";
        var key = contextType.FullName + "|" + historyTable + "|" + string.Join(",", database.GetMigrations());
        var template = Templates.GetOrAdd(key, _ => new Template());

        await template.Gate.WaitAsync(cancellationToken);
        try
        {
            if (template.Connection == null)
            {
                // First empty target for this context: migrate it for real and keep a copy.
                await database.MigrateAsync(cancellationToken);
                var memory = new SqliteConnection($"Data Source=file:no-tests-{Guid.NewGuid():N}?mode=memory&cache=shared");
                memory.Open();
                using (var source = OpenFile(path))
                    source.BackupDatabase(memory);
                template.Connection = memory;
                return;
            }

            using var target = OpenFile(path);
            template.Connection.BackupDatabase(target);
        }
        finally
        {
            template.Gate.Release();
        }

        await database.MigrateAsync(cancellationToken);
    }

    // Unpooled, so the file is not held open after the copy and the test's own cleanup can delete it.
    private static SqliteConnection OpenFile(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
