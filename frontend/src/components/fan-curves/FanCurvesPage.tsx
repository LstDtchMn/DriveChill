'use client';

import { useState, useEffect } from 'react';
import { CurveEditor } from './CurveEditor';
import { PresetSelector } from './PresetSelector';
import { useSensors } from '@/hooks/useSensors';
import { api } from '@/lib/api';
import type { FanCurvePoint, FanCurve } from '@/lib/types';
import { Plus, Save } from 'lucide-react';

const DEFAULT_POINTS: FanCurvePoint[] = [
  { temp: 30, speed: 20 },
  { temp: 50, speed: 40 },
  { temp: 70, speed: 70 },
  { temp: 85, speed: 100 },
];

export function FanCurvesPage() {
  const { cpuTemps, gpuTemps, hddTemps, caseTemps, fanRpms } = useSensors();
  const [curves, setCurves] = useState<FanCurve[]>([]);
  const [selectedCurve, setSelectedCurve] = useState<string | null>(null);
  const [editingPoints, setEditingPoints] = useState<FanCurvePoint[]>(DEFAULT_POINTS);

  const allTempSensors = [...cpuTemps, ...gpuTemps, ...hddTemps, ...caseTemps];
  const allFans = fanRpms.map((f) => f.id.replace('_rpm', ''));

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
    } catch {
      // Handle error
    }
  };

  const handleNewCurve = async () => {
    const newCurve: FanCurve = {
      id: `curve_${Date.now()}`,
      name: `Custom Curve ${curves.length + 1}`,
      sensor_id: allTempSensors[0]?.id || 'cpu_temp_0',
      fan_id: allFans[0] || 'fan_cpu',
      points: [...DEFAULT_POINTS],
      enabled: true,
    };

    try {
      await api.updateCurve(newCurve);
      setCurves([...curves, newCurve]);
      setSelectedCurve(newCurve.id);
      setEditingPoints(newCurve.points);
    } catch {
      // Handle error
    }
  };

  return (
    <div className="space-y-6 animate-fade-in">
      <PresetSelector />

      <div className="border-t pt-6" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center justify-between mb-4">
          <h3 className="section-title">Custom Curves</h3>
          <button onClick={handleNewCurve} className="btn-primary flex items-center gap-2 text-sm">
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
                    {curve.sensor_id} → {curve.fan_id}
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
                width={500}
                height={300}
              />

              <div className="mt-4 flex items-center justify-between">
                <div className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  {editingPoints.length} control points
                </div>
                <button
                  onClick={handleSaveCurve}
                  className="btn-primary flex items-center gap-2 text-sm"
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
            <button onClick={handleNewCurve} className="btn-primary text-sm">
              Create Your First Curve
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
