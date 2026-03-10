'use client';

import { useState, useEffect, useMemo } from 'react';
import { api } from '@/lib/api';
import { useAppStore } from '@/stores/appStore';
import { computeNoiseRecommendations } from '@/lib/noiseOptimizer';
import type { NoiseProfile, TemperatureTarget } from '@/lib/types';

export function NoiseAdvisor() {
  const readings = useAppStore((s) => s.readings);
  const appliedSpeeds = useAppStore((s) => s.appliedSpeeds);

  const [noiseProfiles, setNoiseProfiles] = useState<NoiseProfile[] | null>(null);
  const [tempTargets, setTempTargets] = useState<TemperatureTarget[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [applying, setApplying] = useState<string | null>(null);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [applySuccess, setApplySuccess] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    Promise.all([
      api.noiseProfiles.list(),
      api.temperatureTargets.list(),
    ])
      .then(([profilesRes, targetsRes]) => {
        if (cancelled) return;
        setNoiseProfiles(profilesRes.profiles);
        setTempTargets(targetsRes.targets);
      })
      .catch(() => {
        if (!cancelled) setError('Failed to load noise profiles or temperature targets.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, []);

  // Build current fan speeds from store readings + appliedSpeeds
  const currentFanSpeeds = useMemo(() => {
    // fan_percent readings give us the current duty cycle per fan
    const fanPercentReadings = readings.filter((r) => r.sensor_type === 'fan_percent');

    if (fanPercentReadings.length > 0) {
      return fanPercentReadings.map((r) => ({
        fanId: r.id,
        fanName: r.name,
        percent: r.value,
      }));
    }

    // Fallback: use appliedSpeeds from the store
    return Object.entries(appliedSpeeds).map(([fanId, percent]) => ({
      fanId,
      fanName: fanId,
      percent,
    }));
  }, [readings, appliedSpeeds]);

  // Build temperature target inputs for the optimizer
  const tempTargetInputs = useMemo(() => {
    if (!tempTargets) return [];
    return tempTargets
      .filter((t) => t.enabled)
      .map((t) => {
        // Find the current sensor reading for this target
        const sensor = readings.find((r) => r.id === t.sensor_id);
        return {
          sensorId: t.sensor_id,
          target: t.target_temp_c,
          current: sensor?.value ?? t.target_temp_c, // default to target if no reading (no margin)
        };
      });
  }, [tempTargets, readings]);

  const recommendations = useMemo(() => {
    if (!noiseProfiles || !tempTargets) return [];
    return computeNoiseRecommendations(noiseProfiles, currentFanSpeeds, tempTargetInputs);
  }, [noiseProfiles, tempTargets, currentFanSpeeds, tempTargetInputs]);

  const handleApply = async (fanId: string, percent: number) => {
    setApplying(fanId);
    setApplyError(null);
    setApplySuccess(null);
    try {
      await api.setFanSpeed(fanId, percent);
      setApplySuccess(fanId);
      setTimeout(() => setApplySuccess(null), 3000);
    } catch {
      setApplyError(fanId);
      setTimeout(() => setApplyError(null), 4000);
    } finally {
      setApplying(null);
    }
  };

  if (loading) {
    return (
      <div className="card p-4 text-sm" style={{ color: 'var(--text-secondary)' }}>
        Loading noise advisor...
      </div>
    );
  }

  if (error) {
    return (
      <div className="card p-3 text-sm" style={{ color: 'var(--danger)', borderColor: 'var(--danger)', background: 'rgba(239,68,68,0.08)' }}>
        {error}
      </div>
    );
  }

  if (!noiseProfiles || noiseProfiles.length === 0) {
    return (
      <div className="card p-6 text-center">
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
          Run a noise profile in Settings first to enable recommendations.
        </p>
      </div>
    );
  }

  if (!tempTargets || tempTargets.filter((t) => t.enabled).length === 0) {
    return (
      <div className="card p-6 text-center">
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
          Configure temperature targets first to enable safe noise recommendations.
        </p>
      </div>
    );
  }

  if (recommendations.length === 0) {
    return (
      <div className="card p-6 text-center">
        <p className="text-sm" style={{ color: 'var(--success)' }}>
          No noise reductions available — fans are already at optimal levels or temperature margins are too slim.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {recommendations.map((rec) => {
        const isApplying = applying === rec.fanId;
        const isSuccess = applySuccess === rec.fanId;
        const isError = applyError === rec.fanId;

        return (
          <div
            key={rec.fanId}
            className="card p-4 animate-card-enter"
            style={{ borderLeft: '3px solid var(--accent)' }}
          >
            <div className="flex items-start justify-between gap-4 flex-wrap">
              <div style={{ flex: 1, minWidth: 200 }}>
                <p className="text-sm font-semibold mb-1" style={{ color: 'var(--text)' }}>
                  {rec.fanName}
                </p>
                <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  {rec.message}
                </p>
                <div className="flex gap-4 mt-2 flex-wrap">
                  <div>
                    <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Speed </span>
                    <span className="text-xs font-mono font-semibold" style={{ color: 'var(--text)' }}>
                      {rec.currentPercent}%
                    </span>
                    <span className="text-xs mx-1" style={{ color: 'var(--text-secondary)' }}>→</span>
                    <span className="text-xs font-mono font-semibold" style={{ color: 'var(--accent)' }}>
                      {rec.recommendedPercent}%
                    </span>
                  </div>
                  <div>
                    <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Noise savings </span>
                    <span className="text-xs font-mono font-semibold" style={{ color: 'var(--success)' }}>
                      ~{rec.noiseReductionDb.toFixed(1)} dB
                    </span>
                  </div>
                  <div>
                    <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Temp margin </span>
                    <span
                      className="text-xs font-mono font-semibold"
                      style={{ color: rec.temperatureMarginC < 3 ? 'var(--warning)' : 'var(--text)' }}
                    >
                      {rec.temperatureMarginC.toFixed(1)}°C
                    </span>
                  </div>
                </div>
              </div>

              <div style={{ flexShrink: 0 }}>
                {isSuccess ? (
                  <span className="badge badge-success text-xs">Applied</span>
                ) : isError ? (
                  <span className="badge badge-danger text-xs">Failed</span>
                ) : (
                  <button
                    className="btn-primary text-xs"
                    style={{ minHeight: 32, minWidth: 72 }}
                    disabled={isApplying}
                    onClick={() => handleApply(rec.fanId, rec.recommendedPercent)}
                  >
                    {isApplying ? 'Applying...' : 'Apply'}
                  </button>
                )}
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}
