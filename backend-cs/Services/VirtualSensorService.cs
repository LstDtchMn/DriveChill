using System.Diagnostics;
using DriveChill.Models;

namespace DriveChill.Services;

/// <summary>
/// Resolves virtual sensor IDs into computed values based on their type
/// (max, min, avg, weighted, delta, moving_avg) from real sensor readings.
/// </summary>
public sealed class VirtualSensorService
{
    private static readonly HashSet<string> ValidTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "max", "min", "avg", "weighted", "delta", "moving_avg"
    };

    private readonly object _lock = new();
    private List<VirtualSensor> _defs = new();
    private readonly Dictionary<string, EmaState> _emaState = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Load(List<VirtualSensor> defs)
    {
        lock (_lock)
        {
            _defs = new List<VirtualSensor>(defs);
            // Prune stale EMA state
            var ids = new HashSet<string>(defs.Select(d => d.Id));
            foreach (var key in _emaState.Keys.Where(k => !ids.Contains(k)).ToList())
                _emaState.Remove(key);
        }
    }

    public IReadOnlyList<VirtualSensor> Definitions
    {
        get { lock (_lock) return new List<VirtualSensor>(_defs); }
    }

    /// <summary>
    /// Compute all enabled virtual sensor values and merge into sensor values.
    /// Returns a new dict containing both real and virtual sensor values.
    /// </summary>
    public Dictionary<string, double> ResolveAll(Dictionary<string, double> sensorValues)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, double>(sensorValues);
            foreach (var vs in _defs)
            {
                if (!vs.Enabled) continue;
                var val = Compute(vs, result);
                if (val.HasValue && double.IsFinite(val.Value))
                    result[vs.Id] = val.Value;
            }
            return result;
        }
    }

    private double? Compute(VirtualSensor vs, Dictionary<string, double> values)
    {
        if (vs.Type == "delta")
        {
            if (vs.SourceIds.Count < 2) return null;
            if (!values.TryGetValue(vs.SourceIds[0], out var v0) || !double.IsFinite(v0)) return null;
            if (!values.TryGetValue(vs.SourceIds[1], out var v1) || !double.IsFinite(v1)) return null;
            return (v0 - v1) + vs.Offset;
        }

        var sources = new List<double>();
        foreach (var sid in vs.SourceIds)
        {
            if (values.TryGetValue(sid, out var val) && double.IsFinite(val))
                sources.Add(val);
        }
        if (sources.Count == 0) return null;

        double raw;
        switch (vs.Type)
        {
            case "max":
                raw = sources.Max();
                break;
            case "min":
                raw = sources.Min();
                break;
            case "avg":
                raw = sources.Average();
                break;
            case "weighted":
                raw = ComputeWeighted(vs, values);
                break;
            case "moving_avg":
                raw = UpdateEma(vs, sources.Average());
                break;
            default:
                return null;
        }

        return raw + vs.Offset;
    }

    private double ComputeWeighted(VirtualSensor vs, Dictionary<string, double> values)
    {
        var weights = vs.Weights;
        if (weights == null || weights.Count != vs.SourceIds.Count)
        {
            // Fallback to equal weights
            var sources = new List<double>();
            foreach (var sid in vs.SourceIds)
                if (values.TryGetValue(sid, out var v) && double.IsFinite(v))
                    sources.Add(v);
            return sources.Count > 0 ? sources.Average() : 0;
        }

        double wSum = 0, vSum = 0;
        for (int i = 0; i < vs.SourceIds.Count; i++)
        {
            if (values.TryGetValue(vs.SourceIds[i], out var val) && double.IsFinite(val))
            {
                vSum += val * weights[i];
                wSum += weights[i];
            }
        }
        return wSum > 0 ? vSum / wSum : 0;
    }

    private double UpdateEma(VirtualSensor vs, double instant)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var window = vs.WindowSeconds ?? 30.0;

        if (!_emaState.TryGetValue(vs.Id, out var state) || !double.IsFinite(state.Value))
        {
            _emaState[vs.Id] = new EmaState { Value = instant, LastUpdate = now };
            return instant;
        }

        var dt = now - state.LastUpdate;
        if (dt <= 0) return state.Value;

        var alpha = window > 0 ? 1.0 - Math.Exp(-dt / window) : 1.0;
        state.Value += alpha * (instant - state.Value);
        state.LastUpdate = now;
        return state.Value;
    }

    private sealed class EmaState
    {
        public double Value;
        public double LastUpdate;
    }
}
