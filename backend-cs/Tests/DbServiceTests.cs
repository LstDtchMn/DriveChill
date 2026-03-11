using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriveChill.Models;
using DriveChill.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

public class DbServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;

    public DbServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);
        _settings = new AppSettings();
        _db = new DbService(_settings, NullLogger<DbService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Helper: trigger schema init by calling a lightweight read method.</summary>
    private async Task InitSchemaAsync() => await _db.GetAllLabelsAsync();

    // -----------------------------------------------------------------------
    // Schema initialisation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EnsureInitialised_CreatesDatabaseFile()
    {
        await InitSchemaAsync();
        Assert.True(File.Exists(_settings.DbPath));
    }

    [Fact]
    public async Task EnsureInitialised_CoreTablesExist()
    {
        await InitSchemaAsync();
        var connStr = $"Data Source={_settings.DbPath}";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();

        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("sensor_log", tables);
        Assert.Contains("sessions", tables);
        Assert.Contains("users", tables);
        Assert.Contains("machines", tables);
        Assert.Contains("push_subscriptions", tables);
        Assert.Contains("email_notification_settings", tables);
        Assert.Contains("fan_settings", tables);
        Assert.Contains("quiet_hours", tables);
        Assert.Contains("temperature_targets", tables);
        Assert.Contains("virtual_sensors", tables);
        Assert.Contains("settings", tables);
        Assert.Contains("auth_log", tables);
    }

    [Fact]
    public async Task EnsureInitialised_DefaultSettingsSeeded()
    {
        var val = await _db.GetSettingAsync("drive_monitoring_enabled");
        Assert.Equal("1", val);
    }

    // -----------------------------------------------------------------------
    // Sensor log — write and read
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LogReadings_And_GetHistory_RoundTrips()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "cpu0", Name = "CPU Core 0", SensorType = "cpu_temp", Value = 55.0, Unit = "C" },
            new() { Id = "cpu1", Name = "CPU Core 1", SensorType = "cpu_temp", Value = 60.0, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);

        var history = await _db.GetHistoryAsync("cpu0", DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Single(history);
        Assert.Equal("cpu0", history[0].Id);
        Assert.Equal(55.0, history[0].Value);
    }

    [Fact]
    public async Task GetHistory_FiltersOldReadings()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "s1", Name = "Sensor", SensorType = "temperature", Value = 42.0, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);

        // Query from the future — should find nothing
        var history = await _db.GetHistoryAsync("s1", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Empty(history);
    }

    // -----------------------------------------------------------------------
    // Prune
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PruneAsync_DeletesOldRowsFromBothTables()
    {
        await InitSchemaAsync();
        var oldTs    = DateTimeOffset.UtcNow.AddDays(-10).ToString("o");
        var recentTs = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        var connStr  = $"Data Source={_settings.DbPath}";

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO sensor_log (timestamp, sensor_id, sensor_name, sensor_type, value, unit)
                VALUES ('{oldTs}',    'test_sensor', 'Test', 'temperature', 40.0, 'C'),
                       ('{recentTs}', 'test_sensor', 'Test', 'temperature', 41.0, 'C');

                INSERT INTO drives (id, name) VALUES ('d_old', 'Drive Old'), ('d_recent', 'Drive Recent');

                INSERT INTO drive_health_snapshots (drive_id, recorded_at, temperature_c)
                VALUES ('d_old',    '{oldTs}',    38.0),
                       ('d_recent', '{recentTs}', 39.0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await _db.PruneAsync(retentionDays: 1);

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd1 = conn.CreateCommand();
            cmd1.CommandText = "SELECT COUNT(*) FROM sensor_log";
            Assert.Equal(1L, (long)(await cmd1.ExecuteScalarAsync())!);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "SELECT COUNT(*) FROM drive_health_snapshots";
            Assert.Equal(1L, (long)(await cmd2.ExecuteScalarAsync())!);
        }
    }

    [Fact]
    public async Task PruneAsync_RetainsRowsWithinRetentionWindow()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "s1", Name = "Test", SensorType = "temperature", Value = 40.0, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);
        await _db.PruneAsync(retentionDays: 30);

        var history = await _db.GetHistoryAsync("s1", DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Single(history);
    }

    // -----------------------------------------------------------------------
    // Machine CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Machine_Create_AssignsIdAndTimestamps()
    {
        var machine = new MachineRecord { Name = "Test Box", BaseUrl = "http://192.168.1.10:8085" };
        var created = await _db.CreateMachineAsync(machine);

        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.False(string.IsNullOrEmpty(created.CreatedAt));
        Assert.Equal("Test Box", created.Name);
    }

    [Fact]
    public async Task Machine_GetAll_ReturnsCreatedMachines()
    {
        await _db.CreateMachineAsync(new MachineRecord { Name = "M1", BaseUrl = "http://10.0.0.1:8085" });
        await _db.CreateMachineAsync(new MachineRecord { Name = "M2", BaseUrl = "http://10.0.0.2:8085" });

        var all = await _db.GetMachinesAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Name == "M1");
        Assert.Contains(all, m => m.Name == "M2");
    }

    [Fact]
    public async Task Machine_GetById_ReturnsCorrectMachine()
    {
        var created = await _db.CreateMachineAsync(new MachineRecord { Name = "Target", BaseUrl = "http://10.0.0.3:8085" });

        var fetched = await _db.GetMachineAsync(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Target", fetched!.Name);
    }

    [Fact]
    public async Task Machine_GetById_ReturnsNullForMissing()
    {
        await InitSchemaAsync();
        var fetched = await _db.GetMachineAsync("nonexistent");
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Machine_Update_ChangesFields()
    {
        var created = await _db.CreateMachineAsync(new MachineRecord { Name = "Old", BaseUrl = "http://10.0.0.4:8085" });

        var patch = new MachineRecord { Name = "New", BaseUrl = "http://10.0.0.5:8085", Enabled = false, PollIntervalSeconds = 60.0, TimeoutMs = 10000 };
        var updated = await _db.UpdateMachineAsync(created.Id, patch);
        Assert.True(updated);

        var fetched = await _db.GetMachineAsync(created.Id);
        Assert.Equal("New", fetched!.Name);
        Assert.Equal("http://10.0.0.5:8085", fetched.BaseUrl);
        Assert.False(fetched.Enabled);
    }

    [Fact]
    public async Task Machine_Delete_RemovesMachine()
    {
        var created = await _db.CreateMachineAsync(new MachineRecord { Name = "ToDelete", BaseUrl = "http://10.0.0.6:8085" });

        var deleted = await _db.DeleteMachineAsync(created.Id);
        Assert.True(deleted);

        var fetched = await _db.GetMachineAsync(created.Id);
        Assert.Null(fetched);
    }

    [Fact]
    public async Task Machine_Delete_ReturnsFalseForMissing()
    {
        await InitSchemaAsync();
        var deleted = await _db.DeleteMachineAsync("nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task Machine_UpdateStatus_PersistsFields()
    {
        var created = await _db.CreateMachineAsync(new MachineRecord { Name = "StatusTest", BaseUrl = "http://10.0.0.7:8085" });
        var now = DateTimeOffset.UtcNow.ToString("o");

        await _db.UpdateMachineStatusAsync(created.Id, "online", now, null, 0, "{\"sensors\":[]}");

        var fetched = await _db.GetMachineAsync(created.Id);
        Assert.Equal("online", fetched!.Status);
        Assert.Equal(now, fetched.LastSeenAt);
        Assert.Null(fetched.LastError);
        Assert.Equal("{\"sensors\":[]}", fetched.SnapshotJson);
    }

    [Fact]
    public async Task Machine_SetLastCommand_UpdatesTimestamp()
    {
        var created = await _db.CreateMachineAsync(new MachineRecord { Name = "CmdTest", BaseUrl = "http://10.0.0.8:8085" });
        Assert.Null(created.LastCommandAt);

        await _db.SetMachineLastCommandAsync(created.Id);

        var fetched = await _db.GetMachineAsync(created.Id);
        Assert.NotNull(fetched!.LastCommandAt);
    }

    // -----------------------------------------------------------------------
    // Push subscriptions CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PushSubscription_Create_And_GetAll()
    {
        var sub = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example.com/sub1",
            P256dh   = "key-p256dh",
            AuthKey  = "key-auth",
            UserAgent = "TestAgent/1.0",
        };
        var created = await _db.CreatePushSubscriptionAsync(sub);
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal("https://push.example.com/sub1", created.Endpoint);

        var all = await _db.GetAllPushSubscriptionsAsync();
        Assert.Single(all);
        Assert.Equal(created.Id, all[0].Id);
    }

    [Fact]
    public async Task PushSubscription_DuplicateEndpoint_UpdatesKeys()
    {
        var sub1 = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example.com/dup",
            P256dh   = "old-key",
            AuthKey  = "old-auth",
        };
        var created1 = await _db.CreatePushSubscriptionAsync(sub1);

        var sub2 = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example.com/dup",
            P256dh   = "new-key",
            AuthKey  = "new-auth",
        };
        var created2 = await _db.CreatePushSubscriptionAsync(sub2);

        // Should return same ID (upsert, not duplicate)
        Assert.Equal(created1.Id, created2.Id);

        var fetched = await _db.GetPushSubscriptionAsync(created1.Id);
        Assert.Equal("new-key", fetched!.P256dh);
        Assert.Equal("new-auth", fetched.AuthKey);
    }

    [Fact]
    public async Task PushSubscription_Delete_RemovesRecord()
    {
        var sub = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example.com/del",
            P256dh   = "k",
            AuthKey  = "a",
        };
        var created = await _db.CreatePushSubscriptionAsync(sub);

        var deleted = await _db.DeletePushSubscriptionAsync(created.Id);
        Assert.True(deleted);

        var all = await _db.GetAllPushSubscriptionsAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task PushSubscription_Touch_UpdatesLastUsedAt()
    {
        var sub = new PushSubscriptionRecord
        {
            Endpoint = "https://push.example.com/touch",
            P256dh   = "k",
            AuthKey  = "a",
        };
        var created = await _db.CreatePushSubscriptionAsync(sub);
        Assert.Null(created.LastUsedAt);

        await _db.TouchPushSubscriptionAsync(created.Id);

        var fetched = await _db.GetPushSubscriptionAsync(created.Id);
        Assert.NotNull(fetched!.LastUsedAt);
    }

    // -----------------------------------------------------------------------
    // Email settings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmailSettings_DefaultsExist()
    {
        var settings = await _db.GetEmailSettingsAsync();
        Assert.False(settings.Enabled);
        Assert.Equal(587, settings.SmtpPort);
        Assert.True(settings.UseTls);
    }

    [Fact]
    public async Task EmailSettings_Update_Persists()
    {
        var update = new EmailNotificationSettingsRecord
        {
            Enabled       = true,
            SmtpHost      = "smtp.example.com",
            SmtpPort      = 465,
            SmtpUsername   = "user@example.com",
            SmtpPassword  = "secret123",
            SenderAddress = "noreply@example.com",
            RecipientList = "[\"admin@example.com\"]",
            UseTls        = false,
            UseSsl        = true,
        };
        await _db.UpdateEmailSettingsAsync(update);

        var fetched = await _db.GetEmailSettingsAsync();
        Assert.True(fetched.Enabled);
        Assert.Equal("smtp.example.com", fetched.SmtpHost);
        Assert.Equal(465, fetched.SmtpPort);
        Assert.Equal("user@example.com", fetched.SmtpUsername);
        Assert.Equal("noreply@example.com", fetched.SenderAddress);
        Assert.False(fetched.UseTls);
        Assert.True(fetched.UseSsl);
    }

    [Fact]
    public async Task EmailSettings_EmptyPassword_PreservesExisting()
    {
        // First set a password
        await _db.UpdateEmailSettingsAsync(new EmailNotificationSettingsRecord
        {
            SmtpPassword = "original-pass",
            SmtpHost = "mail.test.com",
        });

        // Update with empty password — should preserve original
        await _db.UpdateEmailSettingsAsync(new EmailNotificationSettingsRecord
        {
            SmtpPassword = "",
            SmtpHost = "mail2.test.com",
        });

        var fetched = await _db.GetEmailSettingsAsync();
        Assert.Equal("mail2.test.com", fetched.SmtpHost);
        // Password should still be the original (not empty)
        Assert.Equal("original-pass", fetched.SmtpPassword);
    }

    // -----------------------------------------------------------------------
    // User and session management
    // -----------------------------------------------------------------------

    [Fact]
    public async Task User_Create_And_Get()
    {
        await _db.CreateUserAsync("alice", "hash123", "admin");

        var user = await _db.GetUserAsync("alice");
        Assert.NotNull(user);
        Assert.Equal("alice", user!.Value.Username);
        Assert.Equal("hash123", user.Value.PasswordHash);
    }

    [Fact]
    public async Task UserExists_ReturnsTrueAfterCreate()
    {
        Assert.False(await _db.UserExistsAsync());
        await _db.CreateUserAsync("bob", "hash");
        Assert.True(await _db.UserExistsAsync());
    }

    [Fact]
    public async Task Session_Create_Validate_Delete()
    {
        await _db.CreateUserAsync("carol", "hash", "admin");
        var token = Guid.NewGuid().ToString();
        var csrf  = Guid.NewGuid().ToString();

        await _db.CreateSessionAsync(token, csrf, "carol", "127.0.0.1", "TestAgent", TimeSpan.FromHours(1));

        var session = await _db.ValidateSessionAsync(token);
        Assert.NotNull(session);
        Assert.Equal("carol", session!.Value.Username);
        Assert.Equal(csrf, session.Value.CsrfToken);
        Assert.Equal("admin", session.Value.Role);

        await _db.DeleteSessionAsync(token);
        var deleted = await _db.ValidateSessionAsync(token);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task Session_ExpiredToken_ReturnsNull()
    {
        await _db.CreateUserAsync("dave", "hash", "admin");
        var token = Guid.NewGuid().ToString();

        // Create session that expired 1 hour ago
        await _db.CreateSessionAsync(token, "csrf", "dave", null, null, TimeSpan.FromHours(-1));

        var session = await _db.ValidateSessionAsync(token);
        Assert.Null(session);
    }

    [Fact]
    public async Task Session_DeletedUser_InvalidatesSession()
    {
        await _db.CreateUserAsync("ephemeral", "hash", "admin");
        // Need a second admin to avoid last-admin guard
        await _db.CreateUserAsync("keeper", "hash", "admin");

        var token = Guid.NewGuid().ToString();
        await _db.CreateSessionAsync(token, "csrf", "ephemeral", null, null, TimeSpan.FromHours(1));

        // Get user ID then delete
        var users = await _db.ListUsersAsync();
        var ephUser = users.First(u => u.Username == "ephemeral");
        await _db.DeleteUserAsync(ephUser.Id);

        // Session should no longer validate (INNER JOIN on users)
        var session = await _db.ValidateSessionAsync(token);
        Assert.Null(session);
    }

    [Fact]
    public async Task ListUsers_ReturnsAllUsers()
    {
        await _db.CreateUserAsync("u1", "h1", "admin");
        await _db.CreateUserAsync("u2", "h2", "viewer");

        var users = await _db.ListUsersAsync();
        Assert.Equal(2, users.Count);
        Assert.Contains(users, u => u.Username == "u1" && u.Role == "admin");
        Assert.Contains(users, u => u.Username == "u2" && u.Role == "viewer");
    }

    [Fact]
    public async Task SetUserRole_ChangesRole()
    {
        await _db.CreateUserAsync("roleuser", "hash", "admin");
        // Create another admin so we don't hit last-admin guard
        await _db.CreateUserAsync("admin2", "hash", "admin");

        var users = await _db.ListUsersAsync();
        var user = users.First(u => u.Username == "roleuser");

        var result = await _db.SetUserRoleAsync(user.Id, "viewer");
        Assert.True(result);

        var fetched = await _db.GetUserByIdAsync(user.Id);
        Assert.Equal("viewer", fetched!.Role);
    }

    [Fact]
    public async Task SetUserPassword_ChangesHash()
    {
        await _db.CreateUserAsync("passuser", "old_hash", "admin");
        var users = await _db.ListUsersAsync();
        var user = users.First(u => u.Username == "passuser");

        await _db.SetUserPasswordAsync(user.Id, "new_hash");

        var fetched = await _db.GetUserAsync("passuser");
        Assert.Equal("new_hash", fetched!.Value.PasswordHash);
    }

    [Fact]
    public async Task DeleteUserSessionsByUsername_ClearsAllSessions()
    {
        await _db.CreateUserAsync("sessionuser", "hash", "admin");
        var t1 = Guid.NewGuid().ToString();
        var t2 = Guid.NewGuid().ToString();
        await _db.CreateSessionAsync(t1, "c1", "sessionuser", null, null, TimeSpan.FromHours(1));
        await _db.CreateSessionAsync(t2, "c2", "sessionuser", null, null, TimeSpan.FromHours(1));

        await _db.DeleteUserSessionsByUsernameAsync("sessionuser");

        Assert.Null(await _db.ValidateSessionAsync(t1));
        Assert.Null(await _db.ValidateSessionAsync(t2));
    }

    [Fact]
    public async Task CountAdminUsers_ReturnsCorrectCount()
    {
        await _db.CreateUserAsync("a1", "h", "admin");
        await _db.CreateUserAsync("a2", "h", "admin");
        await _db.CreateUserAsync("v1", "h", "viewer");

        var count = await _db.CountAdminUsersAsync();
        Assert.Equal(2, count);
    }

    // -----------------------------------------------------------------------
    // Fan settings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FanSetting_SetAndGet_RoundTrips()
    {
        await _db.SetFanSettingAsync("fan1", 25.0, true);

        var setting = await _db.GetFanSettingAsync("fan1");
        Assert.NotNull(setting);
        Assert.Equal("fan1", setting!.FanId);
        Assert.Equal(25.0, setting.MinSpeedPct);
        Assert.True(setting.ZeroRpmCapable);
    }

    [Fact]
    public async Task FanSettings_GetAll_ReturnsAllFans()
    {
        await _db.SetFanSettingAsync("f1", 10.0, false);
        await _db.SetFanSettingAsync("f2", 20.0, true);

        var all = await _db.GetAllFanSettingsAsync();
        Assert.Equal(2, all.Count);
        Assert.True(all.ContainsKey("f1"));
        Assert.True(all.ContainsKey("f2"));
    }

    [Fact]
    public async Task FanSetting_Upsert_OverwritesExisting()
    {
        await _db.SetFanSettingAsync("fan_up", 10.0, false);
        await _db.SetFanSettingAsync("fan_up", 50.0, true);

        var setting = await _db.GetFanSettingAsync("fan_up");
        Assert.Equal(50.0, setting!.MinSpeedPct);
        Assert.True(setting.ZeroRpmCapable);
    }

    // -----------------------------------------------------------------------
    // Sensor labels
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SensorLabel_SetAndGet_RoundTrips()
    {
        await _db.SetLabelAsync("cpu_temp_0", "CPU Package");

        var labels = await _db.GetAllLabelsAsync();
        Assert.Single(labels);
        Assert.Equal("CPU Package", labels["cpu_temp_0"]);
    }

    [Fact]
    public async Task SensorLabel_Delete_RemovesLabel()
    {
        await _db.SetLabelAsync("to_delete", "Temp");
        var deleted = await _db.DeleteLabelAsync("to_delete");
        Assert.True(deleted);

        var labels = await _db.GetAllLabelsAsync();
        Assert.Empty(labels);
    }

    [Fact]
    public async Task SensorLabel_Delete_ReturnsFalseForMissing()
    {
        await InitSchemaAsync();
        var deleted = await _db.DeleteLabelAsync("nonexistent");
        Assert.False(deleted);
    }

    // -----------------------------------------------------------------------
    // Settings key-value store
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Setting_SetAndGet_RoundTrips()
    {
        await _db.SetSettingAsync("test_key", "test_value");

        var val = await _db.GetSettingAsync("test_key");
        Assert.Equal("test_value", val);
    }

    [Fact]
    public async Task Setting_Upsert_OverwritesValue()
    {
        await _db.SetSettingAsync("dup_key", "first");
        await _db.SetSettingAsync("dup_key", "second");

        var val = await _db.GetSettingAsync("dup_key");
        Assert.Equal("second", val);
    }

    [Fact]
    public async Task Setting_Get_ReturnsNullForMissing()
    {
        await InitSchemaAsync();
        var val = await _db.GetSettingAsync("missing_key_xyz");
        Assert.Null(val);
    }

    // -----------------------------------------------------------------------
    // Quiet hours CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QuietHours_CreateAndList()
    {
        var rule = new QuietHoursRule { DayOfWeek = 1, StartTime = "22:00", EndTime = "06:00", ProfileId = "silent" };
        var id = await _db.CreateQuietHoursAsync(rule);
        Assert.True(id > 0);

        var all = await _db.GetQuietHoursAsync();
        Assert.Single(all);
        Assert.Equal("22:00", all[0].StartTime);
    }

    [Fact]
    public async Task QuietHours_Update_ChangesFields()
    {
        var rule = new QuietHoursRule { DayOfWeek = 0, StartTime = "23:00", EndTime = "07:00", ProfileId = "p1" };
        var id = await _db.CreateQuietHoursAsync(rule);

        var updated = new QuietHoursRule { DayOfWeek = 2, StartTime = "21:00", EndTime = "05:00", ProfileId = "p2", Enabled = false };
        var result = await _db.UpdateQuietHoursAsync(id, updated);
        Assert.True(result);

        var all = await _db.GetQuietHoursAsync();
        Assert.Equal(2, all[0].DayOfWeek);
        Assert.Equal("21:00", all[0].StartTime);
        Assert.False(all[0].Enabled);
    }

    [Fact]
    public async Task QuietHours_Delete_RemovesRule()
    {
        var rule = new QuietHoursRule { DayOfWeek = 3, StartTime = "20:00", EndTime = "04:00", ProfileId = "p1" };
        var id = await _db.CreateQuietHoursAsync(rule);

        var deleted = await _db.DeleteQuietHoursAsync(id);
        Assert.True(deleted);

        var all = await _db.GetQuietHoursAsync();
        Assert.Empty(all);
    }

    // -----------------------------------------------------------------------
    // Auth log
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AuthLog_LogEvent_AndCleanup()
    {
        await _db.LogAuthEventAsync("login", "127.0.0.1", "admin", "success", null);
        await _db.LogAuthEventAsync("login", "10.0.0.1", "hacker", "failure", "bad password");

        // Cleanup with 0 retention — should remove both (they are old enough by the time cleanup runs)
        // Actually with 0 days, cutoff = now, our just-inserted rows are at "now" so they won't be deleted.
        // Use a large retention that keeps them.
        await _db.CleanupOldAuthLogsAsync(retentionDays: 1);

        // Verify rows still exist (inserted within last second)
        var connStr = $"Data Source={_settings.DbPath}";
        await using var conn = new SqliteConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM auth_log";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(2L, count);
    }

    [Fact]
    public async Task AuthLog_Cleanup_RemovesOldEntries()
    {
        await InitSchemaAsync();
        var connStr = $"Data Source={_settings.DbPath}";
        var oldTs = DateTimeOffset.UtcNow.AddDays(-100).ToString("o");

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO auth_log (timestamp, event_type, outcome) VALUES ('{oldTs}', 'login', 'success')";
            await cmd.ExecuteNonQueryAsync();
        }

        await _db.CleanupOldAuthLogsAsync(retentionDays: 90);

        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM auth_log";
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(0L, count);
        }
    }

    // -----------------------------------------------------------------------
    // Temperature targets
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TemperatureTarget_CRUD()
    {
        var target = new TemperatureTarget
        {
            Id          = "tt1",
            Name        = "Drive Cooler",
            SensorId    = "hdd_temp_0",
            DriveId     = "drive_a",
            FanIds      = new[] { "fan1", "fan2" },
            TargetTempC = 40.0,
            ToleranceC  = 3.0,
            MinFanSpeed = 30.0,
            Enabled     = true,
        };
        await _db.CreateTemperatureTargetAsync(target);

        var fetched = await _db.GetTemperatureTargetAsync("tt1");
        Assert.NotNull(fetched);
        Assert.Equal("Drive Cooler", fetched!.Name);
        Assert.Equal(40.0, fetched.TargetTempC);
        Assert.Equal(2, fetched.FanIds.Length);

        var all = await _db.ListTemperatureTargetsAsync();
        Assert.Single(all);

        var deleted = await _db.DeleteTemperatureTargetAsync("tt1");
        Assert.True(deleted);
        Assert.Empty(await _db.ListTemperatureTargetsAsync());
    }

    // -----------------------------------------------------------------------
    // Virtual sensors
    // -----------------------------------------------------------------------

    [Fact]
    public async Task VirtualSensor_CRUD()
    {
        var vs = new VirtualSensor
        {
            Id        = "vs1",
            Name      = "Max CPU",
            Type      = "max",
            SourceIds = new List<string> { "cpu0", "cpu1" },
            Enabled   = true,
        };
        await _db.CreateVirtualSensorAsync(vs);

        var all = await _db.GetVirtualSensorsAsync();
        Assert.Single(all);
        Assert.Equal("Max CPU", all[0].Name);
        Assert.Equal(2, all[0].SourceIds.Count);

        vs.Name = "Updated Max";
        var updated = await _db.UpdateVirtualSensorAsync(vs);
        Assert.True(updated);

        var deleted = await _db.DeleteVirtualSensorAsync("vs1");
        Assert.True(deleted);
        Assert.Empty(await _db.GetVirtualSensorsAsync());
    }

    // -----------------------------------------------------------------------
    // Export CSV
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExportCsv_ContainsHeaderAndData()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "csv_s1", Name = "CSV Test", SensorType = "temperature", Value = 42.5, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);

        var csv = await _db.ExportCsvAsync(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.StartsWith("timestamp,sensor_id,sensor_name,sensor_type,value,unit", csv);
        Assert.Contains("csv_s1", csv);
        Assert.Contains("42.5", csv);
    }

    // -----------------------------------------------------------------------
    // Drive settings
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LoadDriveSettings_ReturnsDefaults()
    {
        var ds = await _db.LoadDriveSettingsAsync();
        Assert.True(ds.Enabled);
        Assert.Equal(15, ds.FastPollSeconds);
        Assert.Equal(45.0, ds.HddTempWarningC);
    }

    // -----------------------------------------------------------------------
    // Concurrent reads
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentReads_DoNotCorrupt()
    {
        // Seed some data
        await _db.CreateUserAsync("concurrent_user", "hash", "admin");
        await _db.SetLabelAsync("cs1", "Concurrent Sensor 1");
        await _db.SetFanSettingAsync("cf1", 30.0, false);

        // Fire 10 parallel reads
        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var labels = await _db.GetAllLabelsAsync();
                Assert.True(labels.Count >= 1);

                var fans = await _db.GetAllFanSettingsAsync();
                Assert.True(fans.Count >= 1);

                var exists = await _db.UserExistsAsync();
                Assert.True(exists);
            }));
        }

        await Task.WhenAll(tasks);
    }

    // -----------------------------------------------------------------------
    // Noise profiles
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NoiseProfile_CRUD()
    {
        var profile = new NoiseProfile
        {
            Id = "np1",
            FanId = "fan1",
            Mode = "quick",
            Data = new List<NoiseDataPoint>
            {
                new() { Rpm = 1200, Db = 35.0 },
            },
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await _db.CreateNoiseProfileAsync(profile);

        var all = await _db.ListNoiseProfilesAsync();
        Assert.Single(all);
        Assert.Equal("fan1", all[0].FanId);
        Assert.Equal("quick", all[0].Mode);

        var fetched = await _db.GetNoiseProfileAsync("np1");
        Assert.NotNull(fetched);
        Assert.Single(fetched!.Data);

        var deleted = await _db.DeleteNoiseProfileAsync("np1");
        Assert.True(deleted);
        Assert.Empty(await _db.ListNoiseProfilesAsync());
    }

    // -----------------------------------------------------------------------
    // Profile schedules
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProfileSchedule_CRUD()
    {
        var schedule = new ProfileScheduleRecord
        {
            Id = "ps1",
            ProfileId = "silent",
            StartTime = "22:00",
            EndTime = "06:00",
            DaysOfWeek = "1,2,3,4,5",
            Timezone = "America/New_York",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await _db.CreateProfileScheduleAsync(schedule);

        var all = await _db.GetProfileSchedulesAsync();
        Assert.Single(all);
        Assert.Equal("silent", all[0].ProfileId);
        Assert.Equal("America/New_York", all[0].Timezone);

        schedule.ProfileId = "performance";
        var updated = await _db.UpdateProfileScheduleAsync("ps1", schedule);
        Assert.True(updated);

        var deleted = await _db.DeleteProfileScheduleAsync("ps1");
        Assert.True(deleted);
        Assert.Empty(await _db.GetProfileSchedulesAsync());
    }

    // -----------------------------------------------------------------------
    // Report schedules
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReportSchedule_CRUD()
    {
        var schedule = new ReportScheduleRecord
        {
            Id = "rs1",
            Frequency = "daily",
            TimeUtc = "08:00",
            Timezone = "UTC",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        var created = await _db.CreateReportScheduleAsync(schedule);
        Assert.Equal("rs1", created.Id);

        var all = await _db.ListReportSchedulesAsync();
        Assert.Single(all);

        var fetched = await _db.GetReportScheduleAsync("rs1");
        Assert.NotNull(fetched);
        Assert.Equal("daily", fetched!.Frequency);

        var updated = await _db.UpdateReportScheduleAsync("rs1", frequency: "weekly");
        Assert.NotNull(updated);
        Assert.Equal("weekly", updated!.Frequency);

        await _db.UpdateReportScheduleLastSentAsync("rs1", DateTimeOffset.UtcNow.ToString("o"));
        var afterSent = await _db.GetReportScheduleAsync("rs1");
        Assert.NotNull(afterSent!.LastSentAt);

        var deleted = await _db.DeleteReportScheduleAsync("rs1");
        Assert.True(deleted);
        Assert.Empty(await _db.ListReportSchedulesAsync());
    }

    // -----------------------------------------------------------------------
    // Event annotations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Annotation_CreateListDelete()
    {
        var annotation = new AnnotationRecord
        {
            Id = "ann1",
            EventType = "annotation",
            TimestampUtc = DateTimeOffset.UtcNow.ToString("o"),
            Label = "Firmware update",
            Description = "Updated drive firmware to v2.0",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await _db.CreateAnnotationAsync(annotation);

        var all = await _db.ListAnnotationsAsync();
        Assert.Single(all);
        Assert.Equal("Firmware update", all[0].Label);

        var deleted = await _db.DeleteAnnotationAsync("ann1");
        Assert.True(deleted);
        Assert.Empty(await _db.ListAnnotationsAsync());
    }

    // -----------------------------------------------------------------------
    // Analytics — basic history and stats
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Analytics_History_ReturnsBuckets()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "a_s1", Name = "Analytics Sensor", SensorType = "cpu_temp", Value = 50.0, Unit = "C" },
            new() { Id = "a_s1", Name = "Analytics Sensor", SensorType = "cpu_temp", Value = 60.0, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);

        var buckets = await _db.GetAnalyticsHistoryAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(1),
            new[] { "a_s1" },
            bucketSeconds: 3600);

        Assert.NotEmpty(buckets);
        Assert.Equal("a_s1", buckets[0].SensorId);
        Assert.Equal(2, buckets[0].SampleCount);
        Assert.Equal(55.0, buckets[0].AvgValue, precision: 1);
    }

    [Fact]
    public async Task Analytics_Stats_ReturnsAggregates()
    {
        var readings = new List<SensorReading>
        {
            new() { Id = "st_s1", Name = "Stat Sensor", SensorType = "cpu_temp", Value = 30.0, Unit = "C" },
            new() { Id = "st_s1", Name = "Stat Sensor", SensorType = "cpu_temp", Value = 70.0, Unit = "C" },
        };
        await _db.LogReadingsAsync(readings);

        var (stats, actualStart, actualEnd) = await _db.GetAnalyticsStatsAsync(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(1),
            new[] { "st_s1" });

        Assert.Single(stats);
        Assert.Equal(30.0, stats[0].MinValue);
        Assert.Equal(70.0, stats[0].MaxValue);
        Assert.Equal(50.0, stats[0].AvgValue, precision: 1);
        Assert.NotNull(actualStart);
        Assert.NotNull(actualEnd);
    }

    // -----------------------------------------------------------------------
    // Drive self-test runs
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SelfTestRun_CreateAndQuery()
    {
        await InitSchemaAsync();
        // Need a drive first
        var connStr = $"Data Source={_settings.DbPath}";
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO drives (id, name) VALUES ('drv1', 'Test Drive')";
            await cmd.ExecuteNonQueryAsync();
        }

        var runId = await _db.CreateSelfTestRunAsync("drv1", "short", null);
        Assert.False(string.IsNullOrEmpty(runId));

        var runs = await _db.GetSelfTestRunsAsync("drv1");
        Assert.Single(runs);
        Assert.Equal("running", runs[0]["status"]);

        await _db.UpdateSelfTestRunAsync(runId, "passed", progress: 100.0);

        var updated = await _db.GetSelfTestRunAsync(runId);
        Assert.NotNull(updated);
        Assert.Equal("passed", updated!["status"]);
    }

    // -----------------------------------------------------------------------
    // Notification channels
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NotificationChannel_CRUD()
    {
        var config = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["url"] = System.Text.Json.JsonSerializer.SerializeToElement("https://hooks.example.com/test"),
        };
        await _db.CreateNotificationChannelAsync("nc1", "webhook", "My Hook", true, config);

        var all = await _db.GetNotificationChannelsAsync();
        Assert.Single(all);
        Assert.Equal("webhook", all[0].Type);

        var fetched = await _db.GetNotificationChannelAsync("nc1");
        Assert.NotNull(fetched);
        Assert.Equal("My Hook", fetched!.Name);

        var updated = await _db.UpdateNotificationChannelAsync("nc1", name: "Renamed Hook", enabled: false, config: null);
        Assert.True(updated);

        var afterUpdate = await _db.GetNotificationChannelAsync("nc1");
        Assert.Equal("Renamed Hook", afterUpdate!.Name);
        Assert.False(afterUpdate.Enabled);

        var deleted = await _db.DeleteNotificationChannelAsync("nc1");
        Assert.True(deleted);
        Assert.Empty(await _db.GetNotificationChannelsAsync());
    }
}
