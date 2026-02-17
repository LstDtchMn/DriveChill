'use client';

import { Cpu, Monitor, HardDrive, Thermometer } from 'lucide-react';
import { TempGauge } from './TempGauge';
import { FanSpeedCard } from './FanSpeedCard';
import { TempChart } from './TempChart';
import { useSensors } from '@/hooks/useSensors';

export function SystemOverview() {
  const {
    cpuTemp, gpuTemp, hddTemp, caseTemp,
    cpuLoad, gpuLoad,
    fanRpms, fanPcts,
  } = useSensors();

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Temperature Gauges */}
      <div>
        <h3 className="section-title mb-3 px-1">Temperatures</h3>
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <TempGauge
            label="CPU"
            value={cpuTemp}
            maxValue={100}
            icon={<Cpu size={16} />}
          />
          <TempGauge
            label="GPU"
            value={gpuTemp}
            maxValue={95}
            icon={<Monitor size={16} />}
          />
          <TempGauge
            label="Storage"
            value={hddTemp}
            maxValue={70}
            icon={<HardDrive size={16} />}
          />
          <TempGauge
            label="Case"
            value={caseTemp}
            maxValue={55}
            icon={<Thermometer size={16} />}
          />
        </div>
      </div>

      {/* Load indicators */}
      <div>
        <h3 className="section-title mb-3 px-1">System Load</h3>
        <div className="grid grid-cols-2 gap-4">
          <div className="card p-4 animate-card-enter">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>CPU Load</span>
              <span className="text-lg font-mono font-bold" style={{ color: 'var(--accent)' }}>
                {Math.round(cpuLoad)}%
              </span>
            </div>
            <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--surface-200)' }}>
              <div
                className="h-full rounded-full transition-all duration-500"
                style={{
                  width: `${Math.min(100, cpuLoad)}%`,
                  background: cpuLoad > 80 ? 'var(--danger)' : cpuLoad > 50 ? 'var(--warning)' : 'var(--success)',
                }}
              />
            </div>
          </div>
          <div className="card p-4 animate-card-enter">
            <div className="flex items-center justify-between mb-2">
              <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>GPU Load</span>
              <span className="text-lg font-mono font-bold" style={{ color: 'var(--accent)' }}>
                {Math.round(gpuLoad)}%
              </span>
            </div>
            <div className="w-full h-2 rounded-full overflow-hidden" style={{ background: 'var(--surface-200)' }}>
              <div
                className="h-full rounded-full transition-all duration-500"
                style={{
                  width: `${Math.min(100, gpuLoad)}%`,
                  background: gpuLoad > 80 ? 'var(--danger)' : gpuLoad > 50 ? 'var(--warning)' : 'var(--success)',
                }}
              />
            </div>
          </div>
        </div>
      </div>

      {/* Fan Speeds */}
      <div>
        <h3 className="section-title mb-3 px-1">Fan Speeds</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {fanRpms.map((fan, i) => {
            const pct = fanPcts.find((f) => f.id.replace('_rpm', '') === fan.id.replace('_rpm', ''));
            return (
              <FanSpeedCard
                key={fan.id}
                name={fan.name.replace(' RPM', '').replace(' Rpm', '')}
                rpm={fan.value}
                percentage={pct?.value ?? 0}
                maxRpm={fan.max_value ?? 2000}
              />
            );
          })}
        </div>
      </div>

      {/* Temperature Chart */}
      <TempChart />
    </div>
  );
}
