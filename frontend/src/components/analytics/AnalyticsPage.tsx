'use client';

import { useState, useEffect, useCallback, useRef } from 'react';
import { api } from '@/lib/api';
import { useSettingsStore } from '@/stores/settingsStore';
import { displayTemp, tempUnitSymbol } from '@/lib/tempUnit';
import { ExportButtons } from './ExportButtons';
import { Heatmap } from './Heatmap';
import { CoolingScore } from './CoolingScore';
import { TrendChart } from './TrendChart';
import { PeriodComparison } from './PeriodComparison';
import { NoiseAdvisor } from './NoiseAdvisor';
import { useCanWrite } from '@/hooks/useCanWrite';
import type { AnalyticsStat, AnalyticsAnomaly, AnalyticsBucket, ThermalRegression, AnalyticsCorrelationSample, Annotation } from '@/lib/types';

const TIME_OPTIONS = [
  { label: '1h',  hours: 1 },
  { label: '6h',  hours: 6 },
  { label: '24h', hours: 24 },
  { label: '7d',  hours: 168 },
  { label: '30d', hours: 720 },
];

type FmtFn = (v: number, unit: string) => string;

function Sparkline({ buckets, fmt }: { buckets: AnalyticsBucket[]; fmt: FmtFn }) {
  const W = 120; const H = 36;
  const sorted = [...buckets].sort((a, b) => new Date(a.timestamp_utc).getTime() - new Date(b.timestamp_utc).getTime());
  if (sorted.length < 2) return null;
  const vals = sorted.map((b) => b.avg_value);
  const minV = Math.min(...vals); const maxV = Math.max(...vals); const range = maxV - minV || 1;
  const pts = sorted.map((b, i) => {
    const x = (i / (sorted.length - 1)) * W;
    const y = H - ((b.avg_value - minV) / range) * H;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  const last = sorted[sorted.length - 1];
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
      <svg viewBox={`0 0 ${W} ${H}`} width={W} height={H} style={{ display: 'block', flexShrink: 0 }}>
        <polyline points={pts} fill="none" stroke="var(--accent)" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" />
      </svg>
      <span className="text-sm font-mono font-semibold" style={{ color: 'var(--accent)', minWidth: 56 }}>
        {fmt(last.avg_value, last.unit)}
      </span>
    </div>
  );
}

function StatCard({ s, accentColor, fmt }: { s: AnalyticsStat; accentColor: string; fmt: FmtFn }) {
  return (
    <div className="card p-4 animate-card-enter" style={{ borderLeft: `3px solid ${accentColor}` }}>
      <p className="text-xs font-semibold mb-3 truncate" style={{ color: accentColor }}>{s.sensor_name}</p>
      <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs">
        {(['Min', 'Max', 'Avg', 'P95'] as const).map((lbl) => {
          const key = lbl === 'P95' ? 'p95_value' : (`${lbl.toLowerCase()}_value` as keyof AnalyticsStat);
          const val = s[key] as number | null;
          return [
            <span key={`${lbl}l`} style={{ color: 'var(--text-secondary)' }}>{lbl}</span>,
            <span key={`${lbl}v`} className="font-mono text-right" style={{ color: 'var(--text)' }}>
              {val != null ? fmt(val, s.unit) : '—'}
            </span>,
          ];
        })}
      </div>
      <p className="text-xs mt-3" style={{ color: 'var(--text-secondary)' }}>{s.sample_count.toLocaleString()} samples</p>
    </div>
  );
}

function CorrelationScatter({ samples, labelX }: { samples: AnalyticsCorrelationSample[]; labelX: string; labelY: string }) {
  const W = 200; const H = 120; const PAD = 14;
  if (samples.length < 3) return null;
  const xs = samples.map((s) => s.x); const ys = samples.map((s) => s.y);
  const minX = Math.min(...xs); const maxX = Math.max(...xs); const rangeX = maxX - minX || 1;
  const minY = Math.min(...ys); const maxY = Math.max(...ys); const rangeY = maxY - minY || 1;
  const innerW = W - PAD * 2; const innerH = H - PAD * 2;
  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" style={{ maxWidth: W * 2, display: 'block' }}>
      <line x1={PAD} y1={PAD} x2={PAD} y2={H - PAD} stroke="var(--border)" strokeWidth="1" />
      <line x1={PAD} y1={H - PAD} x2={W - PAD} y2={H - PAD} stroke="var(--border)" strokeWidth="1" />
      <text x={PAD} y={H - 2} fontSize="8" fill="var(--text-secondary)">{labelX.slice(0, 16)}</text>
      {samples.map((s, i) => {
        const cx = PAD + ((s.x - minX) / rangeX) * innerW;
        const cy = PAD + innerH - ((s.y - minY) / rangeY) * innerH;
        return <circle key={i} cx={cx} cy={cy} r={2} fill="var(--accent)" opacity="0.6" />;
      })}
    </svg>
  );
}

