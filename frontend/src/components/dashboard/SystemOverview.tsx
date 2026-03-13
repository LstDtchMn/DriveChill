'use client';

import { useEffect, useState, useCallback } from 'react';
import { Cpu, Monitor, HardDrive, Thermometer, AlertTriangle, Settings2, RotateCcw } from 'lucide-react';
import { useDragReorder } from '@/hooks/useDragReorder';
import { TempGauge } from './TempGauge';
import { FanSpeedCard } from './FanSpeedCard';
import { TempChart } from './TempChart';
import { MachineDrillIn } from './MachineDrillIn';
import { useSensors } from '@/hooks/useSensors';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { api } from '@/lib/api';
import { formatTemp } from '@/lib/tempUnit';
import {
  loadWidgetConfig,
  saveWidgetConfig,
  getWidgetLabel,
  DEFAULT_WIDGETS,
  type WidgetConfig,
  type WidgetType,
} from '@/lib/widgetConfig';
import type { DriveSummary, MachineInfo } from '@/lib/types';

// ─── Widget customize panel ────────────────────────────────────────────────

interface CustomizePanelProps {
  widgets: WidgetConfig[];
  onChange: (widgets: WidgetConfig[]) => void;
  onClose: () => void;
}

function CustomizePanel({ widgets, onChange, onClose }: CustomizePanelProps) {
  const sorted = [...widgets].sort((a, b) => a.order - b.order);

  const toggle = (type: WidgetType) => {
    onChange(widgets.map((w) => (w.type === type ? { ...w, visible: !w.visible } : w)));
  };

  const handleReorder = (newItems: WidgetConfig[]) => {
    const renumbered = newItems.map((w, i) => ({ ...w, order: i }));
    onChange(renumbered);
  };

  const { dragHandleProps, itemProps, dragIndex } = useDragReorder(sorted, handleReorder);

  const reset = () => onChange([...DEFAULT_WIDGETS]);

  return (
    <div
      className="card animate-fade-in"
      style={{ padding: '1rem', marginBottom: '1rem', border: '1px solid var(--accent)' }}
    >
      <div className="flex items-center justify-between mb-3">
        <span className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
          Customize Dashboard
        </span>
        <div className="flex items-center gap-2">
          <button
            className="btn-secondary"
            style={{ fontSize: 12, padding: '2px 8px', display: 'flex', alignItems: 'center', gap: 4 }}
            onClick={reset}
            title="Reset to default layout"
          >
            <RotateCcw size={12} />
            Reset
          </button>
          <button
            className="btn-secondary"
            style={{ fontSize: 12, padding: '2px 8px' }}
            onClick={onClose}
          >
            Done
          </button>
        </div>
      </div>

      {/* Screen-reader live region announces drag state */}
      <div aria-live="polite" aria-atomic="true" className="sr-only">
        {dragIndex !== null ? `Dragging widget ${dragIndex + 1} of ${sorted.length}. Use arrow keys to move, Enter to drop, Escape to cancel.` : ''}
      </div>

      <div data-drag-container aria-dropeffect="move" style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
        {sorted.map((widget, i) => (
          <div
            key={widget.type}
            {...itemProps(i)}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 8,
              padding: '6px 8px',
              borderRadius: 6,
              background: 'var(--surface-200)',
              ...itemProps(i).style,
            }}
          >
            {/* Drag handle */}
            <span
              {...dragHandleProps(i)}
              title="Drag to reorder"
              style={{
                fontSize: 16,
                lineHeight: 1,
                color: 'var(--text-secondary)',
                flexShrink: 0,
                userSelect: 'none',
                ...dragHandleProps(i).style,
              }}
            >
              ⠿
            </span>
            <input
              type="checkbox"
              id={`widget-toggle-${widget.type}`}
              checked={widget.visible}
              onChange={() => toggle(widget.type)}
              style={{ cursor: 'pointer', accentColor: 'var(--accent)', flexShrink: 0 }}
            />
            <label
              htmlFor={`widget-toggle-${widget.type}`}
              style={{
                flex: 1,
                fontSize: 13,
                cursor: 'pointer',
                color: widget.visible ? 'var(--text)' : 'var(--text-secondary)',
              }}
            >
              {getWidgetLabel(widget.type)}
            </label>
          </div>
        ))}
      </div>
    </div>
  );
}

