'use client';

import { Fan } from 'lucide-react';

interface FanSpeedCardProps {
  name: string;
  rpm: number;
  percentage: number;
  maxRpm?: number;
}

export function FanSpeedCard({ name, rpm, percentage, maxRpm = 2000 }: FanSpeedCardProps) {
  const barWidth = Math.min(100, Math.max(0, percentage));

  return (
    <div className="card p-4 animate-card-enter">
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          <div
            className="w-8 h-8 rounded-lg flex items-center justify-center"
            style={{ background: 'var(--accent-muted)' }}
          >
            <Fan
              size={16}
              style={{
                color: 'var(--accent)',
                animation: percentage > 0 ? `spin ${Math.max(0.3, 2 - percentage / 60)}s linear infinite` : 'none',
              }}
            />
          </div>
          <div>
            <p className="text-sm font-medium" style={{ color: 'var(--text)' }}>{name}</p>
            <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              {Math.round(rpm)} RPM
            </p>
          </div>
        </div>

        <span
          className="text-xl font-mono font-bold"
          style={{ color: 'var(--accent)' }}
        >
          {Math.round(percentage)}%
        </span>
      </div>

      {/* Speed bar */}
      <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--surface-200)' }}>
        <div
          className="h-full rounded-full transition-all duration-500 ease-out"
          style={{
            width: `${barWidth}%`,
            background: `linear-gradient(90deg, var(--accent), var(--accent-light))`,
            boxShadow: `0 0 8px var(--accent-muted)`,
          }}
        />
      </div>
    </div>
  );
}
