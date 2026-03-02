'use client';

import { useState, useEffect } from 'react';
import { api } from '@/lib/api';
import type { AnalyticsStat, AnalyticsAnomaly, AnalyticsBucket } from '@/lib/types';

const TIME_OPTIONS = [
  { label: '1h', hours: 1 },
  { label: '6h', hours: 6 },
  { label: '24h', hours: 24 },
  { label: '7d', hours: 168 },
];

function fmt(v: number, unit: string) { return `${v.toFixed(1)} ${unit}`; }

function Sparkline({ buckets }: { buckets: AnalyticsBucket[] }) {
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

function StatCard({ s, accentColor }: { s: AnalyticsStat; accentColor: string }) {
  return (
    <div className="card p-4 animate-card-enter" style={{ borderLeft: `3px solid ${accentColor}` }}>
      <p className="text-xs font-semibold mb-3 truncate" style={{ color: accentColor }}>{s.sensor_name}</p>
      <div className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-xs">
        {(['Min', 'Max', 'Avg', 'P95'] as const).map((lbl) => {
          const key = lbl === 'P95' ? 'p95_value' : (`${lbl.toLowerCase()}_value` as keyof AnalyticsStat);
          return [
            <span key={`${lbl}l`} style={{ color: 'var(--text-secondary)' }}>{lbl}</span>,
            <span key={`${lbl}v`} className="font-mono text-right" style={{ color: 'var(--text)' }}>{fmt(s[key] as number, s.unit)}</span>,
          ];
        })}
      </div>
      <p className="text-xs mt-3" style={{ color: 'var(--text-secondary)' }}>{s.sample_count.toLocaleString()} samples</p>
    </div>
  );
}

export function AnalyticsPage() {
  const [hours, setHours] = useState(24);
  const [stats, setStats] = useState<AnalyticsStat[] | null>(null);
  const [anomalies, setAnomalies] = useState<AnalyticsAnomaly[] | null>(null);
  const [history, setHistory] = useState<AnalyticsBucket[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true); setError(null);
    const bs = hours <= 1 ? 30 : hours <= 6 ? 60 : hours <= 24 ? 300 : 3600;
    Promise.all([api.analytics.getStats(hours), api.analytics.getAnomalies(hours), api.analytics.getHistory(hours, undefined, bs)])
      .then(([sR, aR, hR]) => { setStats(sR.stats); setAnomalies(aR.anomalies); setHistory(hR.buckets); })
      .catch(() => setError('Failed to load analytics data.'))
      .finally(() => setLoading(false));
  }, [hours]);

  const tempStats = stats?.filter((s) => s.sensor_type.includes('temp')) ?? [];
  const fanStats = stats?.filter((s) => s.sensor_type === 'fan_rpm') ?? [];
  const sortedAnomalies = anomalies ? [...anomalies].sort((a, b) => b.z_score - a.z_score) : null;

  const tempBuckets: Record<string, AnalyticsBucket[]> = {};
  if (history) {
    for (const b of history) {
      if (b.sensor_type.includes('temp')) {
        (tempBuckets[b.sensor_id] ??= []).push(b);
      }
    }
  }

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Time window picker */}
      <div className="flex items-center gap-2 flex-wrap">
        {TIME_OPTIONS.map((o) => (
          <button key={o.hours} onClick={() => setHours(o.hours)} className={hours === o.hours ? 'btn-primary' : 'btn-secondary'} style={{ minWidth: 48 }}>
            {o.label}
          </button>
        ))}
        {loading && <span className="text-xs ml-2" style={{ color: 'var(--text-secondary)' }}>Loading...</span>}
      </div>

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
            {tempStats.length > 0 && (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                {tempStats.map((s) => <StatCard key={s.sensor_id} s={s} accentColor="var(--warning)" />)}
              </div>
            )}
            {fanStats.length > 0 && (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
                {fanStats.map((s) => <StatCard key={s.sensor_id} s={s} accentColor="var(--accent)" />)}
              </div>
            )}
          </div>
        )}
      </div>

      {/* Section 2: Anomalies */}
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
                    {['Time', 'Sensor', 'Value', 'Z-Score', 'Mean \u00b1 StDev'].map((h) => (
                      <th key={h} style={{ padding: '8px 14px', textAlign: 'left', color: 'var(--text-secondary)', fontWeight: 600, whiteSpace: 'nowrap' }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {sortedAnomalies.map((a, i) => (
                    <tr key={i} style={{ borderBottom: '1px solid var(--border)', background: i % 2 === 0 ? 'transparent' : 'var(--surface-200)' }}>
                      <td style={{ padding: '8px 14px', color: 'var(--text-secondary)', whiteSpace: 'nowrap' }}>{new Date(a.timestamp_utc).toLocaleString()}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--text)', fontWeight: 500 }}>{a.sensor_name}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--text)', fontFamily: 'monospace' }}>{fmt(a.value, a.unit)}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--danger)', fontFamily: 'monospace', fontWeight: 600 }}>{a.z_score.toFixed(1)}</td>
                      <td style={{ padding: '8px 14px', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>{a.mean.toFixed(1)} &plusmn; {a.stdev.toFixed(1)} {a.unit}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {/* Section 3: Temperature History sparklines */}
      <div>
        <h3 className="section-title mb-3">Temperature History</h3>
        {Object.keys(tempBuckets).length === 0 ? (
          <div className="card p-6 text-center"><p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No history data.</p></div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            {Object.entries(tempBuckets).map(([sensorId, bkts]) => (
              <div key={sensorId} className="card p-4 animate-card-enter">
                <p className="text-xs font-semibold mb-3" style={{ color: 'var(--text-secondary)' }}>{bkts[0].sensor_name}</p>
                <Sparkline buckets={bkts} />
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
