using System.Threading.Channels;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Holds the latest sensor snapshot in memory and fan it out to all WebSocket clients
/// via an unbounded Channel.
///
/// Thread safety: _latest is written by SensorWorker (background thread) and read by
/// controller/WebSocket handlers (Kestrel thread pool). Replacing a reference is atomic
/// on 64-bit CLR, but we use Volatile.Read/Write for correctness.
/// </summary>
public sealed class SensorService
{
    private volatile SensorSnapshot _latest = new();
    private volatile IReadOnlyList<SensorReading> _driveReadings = [];

    // All active WebSocket client channels — add/remove under _lock.
    private readonly List<Channel<SensorSnapshot>> _subscribers = [];
    private readonly object _lock = new();

    /// <summary>Gets the most recent snapshot (never null after first poll).</summary>
    public SensorSnapshot Latest => _latest;

    /// <summary>
    /// Replace the drive temperature readings injected into each snapshot.
    /// Called by DriveMonitorService after each temperature poll.
    /// </summary>
    public void UpdateDriveReadings(IReadOnlyList<SensorReading> readings)
    {
        _driveReadings = readings;
    }

    /// <summary>
    /// Called by SensorWorker on every poll tick.
    /// Merges drive readings into the snapshot before storing and broadcasting.
    /// </summary>
    public void Update(SensorSnapshot snapshot)
    {
        var driveReadings = _driveReadings;
        var merged = driveReadings.Count == 0
            ? snapshot
            : new SensorSnapshot
            {
                Readings = [.. snapshot.Readings, .. driveReadings],
                Backend = snapshot.Backend,
                Timestamp = snapshot.Timestamp,
            };
        _latest = merged;
        BroadcastToSubscribers(merged);
    }

    /// <summary>
    /// Subscribe to receive every new snapshot on a dedicated channel.
    /// The caller is responsible for calling Unsubscribe when done.
    /// </summary>
    public Channel<SensorSnapshot> Subscribe()
    {
        var ch = Channel.CreateUnbounded<SensorSnapshot>(
            new UnboundedChannelOptions { SingleReader = true });
        lock (_lock) _subscribers.Add(ch);
        return ch;
    }

    public void Unsubscribe(Channel<SensorSnapshot> ch)
    {
        lock (_lock) _subscribers.Remove(ch);
        ch.Writer.TryComplete();
    }

    private void BroadcastToSubscribers(SensorSnapshot snap)
    {
        List<Channel<SensorSnapshot>> dead = [];
        lock (_lock)
        {
            foreach (var ch in _subscribers)
            {
                if (!ch.Writer.TryWrite(snap))
                    dead.Add(ch); // back-pressure: drop slow clients
            }
            foreach (var ch in dead) _subscribers.Remove(ch);
        }
    }
}
