'use client';

import { useState, useEffect } from 'react';
import { api } from '@/lib/api';
import type { AnalyticsStat, AnalyticsAnomaly } from '@/lib/types';

type FmtFn = (v: number, unit: string) => string;

export interface PeriodComparisonProps {
  fmt: FmtFn;
}

interface PeriodData {
  stats: AnalyticsStat[];
  anomalyCount: number;
}

interface DeltaCard {
  label: string;
  currentVal: number | null;
  prevVal: number | null;
  unit: string;
  /** True when higher is better (e.g., lower temp/anomalies = improvement) */
  lowerIsBetter: boolean;
}

function buildDeltaCards(
  curr: PeriodData,
  prev: PeriodData,
): DeltaCard[] {
  // Average temp across all temp sensors
  const avgTemp = (stats: AnalyticsStat[]): { val: number; unit: string } | null => {
    const tempStats = stats.filter((s) => s.sensor_type.includes('temp'));
    if (tempStats.length === 0) return null;
    const avg = tempStats.reduce((sum, s) => sum + s.avg_value, 0) / tempStats.length;
    return { val: avg, unit: tempStats[0].unit };
  };

  const avgFan = (stats: AnalyticsStat[]): { val: number; unit: string } | null => {
    const fanStats = stats.filter((s) => s.sensor_type === 'fan_rpm');
    if (fanStats.length === 0) return null;
    const avg = fanStats.reduce((sum, s) => sum + s.avg_value, 0) / fanStats.length;
    return { val: avg, unit: fanStats[0].unit };
  };

  const currTemp = avgTemp(curr.stats);
  const prevTemp = avgTemp(prev.stats);
  const currFan  = avgFan(curr.stats);
  const prevFan  = avgFan(prev.stats);

  return [
    {
      label: 'Avg Temperature',
      currentVal: currTemp?.val ?? null,
      prevVal: prevTemp?.val ?? null,
      unit: currTemp?.unit ?? prevTemp?.unit ?? '°C',
      lowerIsBetter: true,
    },
    {
      label: 'Avg Fan Speed',
      currentVal: currFan?.val ?? null,
      prevVal: prevFan?.val ?? null,
      unit: currFan?.unit ?? prevFan?.unit ?? 'RPM',
      lowerIsBetter: false,
    },
    {
      label: 'Anomaly Count',
      currentVal: curr.anomalyCount,
      prevVal: prev.anomalyCount,
      unit: 'anomalies',
      lowerIsBetter: true,
    },
  ];
}

function Arrow({ direction }: { direction: 'up' | 'down' | 'flat' }) {
  if (direction === 'up')   return <span aria-label="increase">↑</span>;
  if (direction === 'down') return <span aria-label="decrease">↓</span>;
  return <span aria-label="no change">→</span>;
}

