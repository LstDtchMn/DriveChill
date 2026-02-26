'use client';

import { useMemo } from 'react';
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend,
} from 'recharts';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { displayTemp, tempUnitSymbol } from '@/lib/tempUnit';

const CHART_COLORS = [
  '#3b82f6', // blue
  '#ef4444', // red
  '#22c55e', // green
  '#f59e0b', // amber
  '#8b5cf6', // violet
  '#ec4899', // pink
  '#06b6d4', // cyan
  '#f97316', // orange
];

const TEMP_TYPES = new Set(['cpu_temp', 'gpu_temp', 'hdd_temp', 'case_temp']);

export function TempChart() {
  const history = useAppStore((s) => s.history);
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const sensorLabels = useSettingsStore((s) => s.sensorLabels);

  const { chartData, sensorKeys } = useMemo(() => {
    // Get unique temp sensor IDs
    const sensorMap = new Map<string, string>(); // id -> name

    for (const point of history) {
      for (const r of point.readings) {
        if (TEMP_TYPES.has(r.sensor_type) && !sensorMap.has(r.id)) {
          sensorMap.set(r.id, sensorLabels[r.id] || r.name);
        }
      }
    }

    const keys = Array.from(sensorMap.entries());

    // Build chart data (sample every 3rd point to keep it smooth)
    const step = Math.max(1, Math.floor(history.length / 100));
    const data: Record<string, any>[] = [];

    for (let i = 0; i < history.length; i += step) {
      const point = history[i];
      const row: Record<string, any> = {
        time: new Date(point.timestamp).toLocaleTimeString([], { minute: '2-digit', second: '2-digit' }),
      };

      for (const r of point.readings) {
        if (TEMP_TYPES.has(r.sensor_type)) {
          row[r.id] = displayTemp(r.value, tempUnit);
        }
      }

      data.push(row);
    }

    return { chartData: data, sensorKeys: keys };
  }, [history, tempUnit, sensorLabels]);

  if (chartData.length < 2) {
    return (
      <div className="card p-6 flex items-center justify-center h-64">
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
          Collecting data... chart will appear shortly.
        </p>
      </div>
    );
  }

  return (
    <div className="card p-4 animate-card-enter">
      <h3 className="section-title mb-4">Temperature History</h3>
      <ResponsiveContainer width="100%" height={280}>
        <LineChart data={chartData} margin={{ top: 5, right: 10, left: 0, bottom: 5 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="var(--surface-200)" />
          <XAxis
            dataKey="time"
            stroke="var(--text-secondary)"
            tick={{ fontSize: 11 }}
            interval="preserveStartEnd"
          />
          <YAxis
            stroke="var(--text-secondary)"
            tick={{ fontSize: 11 }}
            domain={['dataMin - 5', 'dataMax + 5']}
            unit={tempUnitSymbol(tempUnit)}
          />
          <Tooltip
            contentStyle={{
              background: 'var(--card-bg)',
              border: '1px solid var(--border)',
              borderRadius: '8px',
              fontSize: '12px',
              boxShadow: 'var(--card-shadow)',
            }}
            labelStyle={{ color: 'var(--text)' }}
          />
          <Legend
            wrapperStyle={{ fontSize: '12px', color: 'var(--text-secondary)' }}
          />
          {sensorKeys.map(([id, name], i) => (
            <Line
              key={id}
              type="monotone"
              dataKey={id}
              name={name}
              stroke={CHART_COLORS[i % CHART_COLORS.length]}
              strokeWidth={2}
              dot={false}
              activeDot={{ r: 4 }}
            />
          ))}
        </LineChart>
      </ResponsiveContainer>
    </div>
  );
}
