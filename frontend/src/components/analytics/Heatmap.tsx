'use client';

import type { AnalyticsBucket } from '@/lib/types';

interface HeatmapProps {
  buckets: AnalyticsBucket[];
  fmt: (v: number, unit: string) => string;
}

function tempColor(ratio: number): string {
  // blue (cool) -> yellow -> red (hot)
  if (ratio <= 0.5) {
    const t = ratio * 2;
    const r = Math.round(30 + t * 225);
    const g = Math.round(100 + t * 155);
    const b = Math.round(220 - t * 220);
    return `rgb(${r},${g},${b})`;
  }
  const t = (ratio - 0.5) * 2;
  const r = Math.round(255);
  const g = Math.round(255 - t * 200);
  const b = Math.round(0);
  return `rgb(${r},${g},${b})`;
}

export function Heatmap({ buckets, fmt }: HeatmapProps) {
  // Filter to temperature sensors only
  const tempBuckets = buckets.filter((b) => b.sensor_type.includes('temp'));
  if (tempBuckets.length === 0) return null;

  // Group by sensor, then by hour-of-day
  const sensorHourMap: Record<string, { name: string; unit: string; hours: Record<number, { sum: number; count: number }> }> = {};

  for (const b of tempBuckets) {
    if (!sensorHourMap[b.sensor_id]) {
      sensorHourMap[b.sensor_id] = { name: b.sensor_name, unit: b.unit, hours: {} };
    }
    const hour = new Date(b.timestamp_utc).getUTCHours();
    const entry = sensorHourMap[b.sensor_id].hours;
    if (!entry[hour]) entry[hour] = { sum: 0, count: 0 };
    entry[hour].sum += b.avg_value;
    entry[hour].count += 1;
  }

  const sensors = Object.entries(sensorHourMap);
  if (sensors.length === 0) return null;

  // Find global min/max for color scaling
  let globalMin = Infinity;
  let globalMax = -Infinity;
  for (const [, s] of sensors) {
    for (const h of Object.values(s.hours)) {
      const avg = h.sum / h.count;
      if (avg < globalMin) globalMin = avg;
      if (avg > globalMax) globalMax = avg;
    }
  }
  const range = globalMax - globalMin || 1;

  const CELL = 20;
  const LABEL_W = 100;
  const HEADER_H = 24;
  const W = LABEL_W + 24 * CELL + 4;
  const H = HEADER_H + sensors.length * CELL + 4;

  return (
    <div>
      <h3 className="section-title mb-3">Temperature Heatmap</h3>
      <div className="card p-4 overflow-x-auto">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          width="100%"
          style={{ maxWidth: W, display: 'block', minWidth: 400 }}
        >
          {/* Hour labels */}
          {Array.from({ length: 24 }, (_, i) => (
            <text
              key={`h${i}`}
              x={LABEL_W + i * CELL + CELL / 2}
              y={HEADER_H - 6}
              textAnchor="middle"
              fontSize="9"
              fill="var(--text-secondary)"
            >
              {i}
            </text>
          ))}

          {/* Rows */}
          {sensors.map(([sensorId, s], row) => (
            <g key={sensorId}>
              {/* Sensor name */}
              <text
                x={LABEL_W - 6}
                y={HEADER_H + row * CELL + CELL / 2 + 3}
                textAnchor="end"
                fontSize="9"
                fill="var(--text-secondary)"
              >
                {s.name.length > 14 ? s.name.slice(0, 13) + '\u2026' : s.name}
              </text>

              {/* Cells */}
              {Array.from({ length: 24 }, (_, hour) => {
                const entry = s.hours[hour];
                const hasData = !!entry;
                const avg = hasData ? entry.sum / entry.count : 0;
                const ratio = hasData ? (avg - globalMin) / range : 0;
                return (
                  <rect
                    key={hour}
                    x={LABEL_W + hour * CELL}
                    y={HEADER_H + row * CELL}
                    width={CELL - 1}
                    height={CELL - 1}
                    rx={2}
                    fill={hasData ? tempColor(ratio) : 'var(--border)'}
                    opacity={hasData ? 0.85 : 0.3}
                  >
                    <title>
                      {hasData
                        ? `${s.name} @ ${hour}:00 UTC\n${fmt(avg, s.unit)}`
                        : `${s.name} @ ${hour}:00 UTC\nNo data`}
                    </title>
                  </rect>
                );
              })}
            </g>
          ))}
        </svg>
        <p className="text-xs mt-2" style={{ color: 'var(--text-secondary)' }}>
          Hours are in UTC. Color: blue (cool) to red (hot).
        </p>
      </div>
    </div>
  );
}
