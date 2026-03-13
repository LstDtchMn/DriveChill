'use client';

import { useEffect, useState, useCallback } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { api } from '@/lib/api';
import { cToF, fToC } from '@/lib/tempUnit';
import type { TemperatureTarget, SensorReading } from '@/lib/types';
import { Plus, Trash2, Edit3, ToggleLeft, ToggleRight, Thermometer, Map, List } from 'lucide-react';
import { useConfirm } from '@/components/ui/ConfirmDialog';
import { useCanWrite } from '@/hooks/useCanWrite';
import { ViewerBanner } from '@/components/ui/ViewerBanner';

// Temperature unit helpers (wraps raw converters for convenience)
const toDisplay = (celsius: number, unit: string) => unit === 'F' ? cToF(celsius) : celsius;
const toC = (value: number, unit: string) => unit === 'F' ? fToC(value) : value;
// Delta helpers — tolerance is a temperature *difference*, not absolute (no +32 offset)
const toDisplayDelta = (celsius: number, unit: string) => unit === 'F' ? celsius * 9 / 5 : celsius;
const toCDelta = (value: number, unit: string) => unit === 'F' ? value * 5 / 9 : value;

// ── Proportional speed helper (client-side, matches backend) ─────────────────

function clientProportional(temp: number, target: TemperatureTarget): number {
  const low = target.target_temp_c - target.tolerance_c;
  const high = target.target_temp_c + target.tolerance_c;
  if (temp <= low) return target.min_fan_speed;
  if (temp >= high) return 100;
  const t = (temp - low) / (2 * target.tolerance_c);
  return target.min_fan_speed + t * (100 - target.min_fan_speed);
}

function speedLabel(temp: number | undefined, target: TemperatureTarget): string {
  if (temp === undefined) return 'N/A';
  const speed = clientProportional(temp, target);
  const low = target.target_temp_c - target.tolerance_c;
  const high = target.target_temp_c + target.tolerance_c;
  if (temp <= low) return `${speed.toFixed(0)}% (at floor)`;
  if (temp >= high) return '100% (full)';
  return `${speed.toFixed(0)}% (ramping)`;
}

function thermalColor(temp: number | undefined, target: TemperatureTarget): string {
  if (temp === undefined) return 'var(--text-secondary)';
  const low = target.target_temp_c - target.tolerance_c;
  const high = target.target_temp_c + target.tolerance_c;
  if (temp <= low) return 'var(--success)';
  if (temp >= high) return 'var(--danger)';
  return 'var(--warning)';
}

// ── Target form ──────────────────────────────────────────────────────────────

interface TargetFormProps {
  initial?: TemperatureTarget;
  drives: SensorReading[];
  fans: SensorReading[];
  onSave: (data: any) => Promise<void>;
  onCancel: () => void;
}

