'use client';

import { useState, useEffect, useMemo } from 'react';
import { CurveEditor } from './CurveEditor';
import { PresetSelector } from './PresetSelector';
import { FanTestPanel } from '@/components/fans/FanTestPanel';
import { useSensors } from '@/hooks/useSensors';
import { useAppStore } from '@/stores/appStore';
import { APIError, api } from '@/lib/api';
import type { FanCurvePoint, FanCurve } from '@/lib/types';
import { Plus, Save } from 'lucide-react';

const DEFAULT_POINTS: FanCurvePoint[] = [
  { temp: 30, speed: 20 },
  { temp: 50, speed: 40 },
  { temp: 70, speed: 70 },
  { temp: 85, speed: 100 },
];

function formatDangerWarnings(detail: unknown): string[] {
  if (!detail || typeof detail !== 'object') return [];
  const d = detail as { detail?: { warnings?: Array<{ temp?: number; speed?: number; message?: string }> } };
  const warnings = d.detail?.warnings ?? [];
  return warnings.map((w) => {
    if (w.message) return w.message;
    return `Low speed ${w.speed ?? '?'}% at ${w.temp ?? '?'}°C`;
  });
}

export function FanCurvesPage() {
  const { all: allReadings, cpuTemps, gpuTemps, hddTemps, caseTemps, fanRpms, fanPcts } = useSensors();
  const appliedSpeeds = useAppStore((s) => s.appliedSpeeds);
  const [curves, setCurves] = useState<FanCurve[]>([]);
  const [selectedCurve, setSelectedCurve] = useState<string | null>(null);
  const [editingPoints, setEditingPoints] = useState<FanCurvePoint[]>(DEFAULT_POINTS);
  const [editorSize, setEditorSize] = useState({ width: 500, height: 300 });

  const allTempSensors = [...cpuTemps, ...gpuTemps, ...hddTemps, ...caseTemps];
  const allFans = fanRpms.map((f) => f.id.replace('_rpm', ''));

  // Compute the current operating point for the selected curve
  const operatingPoint = useMemo(() => {
    if (!selectedCurve) return { temp: undefined, speed: undefined };
    const curve = curves.find((c) => c.id === selectedCurve);
    if (!curve) return { temp: undefined, speed: undefined };

    // Find current temperature: use composite MAX if sensor_ids set, else primary sensor_id
    const sensorIds = curve.sensor_ids?.length ? curve.sensor_ids : [curve.sensor_id];
    let maxTemp: number | undefined;
    for (const sid of sensorIds) {
      const reading = allReadings.find((r) => r.id === sid);
      if (reading != null) {
        maxTemp = maxTemp == null ? reading.value : Math.max(maxTemp, reading.value);
      }
    }

    // Find current applied speed for this fan
    const fanKey = curve.fan_id;
    let currentSpeed: number | undefined = appliedSpeeds[fanKey] ?? appliedSpeeds[`${fanKey}_pct`];
    // Fallback: try fan_percent sensor
    if (currentSpeed == null) {
      const pctSensor = fanPcts.find((f) => f.id.replace(/_pct$/, '') === fanKey);
      currentSpeed = pctSensor?.value;
    }

    return { temp: maxTemp, speed: currentSpeed };
  }, [selectedCurve, curves, allReadings, appliedSpeeds, fanPcts]);

  useEffect(() => {
    const fetchCurves = async () => {
      try {
        const { curves: c } = await api.getCurves();
        setCurves(c);
        if (c.length > 0) {
          setSelectedCurve(c[0].id);
          setEditingPoints(c[0].points);
        }
      } catch {
        // API not available
      }
    };
    fetchCurves();
  }, []);

  useEffect(() => {
    const updateEditorSize = () => {
      const isMobile = window.innerWidth < 768;
      const width = isMobile
        ? Math.max(260, Math.min(500, window.innerWidth - 72))
        : 500;
      const height = isMobile ? 240 : 300;
      setEditorSize({ width, height });
    };

    updateEditorSize();
    window.addEventListener('resize', updateEditorSize);
    return () => window.removeEventListener('resize', updateEditorSize);
  }, []);

  const handleSelectCurve = (id: string) => {
    const curve = curves.find((c) => c.id === id);
    if (curve) {
      setSelectedCurve(id);
      setEditingPoints(curve.points);
    }
  };

  const handleSaveCurve = async () => {
    if (!selectedCurve) return;
    const curve = curves.find((c) => c.id === selectedCurve);
    if (!curve) return;

    const updated = { ...curve, points: editingPoints };
    try {
      await api.updateCurve(updated);
      setCurves(curves.map((c) => (c.id === selectedCurve ? updated : c)));
    } catch (err) {
      if (err instanceof APIError && err.status === 409) {
        const warnings = formatDangerWarnings(err.detail);
        const warningText = warnings.length > 0 ? `\n\n${warnings.join('\n')}` : '';
        const confirmOverride = window.confirm(
          `This curve has dangerous speed settings at high temperatures. Apply anyway?${warningText}`
        );
        if (!confirmOverride) return;

        try {
          await api.updateCurve(updated, true);
          setCurves(curves.map((c) => (c.id === selectedCurve ? updated : c)));
        } catch {
          alert('Failed to save curve.');
        }
        return;
      }
      alert('Failed to save curve. Check your connection.');
    }
  };

  const handleToggleSensor = (sensorId: string) => {
    if (!selectedCurve) return;
    const curve = curves.find((c) => c.id === selectedCurve);
    if (!curve) return;
    const ids = curve.sensor_ids ?? [];
    const next = ids.includes(sensorId)
      ? ids.filter((s) => s !== sensorId)
      : [...ids, sensorId];
    const updated = { ...curve, sensor_ids: next };
    setCurves(curves.map((c) => (c.id === selectedCurve ? updated : c)));
  };

  const handleNewCurve = async () => {
    const newCurve: FanCurve = {
      id: `curve_${Date.now()}`,
      name: `Custom Curve ${curves.length + 1}`,
      sensor_id: allTempSensors[0]?.id || 'cpu_temp_0',
      fan_id: allFans[0] || 'fan_cpu',
      points: [...DEFAULT_POINTS],
      enabled: true,
      sensor_ids: [],
    };

    try {
      await api.updateCurve(newCurve);
      setCurves([...curves, newCurve]);
      setSelectedCurve(newCurve.id);
      setEditingPoints(newCurve.points);
    } catch {
      alert('Failed to create curve. Check your connection.');
    }
  };

  return (
    <div className="space-y-6 animate-fade-in">
      <PresetSelector />

      <div className="border-t pt-6" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center justify-between gap-2 mb-4 flex-wrap">
          <h3 className="section-title">Custom Curves</h3>
          <button onClick={handleNewCurve} className="btn-primary min-h-11 flex items-center gap-2 text-sm">
            <Plus size={14} />
            New Curve
          </button>
        </div>

        {curves.length > 0 ? (
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Curve list */}
            <div className="space-y-2">
              {curves.map((curve) => (
                <button
                  key={curve.id}
                  onClick={() => handleSelectCurve(curve.id)}
                  className={`card w-full p-3 text-left transition-all ${
                    selectedCurve === curve.id ? 'ring-2' : ''
                  }`}
                  style={selectedCurve === curve.id ? { borderColor: 'var(--accent)', boxShadow: '0 0 0 2px var(--accent-muted)' } : {}}
                >
                  <p className="text-sm font-medium" style={{ color: 'var(--text)' }}>{curve.name}</p>
                  <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
                    {curve.sensor_ids?.length > 0
                      ? `MAX(${curve.sensor_ids.join(', ')})`
                      : curve.sensor_id}{' '}
                    → {curve.fan_id}
                  </p>
                  <div className="flex items-center gap-2 mt-2">
                    <span className={`badge ${curve.enabled ? 'badge-success' : 'badge-warning'}`}>
                      {curve.enabled ? 'Enabled' : 'Disabled'}
                    </span>
                    <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                      {curve.points.length} points
                    </span>
                  </div>
                </button>
              ))}
            </div>

            {/* Curve editor */}
            <div className="lg:col-span-2">
              <CurveEditor
                points={editingPoints}
                onChange={setEditingPoints}
                width={editorSize.width}
                height={editorSize.height}
                currentTemp={operatingPoint.temp}
                currentSpeed={operatingPoint.speed}
              />

              {/* Composite sensor selector */}
              {selectedCurve && (() => {
                const curve = curves.find((c) => c.id === selectedCurve);
                const ids = curve?.sensor_ids ?? [];
                return (
                  <div className="mt-3 p-3 rounded" style={{ background: 'var(--card-bg)', border: '1px solid var(--border)' }}>
                    <p className="text-xs font-medium mb-2" style={{ color: 'var(--text)' }}>
                      Temperature Sources {ids.length > 1 && <span className="badge badge-info ml-1">Composite — MAX</span>}
                    </p>
                    <div className="flex flex-wrap gap-2">
                      {allTempSensors.map((sensor) => (
                        <button
                          key={sensor.id}
                          onClick={() => handleToggleSensor(sensor.id)}
                          className={`text-xs min-h-11 px-2 py-1 rounded transition-all ${
                            ids.includes(sensor.id)
                              ? 'ring-1'
                              : ''
                          }`}
                          style={{
                            background: ids.includes(sensor.id) ? 'var(--accent-muted)' : 'var(--bg)',
                            color: ids.includes(sensor.id) ? 'var(--accent)' : 'var(--text-secondary)',
                            borderColor: ids.includes(sensor.id) ? 'var(--accent)' : 'var(--border)',
                            border: '1px solid',
                          }}
                        >
                          {sensor.name || sensor.id}
                        </button>
                      ))}
                    </div>
                    {ids.length === 0 && (
                      <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
                        Using primary sensor: {curve?.sensor_id}. Select multiple for composite MAX.
                      </p>
                    )}
                  </div>
                );
              })()}

              <div className="mt-4 flex items-center justify-between">
                <div className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  {editingPoints.length} control points
                </div>
                <button
                  onClick={handleSaveCurve}
                  className="btn-primary min-h-11 flex items-center gap-2 text-sm"
                >
                  <Save size={14} />
                  Save Curve
                </button>
              </div>
            </div>
          </div>
        ) : (
          <div className="card p-8 text-center">
            <p className="text-sm mb-3" style={{ color: 'var(--text-secondary)' }}>
              No custom curves yet. Create one or activate a preset above.
            </p>
            <button onClick={handleNewCurve} className="btn-primary min-h-11 text-sm">
              Create Your First Curve
            </button>
          </div>
        )}
      </div>

      {/* Fan Benchmarks */}
      {allFans.length > 0 && (
        <div className="border-t pt-6" style={{ borderColor: 'var(--border)' }}>
          <h3 className="section-title mb-4">Fan Benchmarks</h3>
          <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
            Sweep each fan from 0% to 100% to find its stall point and maximum RPM.
            The curve engine pauses for the fan under test.
          </p>
          <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-4 gap-4">
            {allFans.map((fanId) => (
              <FanTestPanel
                key={fanId}
                fanId={fanId}
                fanName={fanId.replace(/_/g, ' ').toUpperCase()}
              />
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