export function AnalyticsPage() {
  const [hours, setHours] = useState(24);
  const [customStart, setCustomStart] = useState('');
  const [customEnd, setCustomEnd]     = useState('');
  const [selectedSensorIds, setSelectedSensorIds] = useState<string[]>([]);

  const [stats, setStats]           = useState<AnalyticsStat[] | null>(null);
  const [anomalies, setAnomalies]   = useState<AnalyticsAnomaly[] | null>(null);
  const [history, setHistory]       = useState<AnalyticsBucket[] | null>(null);
  const [regressions, setRegressions] = useState<ThermalRegression[] | null>(null);
  const [retentionLimited, setRetentionLimited] = useState(false);

  const [annotations, setAnnotations] = useState<Annotation[]>([]);
  const [showAnnotationForm, setShowAnnotationForm] = useState(false);
  const [annTimestamp, setAnnTimestamp] = useState('');
  const [annLabel, setAnnLabel] = useState('');
  const [annDescription, setAnnDescription] = useState('');

  const [loading, setLoading]       = useState(false);
  const [error, setError]           = useState<string | null>(null);

  // Correlation state
  const [corrX, setCorrX]           = useState('');
  const [corrY, setCorrY]           = useState('');
  const [corrResult, setCorrResult] = useState<{ coeff: number; samples: AnalyticsCorrelationSample[]; count: number } | null>(null);
  const [corrLoading, setCorrLoading] = useState(false);
  const corrGenRef = useRef(0);

  const canWrite = useCanWrite();
  const { tempUnit } = useSettingsStore();

  const fmt = useCallback<FmtFn>((v, unit) => {
    if (unit === '°C') return `${displayTemp(v, tempUnit).toFixed(1)} ${tempUnitSymbol(tempUnit)}`;
    return `${v.toFixed(1)} ${unit}`;
  }, [tempUnit]);

  const fmtDelta = useCallback((v: number, unit: string) => {
    if (unit === '°C' && tempUnit === 'F') return `${(v * 9 / 5).toFixed(1)} ${tempUnitSymbol(tempUnit)}`;
    if (unit === '°C') return `${v.toFixed(1)} ${tempUnitSymbol(tempUnit)}`;
    return `${v.toFixed(1)} ${unit}`;
  }, [tempUnit]);

  // Build API opts from custom range + sensor filter.
  // Custom range requires both start and end; one-sided input is ignored.
  const buildOpts = useCallback(() => {
    const opts: { start?: string; end?: string; sensorIds?: string[] } = {};
    if (customStart && customEnd) {
      opts.start = new Date(customStart).toISOString();
      opts.end   = new Date(customEnd).toISOString();
    }
    if (selectedSensorIds.length > 0) opts.sensorIds = selectedSensorIds;
    return opts;
  }, [customStart, customEnd, selectedSensorIds]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true); setError(null);
    const opts = buildOpts();
    // Let the backend auto-size buckets — do not send explicit bucket_seconds
    Promise.all([
      api.analytics.getStats(hours, undefined, opts),
      api.analytics.getAnomalies(hours, 3.0, opts),
      api.analytics.getHistory(hours, undefined, undefined, opts),
      api.analytics.getRegression(30, Math.min(hours, 168), 5.0, opts),
      api.annotations.list(opts.start, opts.end),
    ])
      .then(([sR, aR, hR, rR, annR]) => {
        if (cancelled) return;
        setStats(sR.stats);
        setAnomalies(aR.anomalies);
        setHistory(hR.buckets);
        setRegressions(rR.regressions);
        setRetentionLimited(hR.retention_limited);
        setAnnotations(annR);
      })
      .catch(() => { if (!cancelled) setError('Failed to load analytics data.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hours, customStart, customEnd, selectedSensorIds]);

  const handleCorrelate = async () => {
    if (!corrX || !corrY || corrX === corrY) return;
    const gen = ++corrGenRef.current;
    setCorrLoading(true); setCorrResult(null);
    try {
      const r = await api.analytics.getCorrelation(corrX, corrY, hours, buildOpts());
      if (gen !== corrGenRef.current) return; // stale request
      setCorrResult({ coeff: r.correlation_coefficient, samples: r.samples, count: r.sample_count });
    } catch {
      if (gen !== corrGenRef.current) return;
      setCorrResult({ coeff: NaN, samples: [], count: 0 });
    } finally {
      if (gen === corrGenRef.current) setCorrLoading(false);
    }
  };

  const handleCreateAnnotation = async () => {
    if (!annTimestamp || !annLabel.trim()) return;
    try {
      const created = await api.annotations.create({
        timestamp_utc: new Date(annTimestamp).toISOString(),
        label: annLabel.trim(),
        description: annDescription.trim() || undefined,
      });
      setAnnotations((prev) => [created, ...prev]);
      setAnnTimestamp('');
      setAnnLabel('');
      setAnnDescription('');
      setShowAnnotationForm(false);
    } catch { setError('Failed to create annotation.'); }
  };

  const handleDeleteAnnotation = async (id: string) => {
    try {
      await api.annotations.delete(id);
      setAnnotations((prev) => prev.filter((a) => a.id !== id));
    } catch { setError('Failed to delete annotation.'); }
  };

  const toggleSensor = (id: string) => {
    setSelectedSensorIds((prev) =>
      prev.includes(id) ? prev.filter((s) => s !== id) : [...prev, id]
    );
  };

  const thermalStats = stats?.filter((s) => s.sensor_type.includes('temp') && s.sensor_type !== 'hdd_temp') ?? [];
  const driveStats   = stats?.filter((s) => s.sensor_type === 'hdd_temp') ?? [];
  const fanStats     = stats?.filter((s) => s.sensor_type === 'fan_rpm') ?? [];
  const sortedAnomalies = anomalies ? [...anomalies].sort((a, b) => b.z_score - a.z_score) : null;

  const tempBuckets: Record<string, AnalyticsBucket[]> = {};
  if (history) {
    for (const b of history) {
      if (b.sensor_type.includes('temp')) {
        (tempBuckets[b.sensor_id] ??= []).push(b);
      }
    }
  }

  // All sensors for the correlation dropdowns and filter chips
  const allSensorOptions = stats ?? [];
  const corrXName = allSensorOptions.find((s) => s.sensor_id === corrX)?.sensor_name ?? corrX;
  const corrYName = allSensorOptions.find((s) => s.sensor_id === corrY)?.sensor_name ?? corrY;
  const coeffColor = corrResult
    ? Math.abs(corrResult.coeff) >= 0.7 ? 'var(--success)'
    : Math.abs(corrResult.coeff) >= 0.4 ? 'var(--warning)'
    : 'var(--text-secondary)'
    : 'var(--text)';

  const windowLabel = customStart && customEnd
    ? `${new Date(customStart).toLocaleDateString()} - ${new Date(customEnd).toLocaleDateString()}`
    : TIME_OPTIONS.find((o) => o.hours === hours)?.label ?? `${hours}h`;

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Print-only header (hidden on screen, visible when printing) */}
      <div className="print-header">
        <h1>DriveChill Analytics Report</h1>
        <div className="print-meta">
          Generated: {new Date().toLocaleString()} | Window: {windowLabel}
        </div>
      </div>

      {/* Time window picker */}
      <div className="space-y-3">
        <div className="flex items-center gap-2 flex-wrap">
          {TIME_OPTIONS.map((o) => (
            <button
              key={o.hours}
              onClick={() => { setHours(o.hours); setCustomStart(''); setCustomEnd(''); }}
              className={hours === o.hours && !customStart && !customEnd ? 'btn-primary' : 'btn-secondary'}
              style={{ minWidth: 44 }}
            >
              {o.label}
            </button>
          ))}
          {loading && <span className="text-xs ml-2" style={{ color: 'var(--text-secondary)' }}>Loading...</span>}
        </div>
        {/* Custom date range */}
        <div className="flex items-center gap-2 flex-wrap">
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Custom:</span>
          <input
            type="datetime-local"
            value={customStart}
            onChange={(e) => setCustomStart(e.target.value)}
            className="text-xs px-2 py-1 rounded"
            style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32 }}
          />
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>to</span>
          <input
            type="datetime-local"
            value={customEnd}
            onChange={(e) => setCustomEnd(e.target.value)}
            className="text-xs px-2 py-1 rounded"
            style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32 }}
          />
          {(customStart || customEnd) && (
            <button
              onClick={() => { setCustomStart(''); setCustomEnd(''); }}
              className="text-xs btn-secondary px-2 py-1"
            >
              Clear
            </button>
          )}
          {(customStart || customEnd) && !(customStart && customEnd) && (
            <span className="text-xs" style={{ color: 'var(--warning)' }}>
              Both dates required for custom range
            </span>
          )}
        </div>
      </div>

      {/* Export buttons */}
      <ExportButtons
        hours={hours}
        customStart={customStart}
        customEnd={customEnd}
        selectedSensorIds={selectedSensorIds}
        data={{ stats, anomalies, history, regressions }}
      />

      {/* Sensor filter chips */}
      {allSensorOptions.length > 0 && (
        <div className="sensor-filter-panel p-3 rounded" style={{ background: 'var(--card-bg)', border: '1px solid var(--border)' }}>
          <p className="text-xs font-medium mb-2" style={{ color: 'var(--text)' }}>
            Sensor Filter
            {selectedSensorIds.length > 0 && (
              <button
                onClick={() => setSelectedSensorIds([])}
                className="ml-2 text-xs"
                style={{ color: 'var(--accent)' }}
              >
                Clear
              </button>
            )}
          </p>
          <div className="flex flex-wrap gap-1.5">
            {allSensorOptions.map((s) => {
              const active = selectedSensorIds.includes(s.sensor_id);
              return (
                <button
                  key={s.sensor_id}
                  onClick={() => toggleSensor(s.sensor_id)}
                  className="text-xs px-2 py-1 rounded transition-all"
                  style={{
                    minHeight: 28,
                    background: active ? 'var(--accent-muted)' : 'var(--bg)',
                    color: active ? 'var(--accent)' : 'var(--text-secondary)',
                    border: `1px solid ${active ? 'var(--accent)' : 'var(--border)'}`,
                  }}
                >
                  {s.sensor_name}
                </button>
              );
            })}
          </div>
          {selectedSensorIds.length === 0 && (
            <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>Showing all sensors. Click to filter.</p>
          )}
        </div>
      )}

      {/* Retention-limited banner */}
      {retentionLimited && (
        <div className="card p-3 text-sm" style={{ borderColor: 'var(--warning)', color: 'var(--warning)', background: 'rgba(234,179,8,0.08)' }}>
          Data is limited by the history retention window. Older data has been pruned.
        </div>
      )}

      {error && (
        <div className="card p-4 text-sm" style={{ color: 'var(--danger)', borderColor: 'var(--danger)', background: 'rgba(239,68,68,0.08)' }}>
          {error}
        </div>
      )}

      {/* Section 1: Stats cards */}
      <div>
        <h3 className="section-title mb-3">Sensor Statistics</h3>
        {stats !== null && stats.length === 0 ? (
          <div className="card p-6 text-center"><p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No data in this time window.</p></div>
        ) : (
          <div className="space-y-4">
            {/* Cooling Score gauge */}
            {stats && anomalies && regressions && (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                <CoolingScore stats={stats} anomalies={anomalies} regressions={regressions} />
              </div>
            )}
            {/* Period Comparison — always shown, fetches its own 24h vs prev 24h data */}
            <PeriodComparison fmt={fmt} />
            {/* Noise Optimization Advisor */}
            <div>
              <h3 className="section-title mb-3">Noise Optimization Advisor</h3>
              <NoiseAdvisor />
            </div>
            {thermalStats.length > 0 && (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                {thermalStats.map((s) => <StatCard key={s.sensor_id} s={s} accentColor="var(--warning)" fmt={fmt} />)}
              </div>
            )}
            {driveStats.length > 0 && (
              <>
                <p className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>Storage</p>
                <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                  {driveStats.map((s) => <StatCard key={s.sensor_id} s={s} accentColor="var(--success)" fmt={fmt} />)}
                </div>
              </>
            )}
            {fanStats.length > 0 && (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                {fanStats.map((s) => <StatCard key={s.sensor_id} s={s} accentColor="var(--accent)" fmt={fmt} />)}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Section 2: Thermal Health (Regressions) */}
      <div>
        <h3 className="section-title mb-3">Thermal Health</h3>
        <div className="card overflow-hidden">
          {!regressions || regressions.length === 0 ? (
            <div className="p-6 text-center">
              <p className="text-sm" style={{ color: 'var(--success)' }}>✓ All sensors within normal range.</p>
            </div>
          ) : (
            <div className="space-y-2 p-4">
              {regressions.map((r) => (
                <div
                  key={r.sensor_id}
                  className="rounded-lg px-4 py-3"
                  style={{
                    background: r.severity === 'critical' ? 'rgba(239,68,68,0.08)' : 'rgba(234,179,8,0.08)',
                    borderLeft: `3px solid ${r.severity === 'critical' ? 'var(--danger)' : 'var(--warning)'}`,
                  }}
                >
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-sm font-semibold" style={{ color: r.severity === 'critical' ? 'var(--danger)' : 'var(--warning)' }}>
                        {r.sensor_name}
                        <span className="ml-2 text-xs px-1.5 py-0.5 rounded" style={{
                          background: r.severity === 'critical' ? 'rgba(239,68,68,0.15)' : 'rgba(234,179,8,0.15)',
                        }}>
                          {r.severity}
                        </span>
                      </p>
                      <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>{r.message}</p>
                    </div>
                    <div className="text-right">
                      <p className="text-lg font-mono font-bold" style={{ color: r.severity === 'critical' ? 'var(--danger)' : 'var(--warning)' }}>
                        +{fmtDelta(r.delta, '°C')}
                      </p>
                      <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                        {fmt(r.baseline_avg, '°C')} → {fmt(r.recent_avg, '°C')}
                      </p>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Section 3: Anomalies */}
      <div>
        <h3 className="section-title mb-3">Anomalies (z-score &gt; 3)</h3>
        <div className="card overflow-hidden">
          {!sortedAnomalies || sortedAnomalies.length === 0 ? (
            <div className="p-6 text-center">
              <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No anomalies detected in this window.</p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                <thead>
                  <tr style={{ borderBottom: '1px solid var(--border)' }}>
                    {['Time', 'Sensor', 'Value', 'Z-Score', 'Severity', 'Mean ± StDev'].map((h) => (
                      <th key={h} style={{ padding: '8px 14px', textAlign: 'left', color: 'var(--text-secondary)', fontWeight: 600, whiteSpace: 'nowrap' }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {sortedAnomalies.map((a, i) => (
                    <tr key={`${a.sensor_id}-${a.timestamp_utc}`} style={{ borderBottom: '1px solid var(--border)', background: i % 2 === 0 ? 'transparent' : 'var(--surface-200)' }}>
                      <td style={{ padding: '8px 14px', color: 'var(--text-secondary)', whiteSpace: 'nowrap' }}>{new Date(a.timestamp_utc).toLocaleString()}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--text)', fontWeight: 500 }}>{a.sensor_name}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--text)', fontFamily: 'monospace' }}>{fmt(a.value, a.unit)}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--danger)', fontFamily: 'monospace', fontWeight: 600 }}>{a.z_score.toFixed(1)}</td>
                      <td style={{ padding: '8px 14px' }}>
                        <span className={`badge ${a.severity === 'critical' ? 'badge-danger' : 'badge-warning'}`}>
                          {a.severity ?? 'warning'}
                        </span>
                      </td>
                      <td style={{ padding: '8px 14px', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>{fmt(a.mean, a.unit)} ± {fmtDelta(a.stdev, a.unit)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {/* Section 4: Temperature History — interactive trend charts */}
      <div className="print-page-break">
        <div className="flex items-center justify-between mb-3">
          <h3 className="section-title" style={{ marginBottom: 0 }}>Temperature History</h3>
          {canWrite && (
            <button
              onClick={() => setShowAnnotationForm(!showAnnotationForm)}
              className="btn-secondary text-xs"
              style={{ minHeight: 28 }}
            >
              {showAnnotationForm ? 'Cancel' : '+ Add Annotation'}
            </button>
          )}
        </div>
        {showAnnotationForm && (
          <div className="annotation-form card p-4 mb-4 space-y-3 animate-fade-in" style={{ borderColor: 'var(--warning)' }}>
            <p className="text-xs font-semibold" style={{ color: 'var(--warning)' }}>New Annotation</p>
            <div className="flex items-center gap-3 flex-wrap">
              <input
                type="datetime-local"
                value={annTimestamp}
                onChange={(e) => setAnnTimestamp(e.target.value)}
                className="text-xs px-2 py-1 rounded"
                style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32 }}
                placeholder="Timestamp"
              />
              <input
                type="text"
                value={annLabel}
                onChange={(e) => setAnnLabel(e.target.value)}
                placeholder="Label (e.g. 'Repasted CPU')"
                className="text-xs px-2 py-1 rounded"
                style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32, minWidth: 200, flex: 1 }}
                maxLength={200}
              />
            </div>
            <input
              type="text"
              value={annDescription}
              onChange={(e) => setAnnDescription(e.target.value)}
              placeholder="Description (optional)"
              className="text-xs px-2 py-1 rounded"
              style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32, width: '100%' }}
            />
            <button
              onClick={handleCreateAnnotation}
              disabled={!annTimestamp || !annLabel.trim()}
              className="btn-primary text-xs"
              style={{ minHeight: 28 }}
            >
              Save Annotation
            </button>
          </div>
        )}
        {Object.keys(tempBuckets).length === 0 ? (
          <div className="card p-6 text-center"><p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No history data.</p></div>
        ) : (
          <div className="space-y-4">
            {Object.entries(tempBuckets).map(([sensorId, bkts]) => (
              <div key={sensorId} className="card p-4 animate-card-enter">
                <p className="text-xs font-semibold mb-3" style={{ color: 'var(--text-secondary)' }}>{bkts[0].sensor_name}</p>
                <TrendChart
                  buckets={bkts}
                  sensorId={sensorId}
                  sensorName={bkts[0].sensor_name}
                  unit={bkts[0].unit}
                  fmt={fmt}
                  annotations={annotations}
                  onDeleteAnnotation={canWrite ? handleDeleteAnnotation : undefined}
                />
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Section 5: Heatmap */}
      {history && history.length > 0 && (
        <Heatmap buckets={history} fmt={fmt} />
      )}

      {/* Section 6: Correlation */}
      {allSensorOptions.length >= 2 && (
        <div>
          <h3 className="section-title mb-3">Sensor Correlation</h3>
          <div className="card p-4">
            <p className="text-xs mb-3" style={{ color: 'var(--text-secondary)' }}>
              Measure the Pearson correlation between two sensors over the selected time window.
            </p>
            <div className="flex items-center gap-3 flex-wrap mb-3">
              <select
                value={corrX}
                onChange={(e) => { setCorrX(e.target.value); setCorrResult(null); }}
                className="text-xs px-2 py-1 rounded"
                style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32, minWidth: 140 }}
              >
                <option value="">Sensor X</option>
                {allSensorOptions.map((s) => (
                  <option key={s.sensor_id} value={s.sensor_id}>{s.sensor_name}</option>
                ))}
              </select>
              <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>vs</span>
              <select
                value={corrY}
                onChange={(e) => { setCorrY(e.target.value); setCorrResult(null); }}
                className="text-xs px-2 py-1 rounded"
                style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)', minHeight: 32, minWidth: 140 }}
              >
                <option value="">Sensor Y</option>
                {allSensorOptions.map((s) => (
                  <option key={s.sensor_id} value={s.sensor_id}>{s.sensor_name}</option>
                ))}
              </select>
              <button
                onClick={handleCorrelate}
                disabled={!corrX || !corrY || corrX === corrY || corrLoading}
                className="btn-primary text-xs"
                style={{ minHeight: 32 }}
              >
                {corrLoading ? 'Calculating...' : 'Correlate'}
              </button>
            </div>
            {corrResult && (
              <div className="mt-3 flex gap-6 flex-wrap items-start">
                {Number.isNaN(corrResult.coeff) ? (
                  <p className="text-sm" style={{ color: 'var(--danger)' }}>Correlation request failed.</p>
                ) : (<>
                <div>
                  <p className="text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>Pearson r</p>
                  <p className="text-3xl font-mono font-bold" style={{ color: coeffColor }}>
                    {corrResult.coeff.toFixed(3)}
                  </p>
                  <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
                    {Math.abs(corrResult.coeff) >= 0.7 ? 'Strong'
                    : Math.abs(corrResult.coeff) >= 0.4 ? 'Moderate'
                    : 'Weak'}
                    {corrResult.coeff >= 0 ? ' positive' : ' negative'} correlation
                  </p>
                  <p className="text-xs mt-0.5" style={{ color: 'var(--text-secondary)' }}>
                    {corrResult.count} paired samples
                  </p>
                </div>
                <div style={{ flex: 1, minWidth: 200 }}>
                  <p className="text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>Scatter plot</p>
                  <CorrelationScatter samples={corrResult.samples} labelX={corrXName} labelY={corrYName} />
                </div>
                </>)}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
