'use client';

import { useMemo } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { displayTemp, tempUnitSymbol } from '@/lib/tempUnit';

interface TempGaugeProps {
  label: string;
  value: number;
  maxValue?: number;
  unit?: string;
  icon?: React.ReactNode;
  size?: 'sm' | 'md' | 'lg';
}

function getColor(value: number, max: number): string {
  const ratio = value / max;
  if (ratio < 0.5) return 'var(--success)';
  if (ratio < 0.75) return 'var(--warning)';
  return 'var(--danger)';
}

export function TempGauge({ label, value, maxValue = 100, unit: _unit, icon, size = 'md' }: TempGaugeProps) {
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const displayValue = displayTemp(value, tempUnit);
  const displayMax = displayTemp(maxValue, tempUnit);
  const unit = tempUnitSymbol(tempUnit);
  const dimensions = size === 'sm' ? 100 : size === 'md' ? 130 : 160;
  const strokeWidth = size === 'sm' ? 6 : 8;
  const radius = (dimensions - strokeWidth) / 2 - 4;
  const circumference = 2 * Math.PI * radius;

  const { offset, color } = useMemo(() => {
    // Use display-unit values so the arc and color thresholds respect the
    // user's °C/°F preference (e.g., 90°F stays in the "cool" zone, not danger).
    const clamped = Math.max(0, Math.min(displayValue, displayMax));
    const ratio = displayMax > 0 ? clamped / displayMax : 0;
    // Use 270 degrees of the circle (3/4)
    const arcLength = circumference * 0.75;
    const filled = arcLength * ratio;
    return {
      offset: arcLength - filled,
      color: getColor(displayValue, displayMax),
    };
  }, [displayValue, displayMax, circumference]);

  const arcLength = circumference * 0.75;

  return (
    <div className="card p-4 flex flex-col items-center animate-card-enter">
      <div className="relative" style={{ width: dimensions, height: dimensions }}>
        <svg
          width={dimensions}
          height={dimensions}
          className="transform -rotate-[135deg]"
        >
          {/* Background arc */}
          <circle
            cx={dimensions / 2}
            cy={dimensions / 2}
            r={radius}
            fill="none"
            stroke="var(--surface-200)"
            strokeWidth={strokeWidth}
            strokeDasharray={`${arcLength} ${circumference}`}
            strokeLinecap="round"
          />
          {/* Value arc */}
          <circle
            cx={dimensions / 2}
            cy={dimensions / 2}
            r={radius}
            fill="none"
            stroke={color}
            strokeWidth={strokeWidth}
            strokeDasharray={`${arcLength - offset} ${circumference}`}
            strokeLinecap="round"
            className="transition-all duration-500 ease-out"
            style={{ filter: `drop-shadow(0 0 6px ${color}40)` }}
          />
        </svg>

        {/* Center content */}
        <div className="absolute inset-0 flex flex-col items-center justify-center">
          {icon && <div className="mb-1 opacity-60">{icon}</div>}
          <span
            className="font-mono font-bold transition-colors duration-300"
            style={{
              fontSize: size === 'sm' ? '1.25rem' : size === 'md' ? '1.75rem' : '2.25rem',
              color,
            }}
          >
            {displayValue}
          </span>
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>{unit}</span>
        </div>
      </div>

      <span className="mt-2 text-sm font-medium" style={{ color: 'var(--text)' }}>{label}</span>
    </div>
  );
}
