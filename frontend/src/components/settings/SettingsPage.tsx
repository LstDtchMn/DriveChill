'use client';

import { useState, useEffect } from 'react';
import { api } from '@/lib/api';
import { useAppStore } from '@/stores/appStore';
import { Save, RefreshCw, Download, Info } from 'lucide-react';

interface AppSettings {
  sensor_poll_interval: number;
  history_retention_hours: number;
  temp_unit: string;
  hardware_backend: string;
  backend_name: string;
}

export function SettingsPage() {
  const { backendName } = useAppStore();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    const fetchSettings = async () => {
      try {
        const s = await api.getSettings();
        setSettings(s);
      } catch {
        // API not available, use defaults
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

  const handleSave = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      await api.updateSettings({
        sensor_poll_interval: settings.sensor_poll_interval,
        history_retention_hours: settings.history_retention_hours,
        temp_unit: settings.temp_unit,
      });
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
      const resp = await fetch('http://localhost:8085/api/sensors/export?hours=24');
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'drivechill_export.csv';
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      // Handle error
    }
  };

  if (!settings) {
    return (
      <div className="card p-8 text-center animate-fade-in">
        <RefreshCw size={24} className="mx-auto mb-2 animate-spin" style={{ color: 'var(--text-secondary)' }} />
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>Loading settings...</p>
      </div>
    );
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
            <span className="font-mono text-xs" style={{ color: 'var(--text)' }}>1.0.0</span>
          </div>
        </div>
      </div>

      {/* Help */}
      <div className="card p-4 flex items-start gap-3 animate-card-enter" style={{ background: 'var(--accent-muted)' }}>
        <Info size={16} className="mt-0.5 shrink-0" style={{ color: 'var(--accent)' }} />
        <div className="text-xs leading-relaxed" style={{ color: 'var(--text-secondary)' }}>
          <strong style={{ color: 'var(--text)' }}>Tip:</strong> For Windows, ensure LibreHardwareMonitor
          is running with the web server enabled (Settings → Web Server → Run).
          For Linux/Docker, make sure lm-sensors is installed and configured.
        </div>
      </div>
    </div>
  );
}
