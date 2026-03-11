using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DriveChill.Services;

/// <summary>
/// File-based schema migration runner for SQLite, matching the Python backend's
/// migration_runner.py conventions.
///
/// Reads numbered SQL files from embedded resources (Migrations/*.sql),
/// tracks applied versions in a <c>schema_version</c> table, and applies
/// pending migrations in order.  Each migration runs in its own transaction.
///
/// Before applying pending migrations, creates a timestamped backup of the
/// database file.  On failure, the backup is restored automatically.
/// </summary>
public static partial class MigrationRunner
{
    private static readonly ILogger _log =
        LoggerFactory.Create(b => b.AddConsole()).CreateLogger("MigrationRunner");

    /// <summary>
    /// Apply all pending migrations from the Migrations/ directory.
    /// Returns the number of migrations applied.
    /// </summary>
    public static async Task<int> RunAsync(string connectionString, string dbPath, CancellationToken ct = default, string? migrationsDirectory = null)
    {
        var migrations = DiscoverMigrations(migrationsDirectory);
        if (migrations.Count == 0)
        {
            _log.LogInformation("No migration files found");
            return 0;
        }

        await EnsureSchemaVersionTableAsync(connectionString, ct);
        var currentVersion = await GetCurrentVersionAsync(connectionString, ct);

        var pending = migrations.Where(m => m.Version > currentVersion).ToList();
        if (pending.Count == 0)
        {
            _log.LogInformation("Database schema up to date at version {Version}", currentVersion);
            return 0;
        }

        _log.LogInformation("Found {Count} pending migration(s): {Versions}",
            pending.Count, string.Join(", ", pending.Select(m => m.Version)));

        // Backup before applying
        string? backupPath = null;
        if (File.Exists(dbPath) && new FileInfo(dbPath).Length > 0)
            backupPath = BackupDb(dbPath);

        var applied = 0;
        foreach (var migration in pending)
        {
            _log.LogInformation("Applying migration {Version:D3}: {Description} ({StmtCount} statements)",
                migration.Version, migration.Description, migration.Statements.Count);

            try
            {
                await using var conn = new SqliteConnection(connectionString);
                await conn.OpenAsync(ct);

                await using var pragma1 = conn.CreateCommand();
                pragma1.CommandText = "PRAGMA journal_mode=WAL";
                await pragma1.ExecuteNonQueryAsync(ct);

                await using var pragma2 = conn.CreateCommand();
                pragma2.CommandText = "PRAGMA foreign_keys=ON";
                await pragma2.ExecuteNonQueryAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                try
                {
                    foreach (var stmt in migration.Statements)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = (SqliteTransaction)tx;
                        cmd.CommandText = stmt;
                        try
                        {
                            await cmd.ExecuteNonQueryAsync(ct);
                        }
                        catch (SqliteException ex) when (
                            stmt.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase) &&
                            ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                        {
                            // ALTER TABLE ADD COLUMN on a column that already exists
                            // (fresh DB where DDL block already has it) — safe to skip.
                            _log.LogDebug("Column already exists, skipping: {Stmt}", stmt);
                        }
                    }

                    // Record the version
                    await using var ver = conn.CreateCommand();
                    ver.Transaction = (SqliteTransaction)tx;
                    ver.CommandText = "INSERT INTO schema_version (version, applied_at, description) VALUES ($v, $ts, $d)";
                    ver.Parameters.AddWithValue("$v", migration.Version);
                    ver.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
                    ver.Parameters.AddWithValue("$d", migration.Description);
                    await ver.ExecuteNonQueryAsync(ct);

                    await tx.CommitAsync(ct);
                    applied++;
                }
                catch
                {
                    await tx.RollbackAsync(ct);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Migration {Version:D3} failed", migration.Version);
                if (backupPath != null && File.Exists(backupPath))
                {
                    try
                    {
                        RestoreDb(backupPath, dbPath);
                        throw new InvalidOperationException(
                            $"Migration {migration.Version:D3} ({migration.Description}) failed: {ex.Message}. Database restored from backup.",
                            ex);
                    }
                    catch (InvalidOperationException) { throw; }
                    catch (Exception restoreEx)
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Version:D3} failed: {ex.Message}. Restore ALSO failed: {restoreEx.Message}. Manual intervention required.",
                            ex);
                    }
                }
                throw new InvalidOperationException(
                    $"Migration {migration.Version:D3} ({migration.Description}) failed: {ex.Message}. No backup available.",
                    ex);
            }
        }

        _log.LogInformation("Migrations complete. Database now at version {Version} ({Applied} applied)",
            pending[^1].Version, applied);
        return applied;
    }

    // -----------------------------------------------------------------------
    // Discovery
    // -----------------------------------------------------------------------

    internal static List<MigrationFile> DiscoverMigrations(string? migrationsDirectory = null)
    {
        var migrationsDir = migrationsDirectory ?? FindMigrationsDirectory();
        if (migrationsDir == null || !Directory.Exists(migrationsDir))
            return [];

        var result = new List<MigrationFile>();
        foreach (var file in Directory.GetFiles(migrationsDir, "*.sql").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split('_', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out var version))
            {
                _log.LogWarning("Skipping malformed migration file: {File}", Path.GetFileName(file));
                continue;
            }

            var description = parts[1].Replace('_', ' ');
            var sql = File.ReadAllText(file);
            var statements = SplitStatements(sql);
            result.Add(new MigrationFile(version, description, statements));
        }

        return result;
    }

    private static string? FindMigrationsDirectory()
    {
        // Look relative to the assembly location first, then relative to CWD
        var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (assemblyDir != null)
        {
            var candidate = Path.Combine(assemblyDir, "Migrations");
            if (Directory.Exists(candidate)) return candidate;
        }

        // Development: look relative to project root
        var cwd = Directory.GetCurrentDirectory();
        var candidate2 = Path.Combine(cwd, "Migrations");
        if (Directory.Exists(candidate2)) return candidate2;

        return null;
    }

    internal static List<string> SplitStatements(string sql)
    {
        // Strip line comments
        var noComments = LineCommentRegex().Replace(sql, "");
        return noComments.Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex LineCommentRegex();

    // -----------------------------------------------------------------------
    // Schema version tracking
    // -----------------------------------------------------------------------

    private static async Task EnsureSchemaVersionTableAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT    NOT NULL,
                description TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<int> GetCurrentVersionAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), -1) FROM schema_version";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // -----------------------------------------------------------------------
    // Backup / Restore
    // -----------------------------------------------------------------------

    private static string BackupDb(string dbPath)
    {
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{dbPath}.bak-{ts}";
        File.Copy(dbPath, backupPath, overwrite: true);
        _log.LogInformation("Database backed up to {Path}", backupPath);
        return backupPath;
    }

    private static void RestoreDb(string backupPath, string dbPath)
    {
        File.Copy(backupPath, dbPath, overwrite: true);
        _log.LogInformation("Database restored from {Path}", backupPath);
    }

    // -----------------------------------------------------------------------
    // Types
    // -----------------------------------------------------------------------

    internal sealed record MigrationFile(int Version, string Description, List<string> Statements);
}
