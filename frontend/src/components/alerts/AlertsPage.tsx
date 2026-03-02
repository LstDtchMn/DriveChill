'use client';

import { useState, useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useSensors } from '@/hooks/useSensors';
import { api } from '@/lib/api';
import { formatTemp, tempUnitSymbol } from '@/lib/tempUnit';
import { Bell, Plus, Trash2, AlertTriangle, CheckCircle, X } from 'lucide-react';
import type { AlertRule } from '@/lib/types';

export function AlertsPage() {
  const { alertEvents, activeAlerts, clearAlerts, addAlertEvents, setActiveAlerts } = useAppStore();
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const { cpuTemps, gpuTemps, hddTemps, caseTemps } = useSensors();
  const [rules, setRules] = useState<AlertRule[]>([]);
  const [showAddForm, setShowAddForm] = useState(false);
  const [newSensorId, setNewSensorId] = useState('');
  const [newThreshold, setNewThreshold] = useState(80);
  const [newName, setNewName] = useState('');

  const allTempSensors = [...cpuTemps, ...gpuTemps, ...hddTemps, ...caseTemps];

  useEffect(() => {
    const fetchRules = async () => {
      try {
        const data = await api.getAlerts();
        setRules(data.rules);
        // Seed the store with existing alert events so the event log
        // is populated on initial page load (not only via WebSocket).
        if (data.events && data.events.length > 0) {
          addAlertEvents(data.events);
        }
        if (data.active) {
          setActiveAlerts(data.active);
        }
      } catch {
        // API not available
      }
    };
    fetchRules();
  }, [addAlertEvents, setActiveAlerts]);

  const handleAddRule = async () => {
    if (!newSensorId) return;
    const rule: AlertRule = {
      id: `alert_${Date.now()}`,
      sensor_id: newSensorId,
      threshold: newThreshold,
      name: newName || `Alert on ${newSensorId}`,
      enabled: true,
    };

    try {
      const resp = await api.addAlertRule(rule);
      // Use the server-assigned rule (with server-generated ID) so that
      // delete and active-alert highlight use the correct ID.
      const saved: AlertRule = resp?.rule ?? rule;
      setRules([...rules, saved]);
      setShowAddForm(false);
      setNewName('');
      setNewSensorId('');
      setNewThreshold(80);
    } catch {
      // Handle error
    }
  };

  const handleDeleteRule = async (id: string) => {
    try {
      await api.deleteAlertRule(id);
      setRules(rules.filter((r) => r.id !== id));
    } catch {
      // Handle error
    }
  };

  const handleClearEvents = async () => {
    try {
      await api.clearAlerts();
      clearAlerts();
    } catch {
      // Handle error
    }
  };

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Active alerts banner */}
      {activeAlerts.length > 0 && (
        <div className="card p-4 animate-card-enter" style={{ borderColor: 'var(--danger)', background: 'rgba(239, 68, 68, 0.08)' }}>
          <div className="flex items-center gap-3">
            <AlertTriangle size={20} style={{ color: 'var(--danger)' }} />
            <div>
              <p className="text-sm font-semibold" style={{ color: 'var(--danger)' }}>
                {activeAlerts.length} Active Alert{activeAlerts.length > 1 ? 's' : ''}
              </p>
              <p className="text-xs mt-0.5" style={{ color: 'var(--text-secondary)' }}>
                Temperature thresholds exceeded
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Alert rules */}
      <div>
        <div className="flex items-center justify-between gap-2 mb-3 flex-wrap">
          <h3 className="section-title">Alert Rules</h3>
          <button
            onClick={() => setShowAddForm(!showAddForm)}
            className="btn-primary min-h-11 flex items-center gap-2 text-sm"
          >
            <Plus size={14} />
            Add Rule
          </button>
        </div>

        {/* Add rule form */}
        {showAddForm && (
          <div className="card p-4 mb-4 animate-slide-up">
            <div className="grid grid-cols-1 md:grid-cols-4 gap-3">
              <div>
                <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Name</label>
                <input
                  type="text"
                  value={newName}
                  onChange={(e) => setNewName(e.target.value)}
                  placeholder="Alert name..."
                  className="w-full px-3 py-2 rounded-lg text-sm border outline-none focus:ring-2"
                  style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                />
              </div>
              <div>
                <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Sensor</label>
                <select
                  value={newSensorId}
                  onChange={(e) => setNewSensorId(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                  style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                >
                  <option value="">Select sensor...</option>
                  {allTempSensors.map((s) => (
                    <option key={s.id} value={s.id}>{s.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Threshold ({tempUnitSymbol(tempUnit)})</label>
                <input
                  type="number"
                  value={newThreshold}
                  onChange={(e) => setNewThreshold(Number(e.target.value))}
                  min={20}
                  max={110}
                  className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                  style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                />
              </div>
              <div className="flex items-end gap-2">
                <button onClick={handleAddRule} className="btn-primary min-h-11 text-sm flex-1">Add</button>
                <button onClick={() => setShowAddForm(false)} className="btn-secondary min-h-11 text-sm p-2">
                  <X size={16} />
                </button>
              </div>
            </div>
          </div>
        )}

        {/* Rules list */}
        <div className="space-y-2">
          {rules.length === 0 ? (
            <div className="card p-6 text-center">
              <Bell size={24} style={{ color: 'var(--text-secondary)' }} className="mx-auto mb-2 opacity-50" />
              <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
                No alert rules configured. Add one to get notified of temperature spikes.
              </p>
            </div>
          ) : (
            rules.map((rule) => {
              const isActive = activeAlerts.includes(rule.id);
              return (
                <div
                  key={rule.id}
                  className="card p-3 flex items-center justify-between animate-card-enter"
                  style={isActive ? { borderColor: 'var(--danger)' } : {}}
                >
                  <div className="flex items-center gap-3">
                    {isActive ? (
                      <AlertTriangle size={16} style={{ color: 'var(--danger)' }} />
                    ) : (
                      <CheckCircle size={16} style={{ color: 'var(--success)' }} />
                    )}
                    <div>
                      <p className="text-sm font-medium" style={{ color: 'var(--text)' }}>{rule.name}</p>
                      <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                        {rule.sensor_id} &ge; {formatTemp(rule.threshold, tempUnit)}
                      </p>
                    </div>
                  </div>
                  <button
                    onClick={() => handleDeleteRule(rule.id)}
                  className="min-h-11 min-w-11 p-1.5 rounded hover:bg-surface-200 transition-colors"
                >
                  <Trash2 size={14} style={{ color: 'var(--text-secondary)' }} />
                </button>
                </div>
              );
            })
          )}
        </div>
      </div>

      {/* Event log */}
      <div>
        <div className="flex items-center justify-between gap-2 mb-3 flex-wrap">
          <h3 className="section-title">Event Log</h3>
          {alertEvents.length > 0 && (
            <button onClick={handleClearEvents} className="btn-secondary text-xs">
              Clear All
            </button>
          )}
        </div>
        <div className="card overflow-hidden">
          {alertEvents.length === 0 ? (
            <div className="p-6 text-center">
              <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No events recorded yet.</p>
            </div>
          ) : (
            <div className="max-h-64 overflow-y-auto divide-y" style={{ borderColor: 'var(--border)' }}>
              {[...alertEvents].reverse().map((event, i) => (
                <div key={i} className="px-4 py-2.5 flex items-center justify-between">
                  <div>
                    <p className="text-sm" style={{ color: 'var(--text)' }}>{event.message}</p>
                    <p className="text-xs mt-0.5" style={{ color: 'var(--text-secondary)' }}>
                      {new Date(event.timestamp).toLocaleString()}
                    </p>
                  </div>
                  <span className="badge badge-danger">{formatTemp(event.actual_value, tempUnit)}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
