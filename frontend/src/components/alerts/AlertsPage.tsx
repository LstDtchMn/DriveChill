'use client';

import { useState, useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useSensors } from '@/hooks/useSensors';
import { api } from '@/lib/api';
import { formatTemp, tempUnitSymbol, fToC } from '@/lib/tempUnit';
import { Bell, Plus, Trash2, AlertTriangle, CheckCircle, X, Zap } from 'lucide-react';
import type { AlertRule, AlertAction, Profile } from '@/lib/types';
import { useCanWrite } from '@/hooks/useCanWrite';
import { ViewerBanner } from '@/components/ui/ViewerBanner';

export function AlertsPage() {
  const canWrite = useCanWrite();
  const { alertEvents, activeAlerts, clearAlerts, addAlertEvents, setActiveAlerts } = useAppStore();
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const { cpuTemps, gpuTemps, hddTemps, caseTemps } = useSensors();
  const [rules, setRules] = useState<AlertRule[]>([]);
  const [showAddForm, setShowAddForm] = useState(false);
  const [newSensorId, setNewSensorId] = useState('');
  const [newThreshold, setNewThreshold] = useState(80);
  const [newName, setNewName] = useState('');
  const [newActionEnabled, setNewActionEnabled] = useState(false);
  const [newActionProfileId, setNewActionProfileId] = useState('');
  const [newActionRevert, setNewActionRevert] = useState(true);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [error, setError] = useState<string | null>(null);

  const allTempSensors = [...cpuTemps, ...gpuTemps, ...hddTemps, ...caseTemps];

  useEffect(() => {
    const fetchData = async () => {
      try {
        const [alertData, profileData] = await Promise.all([
          api.getAlerts(),
          api.getProfiles(),
        ]);
        setRules(alertData.rules);
        if (alertData.events && alertData.events.length > 0) {
          addAlertEvents(alertData.events);
        }
        if (alertData.active) {
          setActiveAlerts(alertData.active);
        }
        setProfiles(profileData.profiles || []);
      } catch {
        setError('Failed to load alerts. Check your connection.');
      }
    };
    fetchData();
  }, [addAlertEvents, setActiveAlerts]);

  const handleAddRule = async () => {
    if (!newSensorId) return;
    // Backend stores thresholds in Celsius — convert if user entered °F
    const thresholdC = tempUnit === 'F' ? Math.round(fToC(newThreshold)) : newThreshold;
    const action: AlertAction | null = newActionEnabled && newActionProfileId
      ? { type: 'switch_profile', profile_id: newActionProfileId, revert_after_clear: newActionRevert }
      : null;
    const rule: AlertRule = {
      id: crypto.randomUUID(),
      sensor_id: newSensorId,
      threshold: thresholdC,
      name: newName || `Alert on ${newSensorId}`,
      enabled: true,
      action,
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
      setNewActionEnabled(false);
      setNewActionProfileId('');
      setNewActionRevert(true);
    } catch (e: any) {
      setError(e?.message || 'Failed to add rule');
    }
  };

  const handleDeleteRule = async (id: string) => {
    try {
      await api.deleteAlertRule(id);
      setRules(rules.filter((r) => r.id !== id));
    } catch (e: any) {
      setError(e?.message || 'Failed to delete rule');
    }
  };

  const handleClearEvents = async () => {
    try {
      await api.clearAlerts();
      clearAlerts();
    } catch (e: any) {
      setError(e?.message || 'Failed to clear events');
    }
  };

  return (
    <div className="space-y-6 animate-fade-in">
      <ViewerBanner />
      {error && (
        <div className="card p-3 text-sm" style={{ borderColor: 'var(--danger)', color: 'var(--danger)' }}>
          {error}
          <button onClick={() => setError(null)} className="ml-2 underline text-xs">dismiss</button>
        </div>
      )}
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
            disabled={!canWrite}
            className="btn-primary min-h-11 flex items-center gap-2 text-sm disabled:opacity-50"
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
                  min={tempUnit === 'F' ? 68 : 20}
                  max={tempUnit === 'F' ? 230 : 110}
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
            {/* Action: profile switching */}
            <div className="mt-3 pt-3" style={{ borderTop: '1px solid var(--border)' }}>
              <label className="flex items-center gap-2 text-sm cursor-pointer">
                <input
                  type="checkbox"
                  checked={newActionEnabled}
                  onChange={(e) => setNewActionEnabled(e.target.checked)}
                />
                <Zap size={14} style={{ color: 'var(--accent)' }} />
                Switch profile when triggered
              </label>
              {newActionEnabled && (
                <div className="grid grid-cols-1 md:grid-cols-2 gap-3 mt-2">
                  <div>
                    <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Target Profile</label>
                    <select
                      value={newActionProfileId}
                      onChange={(e) => setNewActionProfileId(e.target.value)}
                      className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                      style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                    >
                      <option value="">Select profile...</option>
                      {profiles.map((p) => (
                        <option key={p.id} value={p.id}>{p.name}</option>
                      ))}
                    </select>
                  </div>
                  <div className="flex items-end">
                    <label className="flex items-center gap-2 text-sm cursor-pointer pb-2">
                      <input
                        type="checkbox"
                        checked={newActionRevert}
                        onChange={(e) => setNewActionRevert(e.target.checked)}
                      />
                      Revert when alert clears
                    </label>
                  </div>
                </div>
              )}
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
                        {rule.action?.profile_id && (
                          <span style={{ color: 'var(--accent)' }}>
                            {' '}· switches to {profiles.find((p) => p.id === rule.action?.profile_id)?.name || rule.action.profile_id}
                            {rule.action.revert_after_clear ? ' (reverts)' : ''}
                          </span>
                        )}
                      </p>
                    </div>
                  </div>
                  <button
                    onClick={() => handleDeleteRule(rule.id)}
                    disabled={!canWrite}
                  className="min-h-11 min-w-11 p-1.5 rounded hover:bg-surface-200 transition-colors disabled:opacity-40"
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
            <button onClick={handleClearEvents} disabled={!canWrite} className="btn-secondary text-xs disabled:opacity-50">
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
              {[...alertEvents].reverse().map((event) => (
                <div key={`${event.timestamp}-${event.rule_id}`} className="px-4 py-2.5 flex items-center justify-between">
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
