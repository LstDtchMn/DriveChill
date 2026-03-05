namespace DriveChill.Services;

/// <summary>
/// Hosted service wrapper that starts and stops <see cref="DriveMonitorService"/>
/// as part of the ASP.NET Core application lifecycle.
/// </summary>
public sealed class DriveMonitorWorker : BackgroundService
{
    private readonly DriveMonitorService _monitor;
    private readonly DbService _db;
    private readonly ILogger<DriveMonitorWorker> _log;

    public DriveMonitorWorker(
        DriveMonitorService monitor,
        DbService db,
        ILogger<DriveMonitorWorker> log)
    {
        _monitor = monitor;
        _db      = db;
        _log     = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var settings = await _db.LoadDriveSettingsAsync(stoppingToken);
            await _monitor.StartAsync(settings, stoppingToken);
            _log.LogInformation("DriveMonitorWorker started");

            // Keep the hosted-service alive until the host requests shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — fall through to StopAsync.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "DriveMonitorWorker startup failed");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _monitor.StopAsync();
        await base.StopAsync(cancellationToken);
        _log.LogInformation("DriveMonitorWorker stopped");
    }
}
