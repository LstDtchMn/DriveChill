'use client';

import { useState, useEffect } from 'react';
import { api, getApiBaseUrl } from '@/lib/api';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useSensors } from '@/hooks/useSensors';
import type { TempUnit } from '@/lib/tempUnit';
import { requestNotificationPermission } from '@/hooks/useNotifications';
import type { ApiKeyInfo, MachineInfo, WebhookConfig, WebhookDelivery } from '@/lib/types';
import { Save, RefreshCw, Download, Info, Pencil, X, Check, Bell, BellOff } from 'lucide-react';

interface AppSettings {
  sensor_poll_interval: number;
  history_retention_hours: number;
  temp_unit: string;
  hardware_backend: string;
  backend_name: string;
}

function isValidHttpUrl(value: string): boolean {
  try {
    const u = new URL(value);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

export function SettingsPage() {
  const webhookPageSize = 10;
  const { backendName } = useAppStore();
  const { setTempUnit, sensorLabels, setSensorLabels, setSensorLabel, removeSensorLabel, notificationsEnabled, setNotificationsEnabled } = useSettingsStore();
  const { all: allReadings } = useSensors();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [editingSensor, setEditingSensor] = useState<string | null>(null);
  const [editLabel, setEditLabel] = useState('');
  const [notifPermission, setNotifPermission] = useState<NotificationPermission | 'unsupported'>('default');
  const [machines, setMachines] = useState<MachineInfo[]>([]);
  const [machineName, setMachineName] = useState('');
  const [machineUrl, setMachineUrl] = useState('');
  const [machineApiKey, setMachineApiKey] = useState('');
  const [machineAddBusy, setMachineAddBusy] = useState(false);
  const [busyMachineIds, setBusyMachineIds] = useState<Set<string>>(new Set());
  const [apiKeys, setApiKeys] = useState<ApiKeyInfo[]>([]);
  const [newApiKeyName, setNewApiKeyName] = useState('');
  const [issuedApiKey, setIssuedApiKey] = useState<string | null>(null);
  const [webhook, setWebhook] = useState<WebhookConfig | null>(null);
  const [webhookSaving, setWebhookSaving] = useState(false);
  const [webhookSecretInput, setWebhookSecretInput] = useState('');
  const [webhookDeliveries, setWebhookDeliveries] = useState<WebhookDelivery[]>([]);
  const [webhookDeliveriesOffset, setWebhookDeliveriesOffset] = useState(0);
  const [webhookDeliveriesLoading, setWebhookDeliveriesLoading] = useState(false);
  const [appVersion, setAppVersion] = useState('...');

  useEffect(() => {
    api.health().then((h) => setAppVersion(h.version || '?')).catch(() => setAppVersion('?'));
  }, []);

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

  const fetchMachines = async () => {
    try {
      const data = await api.getMachines();
      setMachines(data.machines);
    } catch {
      setMachines([]);
    }
  };

  const fetchApiKeys = async () => {
    try {
      const data = await api.listApiKeys();
      setApiKeys(data.api_keys);
    } catch {
      setApiKeys([]);
    }
  };

  const fetchWebhook = async () => {
    try {
      const data = await api.getWebhookConfig();
      setWebhook(data.config);
      setWebhookSecretInput('');
    } catch {
      setWebhook(null);
    }
  };

  const fetchWebhookDeliveries = async (offset = webhookDeliveriesOffset) => {
    setWebhookDeliveriesLoading(true);
    try {
      const data = await api.getWebhookDeliveries(webhookPageSize, offset);
      setWebhookDeliveries(data.deliveries);
      setWebhookDeliveriesOffset(offset);
    } catch {
      setWebhookDeliveries([]);
    } finally {
      setWebhookDeliveriesLoading(false);
    }
  };

  useEffect(() => {
    fetchMachines();
    fetchApiKeys();
    fetchWebhook();
    fetchWebhookDeliveries(0);
  }, []);

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
      alert('Failed to save settings.');
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
      setEditingSensor(null);
    } catch {
      alert('Failed to save sensor label.');
    }
  };

  const handleDeleteLabel = async (sensorId: string) => {
    try {
      await api.deleteSensorLabel(sensorId);
      removeSensorLabel(sensorId);
      setEditingSensor(null);
    } catch {
      alert('Failed to reset sensor label.');
    }
  };

  const handleAddMachine = async () => {
    const name = machineName.trim();
    const baseUrl = machineUrl.trim();
    if (!name || !baseUrl) return;
    if (!isValidHttpUrl(baseUrl)) {
      alert('Machine URL must be a valid http(s) URL.');
      return;
    }
    setMachineAddBusy(true);
    try {
      await api.createMachine({
        name,
        base_url: baseUrl,
        api_key: machineApiKey.trim() || undefined,
      });
      setMachineName('');
      setMachineUrl('');
      setMachineApiKey('');
      await fetchMachines();
    } catch {
      alert('Failed to add machine. Check URL and connectivity.');
    } finally {
      setMachineAddBusy(false);
    }
  };

  const handleDeleteMachine = async (machineId: string) => {
    if (!window.confirm('Remove this machine from registry?')) return;
    setBusyMachineIds((prev) => new Set(prev).add(machineId));
    try {
      await api.deleteMachine(machineId);
      await fetchMachines();
    } catch {
      alert('Failed to remove machine.');
    } finally {
      setBusyMachineIds((prev) => {
        const next = new Set(prev);
        next.delete(machineId);
        return next;
      });
    }
  };

  const handleVerifyMachine = async (machineId: string) => {
    setBusyMachineIds((prev) => new Set(prev).add(machineId));
    try {
      await api.verifyMachine(machineId);
      await fetchMachines();
    } catch {
      alert('Machine verification failed.');
    } finally {
      setBusyMachineIds((prev) => {
        const next = new Set(prev);
        next.delete(machineId);
        return next;
      });
    }
  };

  const handleCreateApiKey = async () => {
    const name = newApiKeyName.trim();
    if (!name) return;
    try {
      const data = await api.createApiKey(name);
      setIssuedApiKey(data.plaintext_key);
      setNewApiKeyName('');
      await fetchApiKeys();
    } catch {
      alert('Failed to create API key.');
    }
  };

  const handleRevokeApiKey = async (keyId: string) => {
    if (!window.confirm('Revoke this API key? This cannot be undone.')) return;
    try {
      await api.revokeApiKey(keyId);
      await fetchApiKeys();
    } catch {
      alert('Failed to revoke API key.');
    }
  };

  const handleSaveWebhook = async () => {
    if (!webhook) return;
    if (webhook.target_url && !isValidHttpUrl(webhook.target_url)) {
      alert('Webhook target URL must be a valid http(s) URL.');
      return;
    }
    setWebhookSaving(true);
    try {
      const payload = {
        ...webhook,
        signing_secret: webhookSecretInput || undefined,
      };
      const data = await api.updateWebhookConfig(payload);
      setWebhook(data.config);
      setWebhookSecretInput('');
    } catch {
      alert('Failed to save webhook settings.');
    } finally {
      setWebhookSaving(false);
    }
  };

  const handleCopyIssuedKey = async () => {
    if (!issuedApiKey) return;
    try {
      await navigator.clipboard.writeText(issuedApiKey);
    } catch {
      // no-op
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

  // Deduplicate sensors by ID for the labeling section
  const uniqueSensors = new Map<string, { id: string; name: string }>();
  for (const r of allReadings) {
    if (!uniqueSensors.has(r.id)) {
      uniqueSensors.set(r.id, { id: r.id, name: r.name });
    }
  }

  return (
    <div className="space-y-6 animate-fade-in max-w-none md:max-w-2xl">
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
                  className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-3 px-3 py-2 rounded-lg"
                  style={{ background: 'var(--bg)' }}
                >
                  <span className="text-xs font-mono flex-shrink-0 sm:w-32 truncate" style={{ color: 'var(--text-secondary)' }}>
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
                        aria-label={`Edit label for ${sensor.id}`}
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

      {/* Multi-machine hub registry */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Remote Machines</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Configure remote DriveChill agents for hub monitoring.
        </p>

        <div className="grid grid-cols-1 gap-3 mb-4">
          <input
            type="text"
            value={machineName}
            onChange={(e) => setMachineName(e.target.value)}
            placeholder="Display name (e.g. Server-01)"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <input
            type="text"
            value={machineUrl}
            onChange={(e) => setMachineUrl(e.target.value)}
            placeholder="Base URL (e.g. http://192.168.1.22:8085)"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <input
            type="password"
            value={machineApiKey}
            onChange={(e) => setMachineApiKey(e.target.value)}
            placeholder="API key (optional)"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <div className="flex justify-end">
            <button
              onClick={handleAddMachine}
              disabled={machineAddBusy || !machineName.trim() || !machineUrl.trim()}
              className="btn-primary text-sm px-4"
            >
              {machineAddBusy ? 'Saving...' : 'Add Machine'}
            </button>
          </div>
        </div>

        {machines.length === 0 ? (
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
            No remote machines configured yet.
          </p>
        ) : (
          <div className="space-y-2">
            {machines.map((machine) => (
              <div
                key={machine.id}
                className="flex items-center justify-between gap-3 rounded-lg px-3 py-2"
                style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}
              >
                <div className="min-w-0">
                  <p className="text-sm font-medium truncate" style={{ color: 'var(--text)' }}>
                    {machine.name}
                  </p>
                  <p className="text-xs truncate" style={{ color: 'var(--text-secondary)' }}>
                    {machine.base_url}
                  </p>
                  <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                    Status: {machine.status}
                    {machine.freshness_seconds != null ? ` - ${machine.freshness_seconds.toFixed(1)}s` : ''}
                  </p>
                </div>
                <button
                  onClick={() => handleVerifyMachine(machine.id)}
                  disabled={busyMachineIds.has(machine.id)}
                  className="btn-secondary text-xs px-3"
                >
                  Verify
                </button>
                <button
                  onClick={() => handleDeleteMachine(machine.id)}
                  disabled={busyMachineIds.has(machine.id)}
                  className="btn-secondary text-xs px-3"
                >
                  Remove
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* API keys */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>API Keys</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Generate keys for hub-to-agent machine authentication. New keys default to
          read-only sensor scope (`read:sensors`).
        </p>
        <div className="flex flex-col sm:flex-row gap-2 mb-3">
          <input
            type="text"
            value={newApiKeyName}
            onChange={(e) => setNewApiKeyName(e.target.value)}
            placeholder="Key name (e.g. Hub Main)"
            className="flex-1 px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <button onClick={handleCreateApiKey} className="btn-primary text-sm px-4">
            Create Key
          </button>
        </div>
        {issuedApiKey && (
          <div className="mb-3 p-2 rounded text-xs font-mono" style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}>
            Copy now (shown once): {issuedApiKey}
            <div className="mt-2 flex gap-2">
              <button onClick={handleCopyIssuedKey} className="btn-secondary text-xs px-2 py-1">Copy</button>
              <button onClick={() => setIssuedApiKey(null)} className="btn-secondary text-xs px-2 py-1">Dismiss</button>
            </div>
          </div>
        )}
        <div className="space-y-2">
          {apiKeys.length === 0 ? (
            <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>No API keys yet.</p>
          ) : apiKeys.map((k) => (
            <div key={k.id} className="flex items-center justify-between rounded-lg px-3 py-2" style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
              <div className="min-w-0">
                <p className="text-sm truncate" style={{ color: 'var(--text)' }}>{k.name}</p>
                <p className="text-xs truncate" style={{ color: 'var(--text-secondary)' }}>{k.key_prefix}...</p>
              </div>
              <button onClick={() => handleRevokeApiKey(k.id)} className="btn-secondary text-xs px-3">
                Revoke
              </button>
            </div>
          ))}
        </div>
      </div>

      {/* Webhooks */}
      <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Webhooks</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Send alert events to external systems (Slack/Discord/Home Assistant relay).
        </p>
        {webhook && (
          <div className="space-y-3">
            <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
              <input
                type="checkbox"
                checked={webhook.enabled}
                onChange={(e) => setWebhook({ ...webhook, enabled: e.target.checked })}
              />
              Enabled
            </label>
            <input
              type="text"
              value={webhook.target_url}
              onChange={(e) => setWebhook({ ...webhook, target_url: e.target.value })}
              placeholder="Target URL (https://...)"
              className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            />
            <input
              type="password"
              value={webhookSecretInput}
              onChange={(e) => setWebhookSecretInput(e.target.value)}
              placeholder="Signing secret (optional)"
              className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            />
            <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              {webhook.has_signing_secret ? 'A signing secret is already set.' : 'No signing secret set yet.'}
            </p>
            <button onClick={handleSaveWebhook} disabled={webhookSaving} className="btn-primary text-sm px-4">
              {webhookSaving ? 'Saving...' : 'Save Webhook'}
            </button>

            <div className="pt-2">
              <div className="mb-2 flex items-center justify-between">
                <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>Recent delivery attempts</p>
                <div className="flex gap-2">
                  <button
                    onClick={() => fetchWebhookDeliveries(Math.max(0, webhookDeliveriesOffset - webhookPageSize))}
                    disabled={webhookDeliveriesLoading || webhookDeliveriesOffset === 0}
                    className="btn-secondary text-xs px-2 py-1"
                  >
                    Previous
                  </button>
                  <button
                    onClick={() => fetchWebhookDeliveries(webhookDeliveriesOffset + webhookPageSize)}
                    disabled={webhookDeliveriesLoading || webhookDeliveries.length < webhookPageSize}
                    className="btn-secondary text-xs px-2 py-1"
                  >
                    Next
                  </button>
                  <button
                    onClick={() => fetchWebhookDeliveries(webhookDeliveriesOffset)}
                    disabled={webhookDeliveriesLoading}
                    className="btn-secondary text-xs px-2 py-1"
                  >
                    Refresh
                  </button>
                </div>
              </div>
              {webhookDeliveries.length === 0 ? (
                <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                  {webhookDeliveriesLoading ? 'Loading...' : 'No deliveries recorded yet.'}
                </p>
              ) : (
                <div className="space-y-2">
                  {webhookDeliveries.map((d, idx) => (
                    <div
                      key={`${d.timestamp}-${idx}`}
                      className="rounded-lg px-3 py-2 text-xs"
                      style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}
                    >
                      <div className="flex items-center justify-between gap-2">
                        <span style={{ color: 'var(--text)' }}>{d.event_type}</span>
                        <span style={{ color: d.success ? 'var(--success)' : 'var(--danger)' }}>
                          {d.success ? 'success' : 'failed'}
                        </span>
                      </div>
                      <p className="truncate" style={{ color: 'var(--text-secondary)' }}>
                        {d.target_url}
                      </p>
                      <p style={{ color: 'var(--text-secondary)' }}>
                        Attempt {d.attempt} | HTTP {d.http_status ?? '-'} | {d.latency_ms ?? '-'} ms
                      </p>
                      {d.error && (
                        <p className="truncate" style={{ color: 'var(--danger)' }}>
                          {d.error}
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}
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
            <span className="font-mono text-xs" style={{ color: 'var(--text)' }}>{appVersion}</span>
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
