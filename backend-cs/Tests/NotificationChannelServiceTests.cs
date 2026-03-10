using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveChill.Api;
using DriveChill.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DriveChill.Tests;

/// <summary>
/// Tests for <see cref="NotificationChannelService"/> focusing on
/// SSRF rejection at save time (controller) and send time (service).
/// </summary>
public sealed class NotificationChannelServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppSettings _settings;
    private readonly DbService _db;
    private readonly NotificationChannelService _svc;
    private readonly NotificationChannelsController _ctrl;

    public NotificationChannelServiceTests()
    {
        _tempDir  = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        _settings = new AppSettings();
        _db       = new DbService(_settings, NullLogger<DbService>.Instance);
        _svc      = new NotificationChannelService(_db, new NullHttpClientFactory(),
                        NullLogger<NotificationChannelService>.Instance);
        _ctrl     = new NotificationChannelsController(_svc);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static Dictionary<string, JsonElement> Cfg(string key, string value)
        => new() { [key] = JsonDocument.Parse($"\"{value}\"").RootElement };

    /// <summary>Extract the "detail" string from a BadRequest response value (handles \u0027 escaping).</summary>
    private static string GetDetail(object? value)
    {
        var raw = JsonSerializer.Serialize(value);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("detail", out var el) ? el.GetString() ?? "" : raw;
    }

    // -----------------------------------------------------------------------
    // Save-time SSRF (controller layer)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/hook")]
    public async Task Create_RejectsSsrfUrl_ForUrlField(string badUrl)
    {
        var body = new CreateChannelRequest
        {
            Type    = "ntfy",
            Name    = "Bad NTFY",
            Enabled = true,
            Config  = Cfg("url", badUrl),
        };

        var result = await _ctrl.Create(body, CancellationToken.None);

        var br = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        Assert.Contains("Config 'url'", GetDetail(br.Value));
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://192.168.1.1/hook")]
    public async Task Create_RejectsSsrfUrl_ForWebhookUrlField(string badUrl)
    {
        var body = new CreateChannelRequest
        {
            Type    = "discord",
            Name    = "Bad Discord",
            Enabled = true,
            Config  = Cfg("webhook_url", badUrl),
        };

        var result = await _ctrl.Create(body, CancellationToken.None);

        var br = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        Assert.Contains("Config 'webhook_url'", GetDetail(br.Value));
    }

    [Theory]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost/hook")]
    public async Task Update_RejectsSsrfUrl(string badUrl)
    {
        // First create a valid channel
        await _svc.CreateAsync("nc_test", "ntfy", "NTFY", true,
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        var body = new UpdateChannelRequest
        {
            Config = Cfg("url", badUrl),
        };

        var result = await _ctrl.Update("nc_test", body, CancellationToken.None);

        var br = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result);
        Assert.Contains("Config 'url'", GetDetail(br.Value));
    }

    // -----------------------------------------------------------------------
    // Send-time SSRF (service layer)
    // The service calls UrlSecurity.TryValidateOutboundHttpUrl before any HTTP.
    // localhost / 127.0.0.1 / 192.168.x.x are blocked without network I/O.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SendAlertAll_BlocksNtfy_WithLoopbackUrl()
    {
        await _svc.CreateAsync("nc_1", "ntfy", "NTFY Loopback", true,
            new Dictionary<string, JsonElement>
            {
                ["url"]   = JsonDocument.Parse("\"http://localhost\"").RootElement,
                ["topic"] = JsonDocument.Parse("\"alerts\"").RootElement,
            }, CancellationToken.None);

        var successes = await _svc.SendAlertAllAsync("CPU Temp", 95.0, 80.0);

        Assert.Equal(0, successes);
    }

    [Fact]
    public async Task SendAlertAll_BlocksDiscord_WithPrivateUrl()
    {
        await _svc.CreateAsync("nc_2", "discord", "Discord Private", true,
            new Dictionary<string, JsonElement>
            {
                ["webhook_url"] = JsonDocument.Parse("\"http://192.168.1.10/hook\"").RootElement,
            }, CancellationToken.None);

        var successes = await _svc.SendAlertAllAsync("CPU Temp", 95.0, 80.0);

        Assert.Equal(0, successes);
    }

    [Fact]
    public async Task SendAlertAll_BlocksSlack_WithLinkLocalUrl()
    {
        await _svc.CreateAsync("nc_3", "slack", "Slack Link-local", true,
            new Dictionary<string, JsonElement>
            {
                ["webhook_url"] = JsonDocument.Parse("\"http://169.254.169.254/latest\"").RootElement,
            }, CancellationToken.None);

        var successes = await _svc.SendAlertAllAsync("CPU Temp", 95.0, 80.0);

        Assert.Equal(0, successes);
    }

    [Fact]
    public async Task SendAlertAll_BlocksGeneric_WithLoopbackUrl()
    {
        await _svc.CreateAsync("nc_4", "generic_webhook", "Generic Loopback", true,
            new Dictionary<string, JsonElement>
            {
                ["url"] = JsonDocument.Parse("\"http://127.0.0.1/hook\"").RootElement,
            }, CancellationToken.None);

        var successes = await _svc.SendAlertAllAsync("CPU Temp", 95.0, 80.0);

        Assert.Equal(0, successes);
    }

    [Fact]
    public async Task SendAlertAll_SkipsDisabledChannels()
    {
        await _svc.CreateAsync("nc_5", "discord", "Disabled", false,
            new Dictionary<string, JsonElement>
            {
                ["webhook_url"] = JsonDocument.Parse("\"http://127.0.0.1/hook\"").RootElement,
            }, CancellationToken.None);

        var successes = await _svc.SendAlertAllAsync("CPU Temp", 95.0, 80.0);

        Assert.Equal(0, successes);
    }

    // -----------------------------------------------------------------------
    // CRUD smoke tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateAndList_RoundTrip()
    {
        await _svc.CreateAsync("nc_6", "slack", "My Slack", true,
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        var channels = await _svc.ListAsync();
        Assert.Contains(channels, c => c.Id == "nc_6" && c.Type == "slack");
    }

    [Fact]
    public async Task Delete_RemovesChannel()
    {
        await _svc.CreateAsync("nc_7", "ntfy", "To Delete", true,
            new Dictionary<string, JsonElement>(), CancellationToken.None);

        var deleted = await _svc.DeleteAsync("nc_7");
        Assert.True(deleted);

        var ch = await _svc.GetAsync("nc_7");
        Assert.Null(ch);
    }
}
