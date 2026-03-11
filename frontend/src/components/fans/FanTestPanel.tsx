'use client';

import { useState, useCallback, useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { api } from '@/lib/api';
import type { FanTestProgress, FanTestResult, FanTestStep, FanSettings } from '@/lib/types';
import { Play, Square, RotateCcw, Wind } from 'lucide-react';
import { useCanWrite } from '@/hooks/useCanWrite';

interface Props {
  fanId: string;
  fanName: string;
}

// ─── Mini SVG bar chart ────────────────────────────────────────────────────────

function StepChart({ steps }: { steps: FanTestStep[] }) {
  if (steps.length === 0) return null;
  const maxRpm = Math.max(...steps.map((s) => s.rpm ?? 0), 1);
  const W = 280, H = 80, BAR_W = Math.max(4, (W - 4) / steps.length - 2);

  return (
    <svg viewBox={`0 0 ${W} ${H}`} className="w-full" style={{ height: 80 }}>
      {steps.map((step, i) => {
        const barH = ((step.rpm ?? 0) / maxRpm) * (H - 16);
        const x = 2 + i * ((W - 4) / steps.length);
        const y = H - barH - 14;
        const color = step.spinning ? 'var(--accent)' : 'var(--text-secondary)';
        return (
          <g key={i}>
            <rect x={x} y={y} width={BAR_W} height={barH} fill={color} rx={2} opacity={0.8} />
            <text
              x={x + BAR_W / 2}
              y={H - 2}
              textAnchor="middle"
              fontSize={8}
              fill="var(--text-secondary)"
            >
              {step.speed_pct}%
            </text>
          </g>
        );
      })}
    </svg>
  );
}

// ─── Step table ────────────────────────────────────────────────────────────────

function StepTable({ steps }: { steps: FanTestStep[] }) {
  return (
    <div className="overflow-auto" style={{ maxHeight: 180 }}>
      <table className="w-full text-xs" style={{ borderCollapse: 'collapse' }}>
        <thead>
          <tr style={{ color: 'var(--text-secondary)' }}>
            <th className="text-left py-1 pr-3">Speed</th>
            <th className="text-right py-1 pr-3">RPM</th>
            <th className="text-right py-1">Spinning</th>
          </tr>
        </thead>
        <tbody>
          {steps.map((s, i) => (
            <tr
              key={i}
              style={{
                borderTop: '1px solid var(--border)',
                color: s.spinning ? 'var(--text)' : 'var(--text-secondary)',
              }}
            >
              <td className="py-1 pr-3">{s.speed_pct}%</td>
              <td className="text-right py-1 pr-3">{s.rpm != null ? Math.round(s.rpm) : '—'}</td>
              <td className="text-right py-1">{s.spinning ? '✓' : '✗'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ─── Main panel ───────────────────────────────────────────────────────────────

export function FanTestPanel({ fanId, fanName }: Props) {
  const canWrite = useCanWrite();
  const fanTestProgress = useAppStore((s) => s.fanTestProgress);
  const [result, setResult] = useState<FanTestResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [estimatedSec, setEstimatedSec] = useState<number | null>(null);
  const [fanSettings, setFanSettings] = useState<FanSettings | null>(null);
  const [settingsSaving, setSettingsSaving] = useState(false);

  // Load fan settings on mount
  useEffect(() => {
    api.getFanSettings(fanId).then(setFanSettings).catch(() => {});
  }, [fanId]);

  // Live progress for this fan from the WebSocket store
  const liveProgress: FanTestProgress | undefined = fanTestProgress.find(
    (p) => p.fan_id === fanId
  );

  const isRunning = liveProgress?.status === 'running';

  const handleStart = useCallback(async () => {
    setBusy(true);
    setError(null);
    setResult(null);
    try {
      const res = await api.fanTests.start(fanId);
      setEstimatedSec(Math.ceil(res.estimated_duration_s));
    } catch (e: any) {
      setError(e?.message ?? 'Failed to start test');
    } finally {
      setBusy(false);
    }
  }, [fanId]);

  const handleCancel = useCallback(async () => {
    setBusy(true);
    try {
      await api.fanTests.cancel(fanId);
    } catch (e: any) {
      setError(e?.message ?? 'Failed to cancel');
    } finally {
      setBusy(false);
    }
  }, [fanId]);

  const handleFetchResult = useCallback(async () => {
    setBusy(true);
    try {
      const r = await api.fanTests.getResult(fanId);
      setResult(r);
    } catch (e: any) {
      setError(e?.message ?? 'No result available');
    } finally {
      setBusy(false);
    }
  }, [fanId]);

  // When live progress transitions to completed, fetch the full result
  useEffect(() => {
    if (liveProgress?.status === 'completed' && !result) {
      handleFetchResult();
    }
  }, [liveProgress?.status, result, handleFetchResult]);

  // ── Idle state ──────────────────────────────────────────────────────────────
  if (!isRunning && !result) {
    return (
      <div className="card p-4">
        <div className="flex items-center gap-2 mb-3">
          <Wind size={15} style={{ color: 'var(--accent)' }} />
          <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>
            {fanName}
          </span>
        </div>
        <p className="text-xs mb-3" style={{ color: 'var(--text-secondary)' }}>
          Sweep 0–100% in 10 steps and record RPM at each point. Finds stall speed and max RPM.
          Takes ~27 s with defaults.
        </p>
        {error && (
          <p className="text-xs mb-2" style={{ color: 'var(--danger)' }}>
            {error}
          </p>
        )}
        <button
          onClick={handleStart}
          disabled={busy || !canWrite}
          className="btn-primary flex items-center gap-2 text-xs disabled:opacity-50"
        >
          <Play size={12} />
          Run Benchmark
        </button>
      </div>
    );
  }

  // ── Running state ───────────────────────────────────────────────────────────
  if (isRunning && liveProgress) {
    const pct =
      liveProgress.steps_total > 0
        ? (liveProgress.steps_done / liveProgress.steps_total) * 100
        : 0;

    return (
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <Wind size={15} style={{ color: 'var(--accent)' }} />
            <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>
              {fanName}
            </span>
            <span className="badge badge-warning text-xs">Running</span>
          </div>
          <button
            onClick={handleCancel}
            disabled={busy}
            className="text-xs flex items-center gap-1"
            style={{ color: 'var(--text-secondary)' }}
          >
            <Square size={10} />
            Cancel
          </button>
        </div>

        {/* Progress bar */}
        <div className="mb-3">
          <div className="flex justify-between text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>
            <span>
              Step {liveProgress.steps_done} / {liveProgress.steps_total}
            </span>
            <span>{liveProgress.current_pct}% @ {liveProgress.current_rpm != null ? Math.round(liveProgress.current_rpm) + ' RPM' : '…'}</span>
          </div>
          <div
            className="rounded-full overflow-hidden"
            style={{ height: 6, background: 'var(--border)' }}
          >
            <div
              className="h-full rounded-full transition-all duration-500"
              style={{ width: `${pct}%`, background: 'var(--accent)' }}
            />
          </div>
          {estimatedSec && (
            <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
              Estimated ~{estimatedSec}s total
            </p>
          )}
        </div>

        {liveProgress.steps.length > 0 && <StepTable steps={liveProgress.steps} />}
      </div>
    );
  }

  // ── Completed / cancelled / failed state ────────────────────────────────────
  if (result) {
    const statusBadge =
      result.status === 'completed'
        ? 'badge-success'
        : result.status === 'cancelled'
          ? 'badge-warning'
          : 'badge-danger';

    return (
      <div className="card p-4">
        <div className="flex items-center justify-between mb-3">
          <div className="flex items-center gap-2">
            <Wind size={15} style={{ color: 'var(--accent)' }} />
            <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>
              {fanName}
            </span>
            <span className={`badge ${statusBadge} text-xs capitalize`}>{result.status}</span>
          </div>
          <button
            onClick={() => { setResult(null); setError(null); }}
            className="text-xs flex items-center gap-1"
            style={{ color: 'var(--text-secondary)' }}
          >
            <RotateCcw size={10} />
            Run Again
          </button>
        </div>

        {/* Summary badges */}
        {result.status === 'completed' && (
          <div className="flex flex-wrap gap-2 mb-3">
            <span
              className="badge badge-info text-xs"
              title="Lowest speed where the fan was detected spinning"
            >
              Stall: {result.min_operational_pct != null ? `${result.min_operational_pct}%` : 'n/a'}
            </span>
            <span
              className="badge badge-info text-xs"
              title="RPM recorded at 100% duty cycle"
            >
              Max: {result.max_rpm != null ? `${Math.round(result.max_rpm)} RPM` : 'n/a'}
            </span>
            <span className="badge text-xs" style={{ background: 'var(--border)', color: 'var(--text-secondary)' }}>
              {result.steps.length} steps
            </span>
          </div>
        )}

        {result.error && (
          <p className="text-xs mb-2" style={{ color: 'var(--danger)' }}>
            {result.error}
          </p>
        )}

        {result.steps.length > 0 && (
          <>
            <StepChart steps={result.steps} />
            <div className="mt-2">
              <StepTable steps={result.steps} />
            </div>
          </>
        )}

        {/* Apply calibration from benchmark */}
        {result.status === 'completed' && result.min_operational_pct != null && fanSettings && canWrite && (
          <div className="mt-3 pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <p className="text-xs font-medium mb-2" style={{ color: 'var(--text)' }}>Apply Calibration</p>
            <p className="text-xs mb-2" style={{ color: 'var(--text-secondary)' }}>
              Set min speed floor to <strong>{result.min_operational_pct}%</strong> (stall + margin)
              {result.steps[0]?.spinning === false ? ' and mark as zero-RPM capable' : ''}.
            </p>
            <div className="flex items-center gap-2 text-xs mb-2" style={{ color: 'var(--text-secondary)' }}>
              <span>Current: {fanSettings.min_speed_pct}%</span>
              <span style={{ color: 'var(--accent)' }}>→</span>
              <span>Proposed: {result.min_operational_pct}%</span>
            </div>
            <button
              disabled={settingsSaving}
              onClick={async () => {
                setSettingsSaving(true);
                const zeroRpm = result.steps[0]?.spinning === false;
                const newSettings = {
                  min_speed_pct: result.min_operational_pct!,
                  zero_rpm_capable: zeroRpm,
                };
                try {
                  await api.updateFanSettings(fanId, newSettings);
                  setFanSettings({ ...fanSettings, ...newSettings });
                } catch (e: any) { setError(e?.message || 'Failed to apply calibration'); }
                setSettingsSaving(false);
              }}
              className="btn-primary text-xs px-3 py-1"
            >
              {settingsSaving ? 'Applying...' : 'Apply Calibration'}
            </button>
          </div>
        )}

        {/* Fan Settings — min speed floor & zero-RPM */}
        {fanSettings && (
          <div className="mt-3 pt-3" style={{ borderTop: '1px solid var(--border)' }}>
            <p className="text-xs font-medium mb-2" style={{ color: 'var(--text)' }}>
              Fan Settings
            </p>
            <div className="space-y-2">
              <label className="flex items-center gap-2 text-xs" style={{ color: 'var(--text-secondary)' }}>
                Min Speed Floor:
                <input
                  type="range"
                  min={0}
                  max={100}
                  step={1}
                  value={fanSettings.min_speed_pct}
                  onChange={(e) =>
                    canWrite && setFanSettings({ ...fanSettings, min_speed_pct: Number(e.target.value) })
                  }
                  disabled={!canWrite}
                  className="flex-1"
                />
                <span className="w-8 text-right">{fanSettings.min_speed_pct}%</span>
              </label>
              <label className="flex items-center gap-2 text-xs" style={{ color: 'var(--text-secondary)' }}>
                <input
                  type="checkbox"
                  checked={fanSettings.zero_rpm_capable}
                  disabled={!canWrite}
                  onChange={(e) =>
                    canWrite && setFanSettings({ ...fanSettings, zero_rpm_capable: e.target.checked })
                  }
                />
                Zero-RPM capable (allow 0% speed)
              </label>
              <button
                disabled={settingsSaving || !canWrite}
                onClick={async () => {
                  setSettingsSaving(true);
                  try {
                    await api.updateFanSettings(fanId, {
                      min_speed_pct: fanSettings.min_speed_pct,
                      zero_rpm_capable: fanSettings.zero_rpm_capable,
                    });
                  } catch (e: any) { setError(e?.message || 'Failed to save settings'); }
                  setSettingsSaving(false);
                }}
                className="btn-primary text-xs px-3 py-1"
              >
                {settingsSaving ? 'Saving...' : 'Save Settings'}
              </button>
            </div>
          </div>
        )}
      </div>
    );
  }

  return null;
}
