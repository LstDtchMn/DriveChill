'use client';

import { useState, useEffect } from 'react';
import { api, getApiBaseUrl } from '@/lib/api';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useSensors } from '@/hooks/useSensors';
import type { TempUnit } from '@/lib/tempUnit';
import { requestNotificationPermission } from '@/hooks/useNotifications';
import { Save, RefreshCw, Download, Info, Pencil, X, Check, Bell, BellOff } from 'lucide-react';

interface AppSettings {
  sensor_poll_interval: number;
  history_retention_hours: number;
  temp_unit: string;
  hardware_backend: string;
  backend_name: string;
}

export function SettingsPage() {
  const { backendName } = useAppStore();
  const { setTempUnit, sensorLabels, setSensorLabels, setSensorLabel, removeSensorLabel, notificationsEnabled, setNotificationsEnabled } = useSettingsStore();
  const { all: allReadings } = useSensors();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [editingSensor, setEditingSensor] = useState<string | null>(null);
  const [editLabel, setEditLabel] = useState('');
  const [notifPermission, setNotifPermission] = useState<NotificationPermission | 'unsupported'>('default');

  useEffect(() => {
    if (typeof window !== 'undefined' && 'Notification' in window) {
      setNotifPermission(Notification.permission);
    } else {
      setNotifPermission('unsupported');
    }
  }, []);

  const handleToggleNotifications = async () => {
    if (notificationsEnabled) {
      setNotificationsEnabled(false);
      return;
    }
    const perm = await requestNotificationPermission();
    setNotifPermission(perm);
    if (perm === 'granted') {
      setNotificationsEnabled(true);
    }
  };

  useEffect(() => {
    const fetchSettings = async () => {
      try {
        const s = await api.getSettings();
        setSettings(s);
      } catch {
        setSettings({
          sensor_poll_interval: 1.0,
          history_retention_hours: 24,
          temp_unit: 'C',
          hardware_backend: 'auto',
          backend_name: backendName || 'Unknown',
        });
      }
    };
    fetchSettings();
  }, [backendName]);

  useEffect(() => {
    const fetchLabels = async () => {
      try {
        const { labels } = await api.getSensorLabels();
        setSensorLabels(labels);
      } catch { /* non-critical */ }
    };
    fetchLabels();
  }, [setSensorLabels]);

  const handleSave = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      await api.updateSettings({
        sensor_poll_interval: settings.sensor_poll_interval,
        history_retention_hours: settings.history_retention_hours,
        temp_unit: settings.temp_unit,
      });
      setTempUnit(settings.temp_unit as TempUnit);
      setSaved(true);
      setTimeout(() => setSaved(false), 2000);
    } catch {
      // Handle error
    } finally {
      setSaving(false);
    }
  };

  const handleExport = async () => {
    try {
      const resp = await fetch(`${getApiBaseUrl()}/api/sensors/export?hours=24`, {
        credentials: 'include',
      });
      if (!resp.ok) {
        throw new Error(`Export failed: ${resp.status}`);
      }
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'drivechill_export.csv';
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      alert('Failed to export sensor data. Check your connection.');
    }
  };

  const handleSaveLabel = async (sensorId: string) => {
    const trimmed = editLabel.trim();
    if (!trimmed) return;
    try {
      await api.setSensorLabel(sensorId, trimmed);
      setSensorLabel(sensorId, trimmed);
    } catch { /* handle error */ }
    setEditingSensor(null);
  };

  const handleDeleteLabel = async (sensorId: string) => {
    try {
      await api.deleteSensorLabel(sensorId);
      removeSensorLabel(sensorId);
    } catch { /* may not exist — fine */ }
    setEditingSensor(null);
  };

  if (!settings) {
    return (
      <div className="card p-8 text-center animate-fade-in">
        <RefreshCw size={24} className="mx-auto mb-2 animate-spin" style={{ color: 'var(--text-secondary)' }} />
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>Loading settings...</p>
      </div>
    );
  }

  // Deduplicate sensors by ID for the labeling section
  const uniqueSensors = new Map<string, { id: string; name: string }>();
  for (const r of allReadings) {
    if (!uniqueSensors.has(r.id)) {
      uniqueSensors.set(r.id, { id: r.id, name: r.name });
    }
  }

  return (
    <div className="space-y-6 animate-fade-in max-w-2xl">
      {/* General settings */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>General</h3>

        <div className="space-y-4">
          <div>
            <label className="text-sm font-medium mb-1.5 block" style={{ color: 'var(--text)' }}>
              Polling Interval
            </label>
            <div className="flex items-center gap-3">
              <input
                type="range"
                min={0.5}
                max={5}
                step={0.5}
                value={settings.sensor_poll_interval}
                onChange={(e) => setSettings({ ...settings, sensor_poll_interval: Number(e.target.value) })}
                className="flex-1 accent-blue-500"
              />
              <span className="text-sm font-mono w-12 text-right" style={{ color: 'var(--text)' }}>
                {settings.sensor_poll_interval}s
              </span>
            </div>
            <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
              How often to read sensor data. Lower = more responsive, higher = less CPU usage.
            </p>
          </div>

          <div>
            <label className="text-sm font-medium mb-1.5 block" style={{ color: 'var(--text)' }}>
              Temperature Unit
            </label>
            <div className="flex gap-2">
              {['C', 'F'].map((unit) => (
                <button
                  key={unit}
                  onClick={() => setSettings({ ...settings, temp_unit: unit })}
                  className={`px-4 py-2 rounded-lg text-sm font-medium border transition-all ${
                    settings.temp_unit === unit ? 'ring-2' : ''
                  }`}
                  style={settings.temp_unit === unit
                    ? { borderColor: 'var(--accent)', background: 'var(--accent-muted)', color: 'var(--accent)' }
                    : { borderColor: 'var(--border)', color: 'var(--text-secondary)' }
                  }
                >
                  °{unit}
                </button>
              ))}
            </div>
          </div>

          <div>
            <label className="text-sm font-medium mb-1.5 block" style={{ color: 'var(--text)' }}>
              Data Retention
            </label>
            <select
              value={settings.history_retention_hours}
              onChange={(e) => setSettings({ ...settings, history_retention_hours: Number(e.target.value) })}
              className="px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            >
              <option value={1}>1 hour</option>
              <option value={6}>6 hours</option>
              <option value={24}>24 hours</option>
              <option value={168}>1 week</option>
              <option value={720}>30 days</option>
              <option value={8760}>1 year</option>
            </select>
            <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
              How long to keep historical sensor data in the database.
            </p>
          </div>
        </div>

        <div className="mt-6 flex items-center gap-3">
          <button
            onClick={handleSave}
            disabled={saving}
            className="btn-primary flex items-center gap-2 text-sm"
          >
            <Save size={14} />
            {saving ? 'Saving...' : saved ? 'Saved!' : 'Save Settings'}
          </button>
        </div>
      </div>

      {/* Browser Notifications */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Browser Notifications</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Get desktop notifications when temperature alerts trigger or safe-mode activates.
        </p>
        <div className="flex items-center gap-4">
          <button
            onClick={handleToggleNotifications}
            disabled={notifPermission === 'unsupported'}
            className="flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium border transition-all"
            style={notificationsEnabled
              ? { borderColor: 'var(--accent)', background: 'var(--accent-muted)', color: 'var(--accent)' }
              : { borderColor: 'var(--border)', color: 'var(--text-secondary)' }
            }
          >
            {notificationsEnabled ? <Bell size={14} /> : <BellOff size={14} />}
            {notificationsEnabled ? 'Enabled' : 'Disabled'}
          </button>
          {notifPermission === 'denied' && (
            <span className="text-xs" style={{ color: 'var(--danger)' }}>
              Blocked by browser. Allow notifications in your browser settings.
            </span>
          )}
          {notifPermission === 'unsupported' && (
            <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              Browser notifications are not supported in this environment.
            </span>
          )}
        </div>
      </div>

      {/* Sensor Labels */}
      {uniqueSensors.size > 0 && (
        <div className="card p-6 animate-card-enter">
          <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Sensor Labels</h3>
          <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
            Assign custom names to sensors. Labels are used in all views, charts, and alerts.
          </p>
          <div className="space-y-2">
            {Array.from(uniqueSensors.values()).map((sensor) => {
              const label = sensorLabels[sensor.id];
              const isEditing = editingSensor === sensor.id;

              return (
                <div
                  key={sensor.id}
                  className="flex items-center gap-3 px-3 py-2 rounded-lg"
                  style={{ background: 'var(--bg)' }}
                >
                  <span className="text-xs font-mono flex-shrink-0 w-32 truncate" style={{ color: 'var(--text-secondary)' }}>
                    {sensor.id}
                  </span>
                  {isEditing ? (
                    <>
                      <input
                        type="text"
                        value={editLabel}
                        onChange={(e) => setEditLabel(e.target.value)}
                        onKeyDown={(e) => { if (e.key === 'Enter') handleSaveLabel(sensor.id); if (e.key === 'Escape') setEditingSensor(null); }}
                        className="flex-1 px-2 py-1 rounded text-sm border outline-none focus:ring-1"
                        style={{ background: 'var(--card-bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                        autoFocus
                      />
                      <button onClick={() => handleSaveLabel(sensor.id)} className="p-1 rounded hover:bg-green-500/20">
                        <Check size={14} style={{ color: 'var(--success)' }} />
                      </button>
                      {label && (
                        <button onClick={() => handleDeleteLabel(sensor.id)} className="p-1 rounded hover:bg-red-500/20" title="Reset to default">
                          <X size={14} style={{ color: 'var(--danger)' }} />
                        </button>
                      )}
                      <button onClick={() => setEditingSensor(null)} className="p-1 rounded">
                        <X size={14} style={{ color: 'var(--text-secondary)' }} />
                      </button>
                    </>
                  ) : (
                    <>
                      <span className="flex-1 text-sm truncate" style={{ color: 'var(--text)' }}>
                        {label || sensor.name}
                      </span>
                      {label && (
                        <span className="text-xs px-1.5 py-0.5 rounded" style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}>
                          custom
                        </span>
                      )}
                      <button
                        onClick={() => { setEditingSensor(sensor.id); setEditLabel(label || sensor.name); }}
                        className="p-1 rounded hover:bg-surface-200 transition-colors"
                      >
                        <Pencil size={14} style={{ color: 'var(--text-secondary)' }} />
                      </button>
                    </>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* Data export */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>Data Export</h3>
        <p className="text-sm mb-4" style={{ color: 'var(--text-secondary)' }}>
          Export historical sensor data as CSV for analysis in spreadsheet tools.
        </p>
        <button onClick={handleExport} className="btn-secondary flex items-center gap-2 text-sm">
          <Download size={14} />
          Export Last 24 Hours
        </button>
      </div>

      {/* System info */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>System Info</h3>
        <div className="space-y-2 text-sm">
          <div className="flex justify-between">
            <span style={{ color: 'var(--text-secondary)' }}>Hardware Backend</span>
            <span className="font-medium" style={{ color: 'var(--text)' }}>{settings.backend_name}</span>
          </div>
          <div className="flex justify-between">
            <span style={{ color: 'var(--text-secondary)' }}>Backend Config</span>
            <span className="font-mono text-xs" style={{ color: 'var(--text)' }}>{settings.hardware_backend}</span>
          </div>
          <div className="flex justify-between">
            <span style={{ color: 'var(--text-secondary)' }}>Version</span>
            <span className="font-mono text-xs" style={{ color: 'var(--text)' }}>1.5.0</span>
          </div>
        </div>
      </div>

      {/* Help */}
      <div className="card p-4 flex items-start gap-3 animate-card-enter" style={{ background: 'var(--accent-muted)' }}>
        <Info size={16} className="mt-0.5 shrink-0" style={{ color: 'var(--accent)' }} />
        <div className="text-xs leading-relaxed" style={{ color: 'var(--text-secondary)' }}>
          <strong style={{ color: 'var(--text)' }}>Tip:</strong> For Windows, ensure LibreHardwareMonitor
          is running with the web server enabled (Settings &rarr; Web Server &rarr; Run).
          For Linux/Docker, make sure lm-sensors is installed and configured.
        </div>
      </div>
    </div>
  );
}