// ─── Main component ────────────────────────────────────────────────────────

export function SystemOverview() {
  const {
    cpuTemp, gpuTemp, hddTemp, caseTemp,
    cpuLoad, gpuLoad,
    fanRpms, fanPcts,
    cpuTemps, gpuTemps, hddTemps, caseTemps,
  } = useSensors();
  const sensorLabels = useSettingsStore((s) => s.sensorLabels);
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const setPage = useAppStore((s) => s.setPage);
  const connected = useAppStore((s) => s.connected);
  const [machines, setMachines] = useState<MachineInfo[]>([]);
  const [selectedMachineId, setSelectedMachineId] = useState<string | null>(null);
  const selectedMachine = selectedMachineId ? machines.find(m => m.id === selectedMachineId) ?? null : null;
  const [drives, setDrives] = useState<DriveSummary[]>([]);

  // Widget layout state
  const [widgets, setWidgets] = useState<WidgetConfig[]>(() => loadWidgetConfig());
  const [customizing, setCustomizing] = useState(false);

  const handleWidgetChange = useCallback((next: WidgetConfig[]) => {
    setWidgets(next);
    saveWidgetConfig(next);
  }, []);

  useEffect(() => {
    api.drives.list().then((r) => setDrives(r.drives)).catch(() => {});
  }, []);

  useEffect(() => {
    let mounted = true;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const fetchMachines = async () => {
      try {
        const data = await api.getMachines();
        if (mounted) {
          setMachines(data.machines);
          const nextDelay = data.machines.length > 0 ? 2000 : 30000;
          timer = setTimeout(fetchMachines, nextDelay);
        }
      } catch {
        // non-critical: machine monitoring may be unused
        if (mounted) {
          timer = setTimeout(fetchMachines, 30000);
        }
      }
    };

    fetchMachines();
    return () => {
      mounted = false;
      if (timer) clearTimeout(timer);
    };
  }, []);

  // Use custom labels for gauge labels when available
  const cpuLabel = (cpuTemps[0] && sensorLabels[cpuTemps[0].id]) || 'CPU';
  const gpuLabel = (gpuTemps[0] && sensorLabels[gpuTemps[0].id]) || 'GPU';
  const hddLabel = (hddTemps[0] && sensorLabels[hddTemps[0].id]) || 'Storage';
  const caseLabel = (caseTemps[0] && sensorLabels[caseTemps[0].id]) || 'Case';

  const statusBadgeClass: Record<string, string> = {
    online: 'badge-success',
    degraded: 'badge-warning',
    offline: 'badge-danger',
    auth_error: 'badge-danger',
    version_mismatch: 'badge-warning',
    unknown: 'badge-warning',
  };

  if (selectedMachine !== null) {
    return <MachineDrillIn machine={selectedMachine} onClose={() => setSelectedMachineId(null)} />;
  }

  // Sort visible widgets by order
  const sortedWidgets = [...widgets].sort((a, b) => a.order - b.order);
  const isVisible = (type: WidgetType) => sortedWidgets.find((w) => w.type === type)?.visible ?? true;

  // ─── Widget renderers ───────────────────────────────────────────────────

  const renderCoolingScore = () => {
    if (!isVisible('cooling_score') || machines.length === 0) return null;
    return (
      <div key="cooling_score">
        <h3 className="section-title mb-3 px-1">Machines</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
          {machines.map((machine) => {
            const summary = machine.snapshot?.summary;
            return (
              <div
                key={machine.id}
                className="card p-4 animate-card-enter"
                role="button"
                tabIndex={0}
                aria-label={`View ${machine.name} details`}
                onClick={() => setSelectedMachineId(machine.id)}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setSelectedMachineId(machine.id); } }}
                style={{ cursor: 'pointer', transition: 'opacity 0.15s' }}
                onMouseEnter={(e) => { (e.currentTarget as HTMLDivElement).style.opacity = '0.8'; }}
                onMouseLeave={(e) => { (e.currentTarget as HTMLDivElement).style.opacity = '1'; }}
              >
                <div className="flex items-center justify-between gap-2 mb-2">
                  <p className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
                    {machine.name}
                  </p>
                  <span className={`badge ${statusBadgeClass[machine.status] || 'badge-warning'}`} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
                    <span style={{
                      display: 'inline-block',
                      width: 6,
                      height: 6,
                      borderRadius: '50%',
                      background: machine.status === 'online' ? 'var(--success)'
                        : machine.status === 'offline' || machine.status === 'auth_error' ? 'var(--danger)'
                        : machine.status === 'unknown' ? 'var(--text-secondary)'
                        : 'var(--warning)',
                    }} />
                    {machine.status === 'online' ? 'Online'
                      : machine.status === 'offline' ? 'Offline'
                      : machine.status === 'unknown' ? 'Unknown'
                      : machine.status.replace('_', ' ')}
                  </span>
                </div>
                <p className="text-xs mb-3 truncate" style={{ color: 'var(--text-secondary)' }}>
                  {machine.base_url}
                </p>
                <div className="grid grid-cols-3 gap-2 text-xs">
                  <div>
                    <p style={{ color: 'var(--text-secondary)' }}>CPU</p>
                    <p style={{ color: 'var(--text)' }}>
                      {summary?.cpu_temp != null ? formatTemp(summary.cpu_temp, tempUnit) : '--'}
                    </p>
                  </div>
                  <div>
                    <p style={{ color: 'var(--text-secondary)' }}>GPU</p>
                    <p style={{ color: 'var(--text)' }}>
                      {summary?.gpu_temp != null ? formatTemp(summary.gpu_temp, tempUnit) : '--'}
                    </p>
                  </div>
                  <div>
                    <p style={{ color: 'var(--text-secondary)' }}>Fresh</p>
                    <p style={{ color: 'var(--text)' }}>
                      {machine.freshness_seconds != null ? `${machine.freshness_seconds.toFixed(1)}s` : '--'}
                    </p>
                  </div>
                </div>
                {machine.last_error && (
                  <p className="text-xs mt-2 truncate" style={{ color: 'var(--danger)' }}>
                    {machine.last_error}
                  </p>
                )}
              </div>
            );
          })}
        </div>
      </div>
    );
  };

  const renderDriveHealth = () => {
    if (!isVisible('drive_health') || drives.length === 0) return null;
    const critical = drives.filter((d) => d.health_status === 'critical').length;
    const warning  = drives.filter((d) => d.health_status === 'warning').length;
    const hottest  = drives.reduce<DriveSummary | null>(
      (best, d) => (d.temperature_c != null && (best === null || d.temperature_c > (best.temperature_c ?? -Infinity))) ? d : best,
      null,
    );
    const hasProblem = critical > 0 || warning > 0;
    return (
      <div key="drive_health">
        <h3 className="section-title mb-3 px-1">Storage</h3>
        <div
          className="card flex items-center justify-between gap-4 cursor-pointer hover:opacity-90 transition-opacity"
          onClick={() => setPage('drives')}
        >
          <div className="flex items-center gap-3">
            <div
              className="w-10 h-10 rounded-lg flex items-center justify-center shrink-0"
              style={{ background: 'var(--accent-muted)' }}
            >
              <HardDrive size={20} style={{ color: hasProblem ? 'var(--warning)' : 'var(--accent)' }} />
            </div>
            <div>
              <p className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
                {drives.length} drive{drives.length !== 1 ? 's' : ''} detected
              </p>
              <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                {critical > 0
                  ? `${critical} critical`
                  : warning > 0
                  ? `${warning} warning`
                  : 'All healthy'}
                {hottest?.temperature_c != null && ` · hottest ${formatTemp(hottest.temperature_c, tempUnit)}`}
              </p>
            </div>
          </div>
          <div className="flex items-center gap-3">
            {hasProblem && <AlertTriangle size={16} style={{ color: critical > 0 ? 'var(--danger)' : 'var(--warning)' }} />}
            <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>View →</span>
          </div>
        </div>
      </div>
    );
  };

  const renderTempGauges = () => {
    if (!isVisible('temp_gauges')) return null;
    return (
      <div key="temp_gauges">
        <h3 className="section-title mb-3 px-1">Temperatures</h3>
        <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
          <TempGauge label={cpuLabel}  value={cpuTemp}  maxValue={100} icon={<Cpu       size={16} />} />
          <TempGauge label={gpuLabel}  value={gpuTemp}  maxValue={95}  icon={<Monitor   size={16} />} />
          <TempGauge label={hddLabel}  value={hddTemp}  maxValue={70}  icon={<HardDrive size={16} />} />
          <TempGauge label={caseLabel} value={caseTemp} maxValue={55}  icon={<Thermometer size={16} />} />
        </div>
      </div>
    );
  };

  const renderSystemLoad = () => {
    if (!isVisible('system_load')) return null;
    return (
      <div key="system_load">
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
    );
  };

  const renderFanSpeeds = () => {
    if (!isVisible('fan_speeds')) return null;
    return (
      <div key="fan_speeds">
        <h3 className="section-title mb-3 px-1">Fan Speeds</h3>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          {fanRpms.map((fan) => {
            const fanBase = fan.id.replace(/_rpm$/, '');
            const pct = fanPcts.find((f) => f.id.replace(/_pct$/, '') === fanBase);
            const fanLabel = sensorLabels[fan.id] || fan.name.replace(' RPM', '').replace(' Rpm', '');
            return (
              <FanSpeedCard
                key={fan.id}
                name={fanLabel}
                rpm={fan.value}
                percentage={pct?.value ?? 0}
                maxRpm={fan.max_value ?? 2000}
              />
            );
          })}
        </div>
      </div>
    );
  };

  const renderTempChart = () => {
    if (!isVisible('temp_chart')) return null;
    return <TempChart key="temp_chart" />;
  };

  const widgetRenderers: Record<WidgetType, () => React.ReactNode> = {
    cooling_score: renderCoolingScore,
    temp_gauges:   renderTempGauges,
    fan_speeds:    renderFanSpeeds,
    drive_health:  renderDriveHealth,
    system_load:   renderSystemLoad,
    temp_chart:    renderTempChart,
  };

  return (
    <div className="space-y-6 animate-fade-in">
      {!connected && (
        <div className="px-3 py-2 rounded text-xs" style={{ background: 'var(--warning)', color: '#000', opacity: 0.85 }}>
          Sensor data may be stale — reconnecting to backend…
        </div>
      )}

      {/* Customize button */}
      <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
        <button
          className="btn-secondary"
          style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}
          onClick={() => setCustomizing((v) => !v)}
          aria-expanded={customizing}
          aria-label="Customize dashboard layout"
        >
          <Settings2 size={14} />
          {customizing ? 'Close' : 'Customize'}
        </button>
      </div>

      {/* Widget configuration panel */}
      {customizing && (
        <CustomizePanel
          widgets={widgets}
          onChange={handleWidgetChange}
          onClose={() => setCustomizing(false)}
        />
      )}

      {/* Ordered widget list */}
      {sortedWidgets.map((w) => {
        const renderer = widgetRenderers[w.type];
        return renderer ? renderer() : null;
      })}
    </div>
  );
}
