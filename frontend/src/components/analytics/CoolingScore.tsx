'use client';

import type { AnalyticsStat, AnalyticsAnomaly, ThermalRegression } from '@/lib/types';

interface CoolingScoreProps {
  stats: AnalyticsStat[];
  anomalies: AnalyticsAnomaly[];
  regressions: ThermalRegression[];
}

function computeScore(stats: AnalyticsStat[], anomalies: AnalyticsAnomaly[], regressions: ThermalRegression[]): number {
  let score = 100;

  // -5 per anomaly (max -30)
  const anomalyPenalty = Math.min(anomalies.length * 5, 30);
  score -= anomalyPenalty;

  // -15 per critical regression, -8 per warning
  for (const r of regressions) {
    score -= r.severity === 'critical' ? 15 : 8;
  }

  // -2 per sensor with p95 > 85 C
  for (const s of stats) {
    if (s.sensor_type.includes('temp') && s.p95_value != null && s.p95_value > 85) {
      score -= 2;
    }
  }

  return Math.max(0, Math.min(100, score));
}

function scoreColor(score: number): string {
  if (score >= 80) return 'var(--success)';
  if (score >= 50) return 'var(--warning)';
  return 'var(--danger)';
}

function scoreLabel(score: number): string {
  if (score >= 90) return 'Excellent';
  if (score >= 80) return 'Good';
  if (score >= 60) return 'Fair';
  if (score >= 50) return 'Needs attention';
  return 'Poor';
}

export function CoolingScore({ stats, anomalies, regressions }: CoolingScoreProps) {
  const score = computeScore(stats, anomalies, regressions);
  const color = scoreColor(score);
  const label = scoreLabel(score);

  // Arc parameters: 0-270 degrees
  const SIZE = 120;
  const CX = SIZE / 2;
  const CY = SIZE / 2;
  const R = 46;
  const STROKE = 8;
  const START_ANGLE = 135; // degrees, from bottom-left
  const TOTAL_ARC = 270;

  const toRad = (deg: number) => (deg * Math.PI) / 180;

  // Background arc (full 270 degrees)
  const bgStart = toRad(START_ANGLE);
  const bgEnd = toRad(START_ANGLE + TOTAL_ARC);
  const bgX1 = CX + R * Math.cos(bgStart);
  const bgY1 = CY + R * Math.sin(bgStart);
  const bgX2 = CX + R * Math.cos(bgEnd);
  const bgY2 = CY + R * Math.sin(bgEnd);
  const bgLargeArc = TOTAL_ARC > 180 ? 1 : 0;
  const bgPath = `M ${bgX1} ${bgY1} A ${R} ${R} 0 ${bgLargeArc} 1 ${bgX2} ${bgY2}`;

  // Value arc
  const valueAngle = (score / 100) * TOTAL_ARC;
  const valEnd = toRad(START_ANGLE + valueAngle);
  const valX2 = CX + R * Math.cos(valEnd);
  const valY2 = CY + R * Math.sin(valEnd);
  const valLargeArc = valueAngle > 180 ? 1 : 0;
  const valPath = valueAngle > 0.5
    ? `M ${bgX1} ${bgY1} A ${R} ${R} 0 ${valLargeArc} 1 ${valX2} ${valY2}`
    : '';

  return (
    <div className="card p-4 animate-card-enter" style={{ borderLeft: `3px solid ${color}` }}>
      <p className="text-xs font-semibold mb-2" style={{ color }}>Cooling Score</p>
      <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
        <svg viewBox={`0 0 ${SIZE} ${SIZE}`} width={SIZE} height={SIZE} style={{ flexShrink: 0 }}>
          {/* Background track */}
          <path
            d={bgPath}
            fill="none"
            stroke="var(--border)"
            strokeWidth={STROKE}
            strokeLinecap="round"
          />
          {/* Value arc */}
          {valPath && (
            <path
              d={valPath}
              fill="none"
              stroke={color}
              strokeWidth={STROKE}
              strokeLinecap="round"
            />
          )}
          {/* Score text */}
          <text
            x={CX}
            y={CY - 4}
            textAnchor="middle"
            dominantBaseline="central"
            fontSize="28"
            fontWeight="bold"
            fontFamily="monospace"
            fill={color}
          >
            {score}
          </text>
          <text
            x={CX}
            y={CY + 18}
            textAnchor="middle"
            fontSize="10"
            fill="var(--text-secondary)"
          >
            {label}
          </text>
        </svg>
        <div className="text-xs" style={{ color: 'var(--text-secondary)' }}>
          <p>{anomalies.length} anomal{anomalies.length === 1 ? 'y' : 'ies'}</p>
          <p>{regressions.length} regression{regressions.length !== 1 ? 's' : ''}</p>
          <p>{stats.filter((s) => s.sensor_type.includes('temp') && s.p95_value != null && s.p95_value > 85).length} hot sensor{stats.filter((s) => s.sensor_type.includes('temp') && s.p95_value != null && s.p95_value > 85).length !== 1 ? 's' : ''}</p>
        </div>
      </div>
    </div>
  );
}
