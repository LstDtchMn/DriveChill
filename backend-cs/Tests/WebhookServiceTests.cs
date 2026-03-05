using System;
using System.IO;
using System.Net.Http;
using DriveChill.Models;
using DriveChill.Services;
using Xunit;

namespace DriveChill.Tests;

internal sealed class NullHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}

public sealed class WebhookServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsStore _store;
    private readonly WebhookService _svc;

    public WebhookServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", _tempDir);

        var settings = new AppSettings();
        _store = new SettingsStore(settings);
        _svc   = new WebhookService(_store, new NullHttpClientFactory(), settings);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DRIVECHILL_DATA_DIR", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private void SeedDeliveries(int count)
    {
        var data = _store.GetAll();
        for (int i = 0; i < count; i++)
        {
            data.WebhookDeliveries.Add(new WebhookDelivery
            {
                Timestamp  = DateTimeOffset.UtcNow.AddSeconds(-i).ToString("o"),
                HttpStatus = 200,
                Success    = true,
                EventType  = "alert",
                Attempt    = 1,
            });
        }
        _store.SetAll(data);
    }

    // -----------------------------------------------------------------------

    [Fact]
    public void GetDeliveries_RespectsLimit()
    {
        SeedDeliveries(20);
        var result = _svc.GetDeliveries(limit: 5);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void GetDeliveries_ReturnsAllWhenLimitExceedsCount()
    {
        SeedDeliveries(3);
        var result = _svc.GetDeliveries(limit: 100);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void GetDeliveries_RespectsOffset()
    {
        SeedDeliveries(10);
        var offsetDeliveries = _svc.GetDeliveries(limit: 10, offset: 5);

        // Offset 5 should skip the first 5 (most recent) and return remaining 5
        Assert.Equal(5, offsetDeliveries.Count);
    }

    [Fact]
    public void GetDeliveries_LimitPlusOffset_Paginates()
    {
        SeedDeliveries(15);
        var page1 = _svc.GetDeliveries(limit: 5, offset: 0);
        var page2 = _svc.GetDeliveries(limit: 5, offset: 5);
        var page3 = _svc.GetDeliveries(limit: 5, offset: 10);

        Assert.Equal(5, page1.Count);
        Assert.Equal(5, page2.Count);
        Assert.Equal(5, page3.Count);

        // Pages should return different entries (verified by different timestamps)
        Assert.NotEqual(page1[0].Timestamp, page2[0].Timestamp);
        Assert.NotEqual(page2[0].Timestamp, page3[0].Timestamp);
    }

    [Fact]
    public void GetDeliveries_EmptyWhenOffsetBeyondCount()
    {
        SeedDeliveries(3);
        var result = _svc.GetDeliveries(limit: 10, offset: 100);
        Assert.Empty(result);
    }

    [Fact]
    public void UpdateConfig_ClampsTimeoutToValidRange()
    {
        var cfg = new WebhookConfig
        {
            TargetUrl      = "",
            TimeoutSeconds = 999, // exceeds max of 30
            MaxRetries     = 0,
            RetryBackoffSeconds = 0.1,
        };

        var updated = _svc.UpdateConfig(cfg);
        Assert.Equal(30.0, updated.TimeoutSeconds);
    }

    [Fact]
    public void UpdateConfig_ClampsMaxRetriesToValidRange()
    {
        var cfg = new WebhookConfig
        {
            TargetUrl       = "",
            TimeoutSeconds  = 5,
            MaxRetries      = 999, // exceeds max of 10
            RetryBackoffSeconds = 1,
        };

        var updated = _svc.UpdateConfig(cfg);
        Assert.Equal(10, updated.MaxRetries);
    }
}