function DeltaCardView({ card, fmt }: { card: DeltaCard; fmt: FmtFn }) {
  const noBaseline = card.prevVal === null;
  const noData     = card.currentVal === null;

  let delta: number | null = null;
  let direction: 'up' | 'down' | 'flat' = 'flat';
  let color = 'var(--text-secondary)';
  let borderColor = 'var(--border)';

  if (!noBaseline && !noData && card.prevVal !== null && card.currentVal !== null) {
    delta = card.currentVal - card.prevVal;
    const EPS = 0.05;
    if (Math.abs(delta) < EPS) {
      direction = 'flat';
      color = 'var(--text-secondary)';
      borderColor = 'var(--border)';
    } else if (delta > 0) {
      direction = 'up';
      color = card.lowerIsBetter ? 'var(--danger)' : 'var(--success)';
      borderColor = color;
    } else {
      direction = 'down';
      color = card.lowerIsBetter ? 'var(--success)' : 'var(--danger)';
      borderColor = color;
    }
  }

  const formatDelta = () => {
    if (delta === null) return '—';
    const sign = delta > 0 ? '+' : '';
    if (card.unit === 'anomalies') {
      return `${sign}${Math.round(delta)} ${card.unit}`;
    }
    return `${sign}${fmt(Math.abs(delta), card.unit).replace(/[^0-9.]/g, '')} ${card.unit}`.trim();
  };

  // Actually use fmt properly for temperature deltas
  const formatDeltaFmt = () => {
    if (delta === null) return '—';
    if (card.unit === 'anomalies') {
      const sign = delta > 0 ? '+' : '';
      return `${sign}${Math.round(delta)}`;
    }
    const absDelta = Math.abs(delta);
    // Build display: sign + formatted absolute value
    const sign = delta > 0 ? '+' : delta < 0 ? '−' : '';
    // For temp units we want the converted value
    const displayed = fmt(absDelta, card.unit);
    return `${sign}${displayed}`;
  };

  return (
    <div
      className="card p-4 animate-card-enter"
      style={{ borderLeft: `3px solid ${borderColor}` }}
    >
      <p className="text-xs font-semibold mb-2" style={{ color: 'var(--text-secondary)' }}>
        {card.label}
      </p>

      {noData ? (
        <p className="text-lg font-mono font-bold" style={{ color: 'var(--text-secondary)' }}>—</p>
      ) : (
        <p className="text-2xl font-mono font-bold" style={{ color }}>
          {delta !== null && <Arrow direction={direction} />}
          {' '}
          {formatDeltaFmt()}
        </p>
      )}

      <div className="mt-2" style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
        {noBaseline ? (
          <span>No baseline (previous period)</span>
        ) : noData ? (
          <span>No data for current period</span>
        ) : (
          <>
            <span>
              Now: {card.currentVal !== null
                ? card.unit === 'anomalies'
                  ? `${Math.round(card.currentVal)} ${card.unit}`
                  : fmt(card.currentVal, card.unit)
                : '—'
              }
            </span>
            <span style={{ margin: '0 4px' }}>·</span>
            <span>
              Prev: {card.prevVal !== null
                ? card.unit === 'anomalies'
                  ? `${Math.round(card.prevVal)} ${card.unit}`
                  : fmt(card.prevVal, card.unit)
                : '—'
              }
            </span>
          </>
        )}
      </div>
      <p style={{ fontSize: 10, color: 'var(--text-secondary)', marginTop: 4 }}>
        Last 24h vs previous 24h
      </p>
    </div>
  );
}

export function PeriodComparison({ fmt }: PeriodComparisonProps) {
  const [cards, setCards]     = useState<DeltaCard[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const now      = new Date();
    const h24Ago   = new Date(now.getTime() - 24 * 60 * 60 * 1000);
    const h48Ago   = new Date(now.getTime() - 48 * 60 * 60 * 1000);
    const prevOpts = { start: h48Ago.toISOString(), end: h24Ago.toISOString() };

    Promise.all([
      api.analytics.getStats(24),
      api.analytics.getStats(24, undefined, prevOpts),
      api.analytics.getAnomalies(24),
      api.analytics.getAnomalies(24, 3.0, prevOpts),
    ])
      .then(([currStats, prevStats, currAnom, prevAnom]) => {
        if (cancelled) return;
        const curr: PeriodData = { stats: currStats.stats, anomalyCount: currAnom.anomalies.length };
        const prev: PeriodData = { stats: prevStats.stats, anomalyCount: prevAnom.anomalies.length };
        setCards(buildDeltaCards(curr, prev));
      })
      .catch(() => {
        if (!cancelled) setError('Failed to load comparison data.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => { cancelled = true; };
  }, []);

  return (
    <div>
      <h3 className="section-title mb-3">Period Comparison</h3>
      {loading && (
        <div className="card p-4 text-sm" style={{ color: 'var(--text-secondary)' }}>
          Loading comparison…
        </div>
      )}
      {error && (
        <div className="card p-3 text-sm" style={{ color: 'var(--danger)', borderColor: 'var(--danger)', background: 'rgba(239,68,68,0.08)' }}>
          {error}
        </div>
      )}
      {cards && !loading && (
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {cards.map((card) => (
            <DeltaCardView key={card.label} card={card} fmt={fmt} />
          ))}
        </div>
      )}
    </div>
  );
}
