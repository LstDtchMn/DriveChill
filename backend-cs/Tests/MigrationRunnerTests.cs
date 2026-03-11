using DriveChill.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DriveChill.Tests;

public sealed class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connStr;
    private readonly string _migrationsDir;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"drivechill_mig_test_{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _migrationsDir = Path.Combine(Path.GetTempPath(), $"mig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_migrationsDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        if (Directory.Exists(_migrationsDir)) Directory.Delete(_migrationsDir, true);
        // Clean up any backup files
        foreach (var bak in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_dbPath)}.bak-*"))
            File.Delete(bak);
    }

    [Fact]
    public void SplitStatements_StripComments()
    {
        var sql = """
            -- comment line
            CREATE TABLE foo (id INTEGER);
            INSERT INTO foo VALUES (1); -- inline
            """;
        var stmts = MigrationRunner.SplitStatements(sql);
        Assert.Equal(2, stmts.Count);
        Assert.StartsWith("CREATE TABLE", stmts[0]);
        Assert.StartsWith("INSERT INTO", stmts[1]);
    }

    [Fact]
    public async Task RunAsync_AppliesNewMigrations()
    {
        // Create a simple DB with one table
        await using (var conn = new SqliteConnection(_connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE test_t (id INTEGER PRIMARY KEY)";
            await cmd.ExecuteNonQueryAsync();
        }

        // Write two migration files
        File.WriteAllText(Path.Combine(_migrationsDir, "000_baseline.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_migrationsDir, "001_add-col.sql"),
            "ALTER TABLE test_t ADD COLUMN name TEXT NOT NULL DEFAULT '';");

        var applied = await RunMigrationsFromDir(_migrationsDir);
        Assert.Equal(2, applied);

        // Verify schema_version was populated
        var version = await MigrationRunner.GetCurrentVersionAsync(_connStr, CancellationToken.None);
        Assert.Equal(1, version);

        // Verify the column was added
        await using (var conn = new SqliteConnection(_connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO test_t (id, name) VALUES (1, 'test')";
            await cmd.ExecuteNonQueryAsync(); // would throw if column doesn't exist
        }
    }

    [Fact]
    public async Task RunAsync_SkipsAlreadyApplied()
    {
        File.WriteAllText(Path.Combine(_migrationsDir, "000_baseline.sql"), "SELECT 1;");

        var first = await RunMigrationsFromDir(_migrationsDir);
        Assert.Equal(1, first);

        var second = await RunMigrationsFromDir(_migrationsDir);
        Assert.Equal(0, second); // nothing new to apply
    }

    [Fact]
    public async Task RunAsync_DuplicateColumnIsSafe()
    {
        // Create a table that already has the column
        await using (var conn = new SqliteConnection(_connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE users (id INTEGER PRIMARY KEY, role TEXT NOT NULL DEFAULT 'admin')";
            await cmd.ExecuteNonQueryAsync();
        }

        // Migration tries to add the same column
        File.WriteAllText(Path.Combine(_migrationsDir, "000_add-role.sql"),
            "ALTER TABLE users ADD COLUMN role TEXT NOT NULL DEFAULT 'admin';");

        var applied = await RunMigrationsFromDir(_migrationsDir);
        Assert.Equal(1, applied); // should succeed (duplicate column caught)
    }

    [Fact]
    public async Task RunAsync_FailedMigrationRestoresBackup()
    {
        // Create a DB with data
        await using (var conn = new SqliteConnection(_connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE keep_me (id INTEGER)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "INSERT INTO keep_me VALUES (42)";
            await cmd.ExecuteNonQueryAsync();
        }

        // First migration succeeds, second fails
        File.WriteAllText(Path.Combine(_migrationsDir, "000_baseline.sql"), "SELECT 1;");
        File.WriteAllText(Path.Combine(_migrationsDir, "001_bad.sql"),
            "CREATE TABLE new_t (id INTEGER);\nINSERT INTO nonexistent_table VALUES (1);");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunMigrationsFromDir(_migrationsDir));
        Assert.Contains("failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restored", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Original data should still be intact (restored from backup)
        await using (var conn = new SqliteConnection(_connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM keep_me";
            var val = await cmd.ExecuteScalarAsync();
            Assert.Equal(42L, val);
        }
    }

    private Task<int> RunMigrationsFromDir(string dir) =>
        MigrationRunner.RunAsync(_connStr, _dbPath, CancellationToken.None, migrationsDirectory: dir);
}
