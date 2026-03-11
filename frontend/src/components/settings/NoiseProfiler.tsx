'use client';

/**
 * NoiseProfiler — Web Audio noise sweep component.
 *
 * Sweeps a selected fan from 0–100% in steps, measures ambient dB at each
 * step, then saves a noise-vs-RPM curve as a NoiseProfile via the API.
 */

import React, { useState, useEffect, useRef, useCallback } from 'react';
import { api } from '@/lib/api';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useCanWrite } from '@/hooks/useCanWrite';
import type { NoiseProfile, NoiseDataPoint } from '@/lib/types';
import { AudioMeter } from '@/lib/audioMeter';
import { Trash2, Mic, MicOff, Play, StopCircle } from 'lucide-react';
import { useConfirm } from '@/components/ui/ConfirmDialog';
import { useToast } from '@/components/ui/ToastProvider';

// Sweep steps as percentages (0, 10, 20 … 100)
const QUICK_STEPS = [0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
const PRECISE_STEPS = [0, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100];

const SETTLE_MS = 5000;   // wait for fan to reach target speed
const MEASURE_MS = 3000;  // measure dB for this long at each step

type SweepState = 'idle' | 'settling' | 'measuring' | 'done' | 'error';

interface LiveStep {
  pct: number;
  db: number | null;
}

export function NoiseProfiler() {
  const confirm = useConfirm();
  const toast = useToast();
  const canWrite = useCanWrite();

  const readings = useAppStore((s) => s.readings);
  const { sensorLabels } = useSettingsStore();
  const fanSensors = readings.filter((r) => r.sensor_type === 'fan_rpm');

  const [profiles, setProfiles] = useState<NoiseProfile[]>([]);
  const [selectedFanId, setSelectedFanId] = useState('');
  const [mode, setMode] = useState<'quick' | 'precise'>('quick');
  const [muteFanIds, setMuteFanIds] = useState<Set<string>>(new Set());

  // Sweep state
  const [sweepState, setSweepState] = useState<SweepState>('idle');
  const [currentStep, setCurrentStep] = useState(0);
  const [liveSteps, setLiveSteps] = useState<LiveStep[]>([]);
  const [liveDB, setLiveDB] = useState<number | null>(null);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);

  const abortRef = useRef(false);
  const meterRef = useRef<AudioMeter | null>(null);
  const liveDBIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // Load existing profiles on mount
  useEffect(() => {
    api.noiseProfiles.list().then((r) => setProfiles(r.profiles)).catch(() => {});
  }, []);

  // Default fan selection when sensors load
  useEffect(() => {
    if (!selectedFanId && fanSensors.length > 0) {
      setSelectedFanId(fanSensors[0].id);
    }
  }, [fanSensors, selectedFanId]);

  // Live dB polling while measuring
  const startLiveDB = useCallback(() => {
    liveDBIntervalRef.current = setInterval(() => {
      if (meterRef.current) {
        const db = meterRef.current.getDB();
        setLiveDB(isFinite(db) ? db : null);
      }
    }, 150);
  }, []);

  const stopLiveDB = useCallback(() => {
    if (liveDBIntervalRef.current) {
      clearInterval(liveDBIntervalRef.current);
      liveDBIntervalRef.current = null;
    }
    setLiveDB(null);
  }, []);

  // Clean up on unmount
  useEffect(() => {
    return () => {
      stopLiveDB();
      abortRef.current = true;
      meterRef.current?.stop();
    };
  }, [stopLiveDB]);

  const runSweep = async () => {
    if (!selectedFanId) return;

    abortRef.current = false;
    setErrorMsg(null);
    setSweepState('idle');
    setLiveSteps([]);
    setCurrentStep(0);

    // Request microphone
    const meter = new AudioMeter();
    meterRef.current = meter;
    try {
      await meter.start();
    } catch (err) {
      setErrorMsg('Microphone access denied. Please allow microphone use and try again.');
      setSweepState('error');
      meter.stop();
      meterRef.current = null;
      return;
    }

    const steps = mode === 'quick' ? QUICK_STEPS : PRECISE_STEPS;
    const collectedData: NoiseDataPoint[] = [];

    startLiveDB();

    try {
      for (let i = 0; i < steps.length; i++) {
        if (abortRef.current) break;

        const pct = steps[i];
        setCurrentStep(i);

        // Mute other fans if in precise mode
        if (mode === 'precise') {
          for (const fid of muteFanIds) {
            await api.setFanSpeed(fid, 0).catch(() => {});
          }
        }

        // Set target fan speed
        await api.setFanSpeed(selectedFanId, pct).catch(() => {});

        // Settle
        setSweepState('settling');
        await sleep(SETTLE_MS, abortRef);
        if (abortRef.current) break;

        // Measure
        setSweepState('measuring');
        const db = await meter.measureMedianDB(MEASURE_MS);
        if (abortRef.current) break;

        const rpmReading = fanSensors.find((s) => s.id === selectedFanId);
        const rpm = rpmReading?.value ?? pct * 20; // fallback: rough estimate

        collectedData.push({ rpm, db: isFinite(db) ? db : 0 });
        setLiveSteps((prev) => [...prev, { pct, db: isFinite(db) ? db : null }]);
      }
    } finally {
      stopLiveDB();
      meter.stop();
      meterRef.current = null;
    }

    if (abortRef.current) {
      setSweepState('idle');
      // Release fan control
      await api.releaseFanControl().catch(() => {});
      return;
    }

    if (collectedData.length === 0) {
      setErrorMsg('No data collected during sweep.');
      setSweepState('error');
      await api.releaseFanControl().catch(() => {});
      return;
    }

    // Save the profile
    try {
      const saved = await api.noiseProfiles.create({
        fan_id: selectedFanId,
        mode,
        data: collectedData,
      });
      setProfiles((prev) => [saved, ...prev]);
      setSweepState('done');
      toast('Noise profile saved', 'success');
    } catch (err) {
      setErrorMsg('Failed to save noise profile.');
      setSweepState('error');
    }

    // Release fan control back to profile
    await api.releaseFanControl().catch(() => {});
  };

  const handleStop = () => {
    abortRef.current = true;
  };

  const handleDelete = async (id: string) => {
    const ok = await confirm({
      title: 'Delete noise profile',
      message: 'This action cannot be undone.',
      confirmLabel: 'Delete',
      danger: true,
    });
    if (!ok) return;
    try {
      await api.noiseProfiles.delete(id);
      setProfiles((prev) => prev.filter((p) => p.id !== id));
      toast('Profile deleted', 'success');
    } catch {
      toast('Failed to delete profile', 'error');
    }
  };

  const steps = mode === 'quick' ? QUICK_STEPS : PRECISE_STEPS;
  const isRunning = sweepState === 'settling' || sweepState === 'measuring';

  const fanLabel = (fanId: string) => {
    const sensor = fanSensors.find((s) => s.id === fanId);
    return sensorLabels[fanId] || sensor?.name || fanId;
  };

  return (
    <div className="space-y-5">
      <h2 className="section-title">Noise Profiler</h2>
      <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
        Measure fan noise at each speed step using your microphone. The sweep
        sets the fan speed, waits for it to stabilise, then records dB(A).
      </p>

      {/* Configuration */}
      <div className="card p-4 space-y-4 animate-card-enter">
        <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
          Sweep Configuration
        </h3>

        <div className="grid gap-4" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}>
          {/* Fan selector */}
          <div className="space-y-1">
            <label className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              Fan to profile
            </label>
            <select
              className="w-full text-xs p-2 rounded"
              style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              value={selectedFanId}
              onChange={(e) => setSelectedFanId(e.target.value)}
              disabled={isRunning}
            >
              {fanSensors.length === 0 && (
                <option value="">No fans detected</option>
              )}
              {fanSensors.map((s) => (
                <option key={s.id} value={s.id}>
                  {sensorLabels[s.id] || s.name || s.id}
                </option>
              ))}
            </select>
          </div>

          {/* Mode selector */}
          <div className="space-y-1">
            <label className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              Mode
            </label>
            <select
              className="w-full text-xs p-2 rounded"
              style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              value={mode}
              onChange={(e) => setMode(e.target.value as 'quick' | 'precise')}
              disabled={isRunning}
            >
              <option value="quick">Quick ({QUICK_STEPS.length} steps, ~{Math.round(QUICK_STEPS.length * (SETTLE_MS + MEASURE_MS) / 60000)} min)</option>
              <option value="precise">Precise ({PRECISE_STEPS.length} steps, ~{Math.round(PRECISE_STEPS.length * (SETTLE_MS + MEASURE_MS) / 60000)} min)</option>
            </select>
          </div>
        </div>

        {/* Precise mode: other fans to mute */}
        {mode === 'precise' && fanSensors.length > 1 && (
          <div className="space-y-1">
            <label className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              Mute other fans during sweep (reduces background noise)
            </label>
            <div className="flex flex-wrap gap-2 mt-1">
              {fanSensors
                .filter((s) => s.id !== selectedFanId)
                .map((s) => (
                  <label key={s.id} className="flex items-center gap-1.5 text-xs cursor-pointer">
                    <input
                      type="checkbox"
                      checked={muteFanIds.has(s.id)}
                      disabled={isRunning}
                      onChange={(e) => {
                        setMuteFanIds((prev) => {
                          const next = new Set(prev);
                          if (e.target.checked) next.add(s.id);
                          else next.delete(s.id);
                          return next;
                        });
                      }}
                    />
                    <span style={{ color: 'var(--text)' }}>{sensorLabels[s.id] || s.name || s.id}</span>
                  </label>
                ))}
            </div>
          </div>
        )}

        {/* Start / Stop */}
        <div className="flex items-center gap-3 pt-1">
          {!isRunning ? (
            <button
              className="btn-primary flex items-center gap-2 text-xs"
              disabled={!selectedFanId || sweepState === 'done' || !canWrite}
              onClick={runSweep}
            >
              <Mic size={13} />
              Start Sweep
            </button>
          ) : (
            <button
              className="btn-secondary flex items-center gap-2 text-xs"
              style={{ color: 'var(--danger)', borderColor: 'var(--danger)' }}
              onClick={handleStop}
            >
              <StopCircle size={13} />
              Stop
            </button>
          )}
          {sweepState === 'done' && (
            <button
              className="btn-secondary text-xs"
              onClick={() => {
                setSweepState('idle');
                setLiveSteps([]);
                setCurrentStep(0);
              }}
            >
              New Sweep
            </button>
          )}
        </div>
      </div>

      {/* Progress / Live meter */}
      {(isRunning || sweepState === 'done') && (
        <div className="card p-4 space-y-3 animate-card-enter">
          <div className="flex items-center justify-between">
            <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
              {isRunning ? (
                sweepState === 'settling'
                  ? `Step ${currentStep + 1}/${steps.length} — Settling (${steps[currentStep]}%)…`
                  : `Step ${currentStep + 1}/${steps.length} — Measuring (${steps[currentStep]}%)…`
              ) : 'Sweep complete'}
            </h3>
            {liveDB !== null && isRunning && (
              <span
                className="text-xs font-mono px-2 py-0.5 rounded"
                style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}
              >
                {liveDB.toFixed(1)} dB(A)
              </span>
            )}
          </div>

          {/* Progress bar */}
          <div className="w-full rounded-full overflow-hidden" style={{ height: 6, background: 'var(--surface-200)' }}>
            <div
              className="h-full rounded-full transition-all"
              style={{
                width: `${((currentStep + (sweepState === 'done' ? 1 : 0)) / steps.length) * 100}%`,
                background: 'var(--accent)',
              }}
            />
          </div>

          {/* Noise-vs-RPM chart (SVG sparkline) */}
          {liveSteps.length > 1 && (
            <NoiseCurveChart steps={liveSteps} />
          )}

          {/* Step table */}
          {liveSteps.length > 0 && (
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr style={{ color: 'var(--text-secondary)' }}>
                    <th className="text-left pb-1">Speed</th>
                    <th className="text-right pb-1">dB(A)</th>
                  </tr>
                </thead>
                <tbody>
                  {liveSteps.map((s, i) => (
                    <tr key={i} style={{ borderTop: '1px solid var(--border)' }}>
                      <td className="py-0.5" style={{ color: 'var(--text)' }}>{s.pct}%</td>
                      <td className="text-right py-0.5" style={{ color: 'var(--text)' }}>
                        {s.db !== null ? s.db.toFixed(1) : '—'}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {/* Error */}
      {sweepState === 'error' && errorMsg && (
        <div
          className="card p-3 text-xs flex items-center gap-2 animate-card-enter"
          style={{ color: 'var(--danger)', borderColor: 'var(--danger)' }}
        >
          <MicOff size={14} />
          {errorMsg}
        </div>
      )}

      {/* Saved profiles */}
      {profiles.length > 0 && (
        <div className="card p-4 space-y-3 animate-card-enter">
          <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
            Saved Profiles
          </h3>
          <div className="space-y-2">
            {profiles.map((p) => (
              <div
                key={p.id}
                className="flex items-center justify-between gap-3 p-2 rounded"
                style={{ background: 'var(--surface-200)' }}
              >
                <div className="min-w-0">
                  <span className="text-xs font-medium" style={{ color: 'var(--text)' }}>
                    {fanLabel(p.fan_id)}
                  </span>
                  <span
                    className="ml-2 badge"
                    style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}
                  >
                    {p.mode}
                  </span>
                  <span className="ml-2 text-xs" style={{ color: 'var(--text-secondary)' }}>
                    {p.data.length} pts · {new Date(p.created_at).toLocaleDateString()}
                  </span>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  {p.data.length > 1 && (
                    <SavedProfileSparkline data={p.data} />
                  )}
                  <button
                    className="p-1 rounded hover:bg-red-900/20 transition-colors"
                    style={{ color: 'var(--danger)' }}
                    title="Delete profile"
                    onClick={() => handleDelete(p.id)}
                  >
                    <Trash2 size={13} />
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function sleep(ms: number, abortRef: React.MutableRefObject<boolean>): Promise<void> {
  return new Promise<void>((resolve) => {
    const step = 100;
    let elapsed = 0;
    const tick = setInterval(() => {
      elapsed += step;
      if (elapsed >= ms || abortRef.current) {
        clearInterval(tick);
        resolve();
      }
    }, step);
  });
}

// ── Sub-components ────────────────────────────────────────────────────────────

interface NoiseCurveChartProps {
  steps: LiveStep[];
}

function NoiseCurveChart({ steps }: NoiseCurveChartProps) {
  const W = 300;
  const H = 80;
  const PAD = 8;

  const validSteps = steps.filter((s) => s.db !== null) as { pct: number; db: number }[];
  if (validSteps.length < 2) return null;

  const minDB = Math.min(...validSteps.map((s) => s.db));
  const maxDB = Math.max(...validSteps.map((s) => s.db));
  const dbRange = maxDB - minDB || 1;

  const pts = validSteps.map((s) => {
    const x = PAD + (s.pct / 100) * (W - PAD * 2);
    const y = PAD + (1 - (s.db - minDB) / dbRange) * (H - PAD * 2);
    return `${x},${y}`;
  });

  return (
    <svg
      width={W}
      height={H}
      style={{ display: 'block', borderRadius: 4, background: 'var(--surface-200)' }}
    >
      <polyline
        points={pts.join(' ')}
        fill="none"
        stroke="var(--accent)"
        strokeWidth={2}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
      {validSteps.map((s, i) => {
        const x = PAD + (s.pct / 100) * (W - PAD * 2);
        const y = PAD + (1 - (s.db - minDB) / dbRange) * (H - PAD * 2);
        return <circle key={i} cx={x} cy={y} r={3} fill="var(--accent)" />;
      })}
    </svg>
  );
}

interface SavedProfileSparklineProps {
  data: NoiseDataPoint[];
}

function SavedProfileSparkline({ data }: SavedProfileSparklineProps) {
  const W = 80;
  const H = 30;
  const PAD = 3;

  const minDB = Math.min(...data.map((d) => d.db));
  const maxDB = Math.max(...data.map((d) => d.db));
  const dbRange = maxDB - minDB || 1;
  const minRPM = Math.min(...data.map((d) => d.rpm));
  const maxRPM = Math.max(...data.map((d) => d.rpm));
  const rpmRange = maxRPM - minRPM || 1;

  const sorted = [...data].sort((a, b) => a.rpm - b.rpm);
  const pts = sorted.map((d) => {
    const x = PAD + ((d.rpm - minRPM) / rpmRange) * (W - PAD * 2);
    const y = PAD + (1 - (d.db - minDB) / dbRange) * (H - PAD * 2);
    return `${x},${y}`;
  });

  return (
    <svg
      width={W}
      height={H}
      style={{ display: 'block' }}
      aria-label="Noise-vs-RPM curve"
    >
      <polyline
        points={pts.join(' ')}
        fill="none"
        stroke="var(--accent)"
        strokeWidth={1.5}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
    </svg>
  );
}