function TargetForm({ initial, drives, fans, onSave, onCancel }: TargetFormProps) {
  const { tempUnit } = useSettingsStore();
  const [name, setName] = useState(initial?.name ?? '');
  const [sensorId, setSensorId] = useState(initial?.sensor_id ?? '');
  const [fanIds, setFanIds] = useState<string[]>(initial?.fan_ids ?? []);
  const [targetTemp, setTargetTemp] = useState(
    initial ? toDisplay(initial.target_temp_c, tempUnit) : toDisplay(45, tempUnit)
  );
  const [tolerance, setTolerance] = useState(
    initial ? toDisplayDelta(initial.tolerance_c, tempUnit) : toDisplayDelta(5, tempUnit)
  );
  const [minSpeed, setMinSpeed] = useState(initial?.min_fan_speed ?? 20);
  const [pidMode, setPidMode] = useState(initial?.pid_mode ?? false);
  const [pidKp, setPidKp] = useState(initial?.pid_kp ?? 5.0);
  const [pidKi, setPidKi] = useState(initial?.pid_ki ?? 0.05);
  const [pidKd, setPidKd] = useState(initial?.pid_kd ?? 1.0);
  const [showPid, setShowPid] = useState(initial?.pid_mode ?? false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const driveId = drives.find(d => d.id === sensorId)?.id?.replace('hdd_temp_', '') ?? null;

  const handleSubmit = async () => {
    setError('');
    if (!sensorId) { setError('Select a drive'); return; }
    if (fanIds.length === 0) { setError('Select at least one fan'); return; }
    setSaving(true);
    try {
      await onSave({
        name: name || `Target for ${sensorId.replace('hdd_temp_', '')}`,
        drive_id: driveId,
        sensor_id: sensorId,
        fan_ids: fanIds,
        target_temp_c: toC(targetTemp, tempUnit),
        tolerance_c: toCDelta(tolerance, tempUnit),
        min_fan_speed: minSpeed,
        pid_mode: pidMode,
        pid_kp: pidKp,
        pid_ki: pidKi,
        pid_kd: pidKd,
      });
    } catch (e: any) {
      const body = e?.detail;
      let msg = e?.message ?? 'Failed to save';
      if (body && typeof body === 'object') {
        if (typeof body.detail === 'string') {
          msg = body.detail;
        } else if (Array.isArray(body.detail) && body.detail.length > 0) {
          // FastAPI 422 validation errors: [{loc, msg, type}]
          msg = body.detail.map((err: any) => err?.msg ?? String(err)).join('; ');
        }
      }
      setError(msg);
    } finally {
      setSaving(false);
    }
  };

  const toggleFan = (id: string) => {
    setFanIds(prev => prev.includes(id) ? prev.filter(f => f !== id) : [...prev, id]);
  };

  return (
    <div className="card p-4 space-y-4 animate-fade-in" style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: '12px' }}>
      <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
        {initial ? 'Edit Target' : 'New Temperature Target'}
      </h3>

      <div>
        <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Name</label>
        <input
          value={name}
          onChange={e => setName(e.target.value)}
          placeholder="e.g. Bay Fan Control"
          className="w-full px-3 py-2 rounded-lg text-sm"
          style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
        />
      </div>

      <div>
        <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Drive (sensor)</label>
        <select
          value={sensorId}
          onChange={e => setSensorId(e.target.value)}
          className="w-full px-3 py-2 rounded-lg text-sm"
          style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
        >
          <option value="">Select a drive...</option>
          {drives.map(d => (
            <option key={d.id} value={d.id}>{d.name} ({d.value.toFixed(1)}°C)</option>
          ))}
        </select>
      </div>

      <div>
        <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Fans to control</label>
        <div className="flex flex-wrap gap-2">
          {fans.map(f => (
            <button
              key={f.id}
              onClick={() => toggleFan(f.id)}
              className="px-3 py-1.5 rounded-lg text-xs font-medium transition-colors"
              style={fanIds.includes(f.id)
                ? { background: 'var(--accent)', color: 'white' }
                : { background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text-secondary)' }}
            >
              {f.name}
            </button>
          ))}
          {fans.length === 0 && <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>No fans detected</span>}
        </div>
      </div>

      <div className="grid grid-cols-3 gap-3">
        <div>
          <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>
            Target °{tempUnit}
          </label>
          <input
            type="number"
            value={targetTemp}
            onChange={e => setTargetTemp(Number(e.target.value))}
            className="w-full px-3 py-2 rounded-lg text-sm"
            style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
        </div>
        <div>
          <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>
            Tolerance ±°{tempUnit}
          </label>
          <input
            type="number"
            value={tolerance}
            onChange={e => setTolerance(Number(e.target.value))}
            min={1} max={20}
            className="w-full px-3 py-2 rounded-lg text-sm"
            style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
        </div>
        <div>
          <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Floor %</label>
          <input
            type="number"
            value={minSpeed}
            onChange={e => setMinSpeed(Number(e.target.value))}
            min={0} max={100}
            className="w-full px-3 py-2 rounded-lg text-sm"
            style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
          />
        </div>
      </div>

      {/* Advanced PID gains — collapsed by default */}
      <div>
        <button
          type="button"
          onClick={() => setShowPid(v => !v)}
          className="text-xs font-medium flex items-center gap-1"
          style={{ color: 'var(--accent)', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
        >
          {showPid ? '▾' : '▸'} Advanced: PID Controller
          {pidMode && <span className="badge badge-success ml-1">On</span>}
        </button>
        {showPid && (
          <div className="mt-3 p-3 rounded-lg space-y-3" style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
            <div className="flex items-center justify-between">
              <span className="text-xs font-medium" style={{ color: 'var(--text)' }}>Enable PID controller</span>
              <button
                type="button"
                onClick={() => setPidMode(v => !v)}
                className="text-xs px-3 py-1 rounded-full font-medium transition-colors"
                style={pidMode
                  ? { background: 'var(--accent)', color: 'white' }
                  : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
              >
                {pidMode ? 'Enabled' : 'Disabled'}
              </button>
            </div>
            {pidMode && (
              <>
                <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  PID replaces the tolerance band with continuous control.
                  Kp=proportional, Ki=integral (windup clamped), Kd=derivative.
                </p>
                <div className="grid grid-cols-3 gap-3">
                  <div>
                    <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Kp (%/°C)</label>
                    <input
                      type="number" step="0.1" min="0" max="100"
                      value={pidKp}
                      onChange={e => setPidKp(Number(e.target.value))}
                      className="w-full px-3 py-2 rounded-lg text-sm"
                      style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                    />
                  </div>
                  <div>
                    <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Ki (%/°C·s)</label>
                    <input
                      type="number" step="0.01" min="0" max="10"
                      value={pidKi}
                      onChange={e => setPidKi(Number(e.target.value))}
                      className="w-full px-3 py-2 rounded-lg text-sm"
                      style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                    />
                  </div>
                  <div>
                    <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Kd (%·s/°C)</label>
                    <input
                      type="number" step="0.1" min="0" max="100"
                      value={pidKd}
                      onChange={e => setPidKd(Number(e.target.value))}
                      className="w-full px-3 py-2 rounded-lg text-sm"
                      style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                    />
                  </div>
                </div>
              </>
            )}
          </div>
        )}
      </div>

      {error && <p className="text-xs" style={{ color: 'var(--danger)' }}>{error}</p>}

      <div className="flex gap-2 justify-end">
        <button onClick={onCancel} className="btn-secondary px-4 py-2 rounded-lg text-sm">Cancel</button>
        <button
          onClick={handleSubmit}
          disabled={saving}
          className="btn-primary px-4 py-2 rounded-lg text-sm font-medium"
          style={{ background: 'var(--accent)', color: 'white', opacity: saving ? 0.7 : 1 }}
        >
          {saving ? 'Saving...' : initial ? 'Update' : 'Create'}
        </button>
      </div>
    </div>
  );
}

// ── Relationship Map ─────────────────────────────────────────────────────────

function RelationshipMap({
  targets, drives, fans, sensorMap, onEdit, onToggle: _onToggle, onDelete: _onDelete,
}: {
  targets: TemperatureTarget[];
  drives: SensorReading[];
  fans: SensorReading[];
  sensorMap: Record<string, number>;
  onEdit: (t: TemperatureTarget) => void;
  onToggle: (t: TemperatureTarget) => void;
  onDelete: (t: TemperatureTarget) => void;
}) {
  const { tempUnit } = useSettingsStore();
  const driveH = 52, fanH = 52, padX = 20, padY = 20;
  const driveCount = drives.length || 1;
  const fanCount = fans.length || 1;
  const maxCount = Math.max(driveCount, fanCount);
  const height = padY * 2 + maxCount * driveH + 20;
  const width = 700;
  const driveX = padX + 160;
  const fanX = width - padX - 160;

  return (
    <div style={{ overflowX: 'auto' }}>
      <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} style={{ minWidth: 500 }}>
        {/* Drive nodes */}
        {drives.map((d, i) => {
          const y = padY + i * driveH + driveH / 2;
          const temp = sensorMap[d.id];
          return (
            <g key={d.id}>
              <rect x={padX} y={y - 18} width={160} height={36} rx={8}
                fill="var(--card-bg)" stroke="var(--border)" strokeWidth={1} />
              <text x={padX + 10} y={y - 2} fontSize={11} fontWeight={600} fill="var(--text)">
                {d.name.length > 18 ? d.name.slice(0, 16) + '...' : d.name}
              </text>
              <text x={padX + 10} y={y + 12} fontSize={10} fill="var(--text-secondary)">
                {temp !== undefined ? `${toDisplay(temp, tempUnit).toFixed(1)}°${tempUnit}` : 'offline'}
              </text>
            </g>
          );
        })}

        {/* Fan nodes */}
        {fans.map((f, i) => {
          const y = padY + i * fanH + fanH / 2;
          return (
            <g key={f.id}>
              <rect x={fanX} y={y - 18} width={160} height={36} rx={8}
                fill="var(--card-bg)" stroke="var(--border)" strokeWidth={1} />
              <text x={fanX + 10} y={y - 2} fontSize={11} fontWeight={600} fill="var(--text)">
                {f.name.length > 18 ? f.name.slice(0, 16) + '...' : f.name}
              </text>
              <text x={fanX + 10} y={y + 12} fontSize={10} fill="var(--text-secondary)">
                {f.value.toFixed(0)} RPM
              </text>
            </g>
          );
        })}

        {/* Connection lines */}
        {targets.map(t => {
          const driveIdx = drives.findIndex(d => d.id === t.sensor_id);
          if (driveIdx < 0) return null;
          const dy = padY + driveIdx * driveH + driveH / 2;
          const temp = sensorMap[t.sensor_id];
          const color = !t.enabled ? 'var(--text-secondary)' : thermalColor(temp, t);

          return (
            <g key={t.id}>
              {t.fan_ids.map(fanId => {
                const fanIdx = fans.findIndex(f => f.id === fanId);
                if (fanIdx < 0) return null;
                const fy = padY + fanIdx * fanH + fanH / 2;
                const cx1 = driveX + 40, cx2 = fanX - 40;

                return (
                  <path
                    key={`${t.id}-${fanId}`}
                    d={`M ${driveX} ${dy} C ${cx1} ${dy}, ${cx2} ${fy}, ${fanX} ${fy}`}
                    fill="none"
                    stroke={color}
                    strokeWidth={2}
                    strokeDasharray={t.enabled ? undefined : '6 4'}
                    opacity={t.enabled ? 0.9 : 0.4}
                    style={{ cursor: 'pointer' }}
                    onClick={() => onEdit(t)}
                  >
                    <title>
                      {t.name} — {temp !== undefined ? speedLabel(temp, t) : 'offline'}
                      {'\n'}Click to edit
                    </title>
                  </path>
                );
              })}
            </g>
          );
        })}
      </svg>
    </div>
  );
}

// ── Target List (card view) ──────────────────────────────────────────────────

function TargetList({
  targets, sensorMap, onEdit, onToggle, onDelete,
}: {
  targets: TemperatureTarget[];
  sensorMap: Record<string, number>;
  onEdit: (t: TemperatureTarget) => void;
  onToggle: (t: TemperatureTarget) => void;
  onDelete: (t: TemperatureTarget) => void;
}) {
  const canWrite = useCanWrite();
  const { tempUnit } = useSettingsStore();

  if (targets.length === 0) {
    return (
      <div className="text-center py-12" style={{ color: 'var(--text-secondary)' }}>
        <Thermometer size={40} style={{ margin: '0 auto 12px', opacity: 0.4 }} />
        <p className="text-sm">No temperature targets configured.</p>
        <p className="text-xs mt-1">Create one to automatically control fan speeds based on drive temperatures.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {targets.map(t => {
        const temp = sensorMap[t.sensor_id];
        const color = thermalColor(temp, t);
        return (
          <div
            key={t.id}
            className="card p-4 animate-card-enter"
            style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: '12px', borderLeft: `3px solid ${color}` }}
          >
            <div className="flex items-start justify-between gap-3">
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 mb-1">
                  <span className="text-sm font-semibold" style={{ color: 'var(--text)' }}>
                    {t.name || t.sensor_id}
                  </span>
                  {!t.enabled && (
                    <span className="badge text-[10px] px-1.5 py-0.5" style={{ background: 'var(--surface-200)', color: 'var(--text-secondary)' }}>
                      disabled
                    </span>
                  )}
                </div>
                <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  Target: {toDisplay(t.target_temp_c, tempUnit).toFixed(0)}°{tempUnit} ±{toDisplayDelta(t.tolerance_c, tempUnit).toFixed(0)}°{tempUnit}
                  {' '} Floor: {t.min_fan_speed.toFixed(0)}%
                </p>
                <p className="text-xs mt-0.5" style={{ color }}>
                  Now: {temp !== undefined
                    ? `${toDisplay(temp, tempUnit).toFixed(1)}°${tempUnit} → ${speedLabel(temp, t)}`
                    : 'sensor offline'}
                </p>
                <p className="text-[10px] mt-1" style={{ color: 'var(--text-secondary)' }}>
                  Fans: {t.fan_ids.join(', ')}
                </p>
              </div>
              <div className="flex gap-1.5 shrink-0">
                <button
                  onClick={() => canWrite && onToggle(t)}
                  disabled={!canWrite}
                  className="p-1.5 rounded-lg hover:bg-surface-200 transition-colors disabled:opacity-40"
                  title={t.enabled ? 'Disable' : 'Enable'}
                >
                  {t.enabled
                    ? <ToggleRight size={18} style={{ color: 'var(--success)' }} />
                    : <ToggleLeft size={18} style={{ color: 'var(--text-secondary)' }} />}
                </button>
                <button
                  onClick={() => canWrite && onEdit(t)}
                  disabled={!canWrite}
                  className="p-1.5 rounded-lg hover:bg-surface-200 transition-colors disabled:opacity-40"
                  title="Edit"
                >
                  <Edit3 size={16} style={{ color: 'var(--text-secondary)' }} />
                </button>
                <button
                  onClick={() => canWrite && onDelete(t)}
                  disabled={!canWrite}
                  className="p-1.5 rounded-lg hover:bg-surface-200 transition-colors disabled:opacity-40"
                  title="Delete"
                >
                  <Trash2 size={16} style={{ color: 'var(--danger)' }} />
                </button>
              </div>
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ── Main page ────────────────────────────────────────────────────────────────

export function TemperatureTargetsPage() {
  const confirm = useConfirm();
  const canWrite = useCanWrite();
  const readings = useAppStore(s => s.readings);
  const [targets, setTargets] = useState<TemperatureTarget[]>([]);
  const [view, setView] = useState<'map' | 'list'>('list');
  const [editing, setEditing] = useState<TemperatureTarget | null>(null);
  const [creating, setCreating] = useState(false);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState<string | null>(null);

  const drives = readings.filter(r => r.sensor_type === 'hdd_temp');
  // Use controllable fan IDs (strip _rpm suffix) — backend expects fan_cpu, not fan_cpu_rpm
  const fans = readings
    .filter(r => r.sensor_type === 'fan_rpm')
    .map(r => ({ ...r, id: r.id.replace(/_rpm$/, ''), name: r.name.replace(/ RPM$/, '') }));
  const sensorMap: Record<string, number> = {};
  readings.forEach(r => { sensorMap[r.id] = r.value; });

  const load = useCallback(async () => {
    try {
      const { targets: t } = await api.temperatureTargets.list();
      setTargets(t);
    } catch { /* will retry */ }
    setLoading(false);
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleCreate = async (data: any) => {
    try {
      await api.temperatureTargets.create(data);
      setCreating(false);
      load();
    } catch (e: any) {
      setPageError(e?.message || 'Failed to create target');
    }
  };

  const handleUpdate = async (data: any) => {
    if (!editing) return;
    try {
      await api.temperatureTargets.update(editing.id, data);
      setEditing(null);
      load();
    } catch (e: any) {
      setPageError(e?.message || 'Failed to update target');
    }
  };

  const handleToggle = async (t: TemperatureTarget) => {
    try {
      await api.temperatureTargets.toggle(t.id, !t.enabled);
      load();
    } catch (e: any) {
      setPageError(e?.message || 'Failed to toggle target');
    }
  };

  const handleDelete = async (t: TemperatureTarget) => {
    if (!(await confirm(`Delete target "${t.name || t.sensor_id}"?`))) return;
    try {
      await api.temperatureTargets.delete(t.id);
      load();
    } catch (e: any) {
      setPageError(e?.message || 'Failed to delete target');
    }
  };

  if (loading) {
    return <p className="text-sm py-8 text-center" style={{ color: 'var(--text-secondary)' }}>Loading...</p>;
  }

  return (
    <div className="space-y-4 max-w-4xl mx-auto animate-fade-in">
      <ViewerBanner />
      {pageError && (
        <div className="card p-3 text-sm" style={{ borderColor: 'var(--danger)', color: 'var(--danger)' }}>
          {pageError}
          <button onClick={() => setPageError(null)} className="ml-2 underline text-xs">dismiss</button>
        </div>
      )}
      {/* Header bar */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <button
            onClick={() => setView('map')}
            className="px-3 py-1.5 rounded-lg text-xs font-medium transition-colors"
            style={view === 'map'
              ? { background: 'var(--accent)', color: 'white' }
              : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
          >
            <Map size={14} className="inline mr-1" />Map
          </button>
          <button
            onClick={() => setView('list')}
            className="px-3 py-1.5 rounded-lg text-xs font-medium transition-colors"
            style={view === 'list'
              ? { background: 'var(--accent)', color: 'white' }
              : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
          >
            <List size={14} className="inline mr-1" />List
          </button>
        </div>
        <button
          onClick={() => { setCreating(true); setEditing(null); }}
          disabled={!canWrite}
          className="btn-primary px-3 py-1.5 rounded-lg text-xs font-medium flex items-center gap-1 disabled:opacity-50"
          style={{ background: 'var(--accent)', color: 'white' }}
        >
          <Plus size={14} /> Add Target
        </button>
      </div>

      {/* Create / edit form */}
      {creating && (
        <TargetForm
          drives={drives}
          fans={fans}
          onSave={handleCreate}
          onCancel={() => setCreating(false)}
        />
      )}
      {editing && (
        <TargetForm
          initial={editing}
          drives={drives}
          fans={fans}
          onSave={handleUpdate}
          onCancel={() => setEditing(null)}
        />
      )}

      {/* View */}
      {view === 'map' ? (
        <RelationshipMap
          targets={targets}
          drives={drives}
          fans={fans}
          sensorMap={sensorMap}
          onEdit={t => { if (canWrite) { setEditing(t); setCreating(false); } }}
          onToggle={canWrite ? handleToggle : () => {}}
          onDelete={canWrite ? handleDelete : () => {}}
        />
      ) : (
        <TargetList
          targets={targets}
          sensorMap={sensorMap}
          onEdit={t => { setEditing(t); setCreating(false); }}
          onToggle={handleToggle}
          onDelete={handleDelete}
        />
      )}
    </div>
  );
}
