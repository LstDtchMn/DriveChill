'use client';

import { useState, useEffect, useRef } from 'react';
import { api, authApi, getApiBaseUrl } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useSensors } from '@/hooks/useSensors';
import { closeWebSocket } from '@/hooks/useWebSocket';
import type { TempUnit } from '@/lib/tempUnit';
import { requestNotificationPermission } from '@/hooks/useNotifications';
import type { ApiKeyInfo, DriveSettings, MachineInfo, WebhookConfig, WebhookDelivery, PushSubscription, EmailNotificationSettings } from '@/lib/types';
import { NotificationChannelForm } from './NotificationChannelForm';
import { NoiseProfiler } from './NoiseProfiler';
import { ReportScheduleForm } from './ReportScheduleForm';
import { ProfileScheduleEditor } from './ProfileScheduleEditor';
import { Save, RefreshCw, Download, Upload, Info, Pencil, X, Check, Bell, BellOff, HardDrive, ArrowUpCircle } from 'lucide-react';
import { useConfirm } from '@/components/ui/ConfirmDialog';
import { useToast } from '@/components/ui/ToastProvider';
import { ViewerBanner } from '@/components/ui/ViewerBanner';

interface AppSettings {
  sensor_poll_interval: number;
  history_retention_hours: number;
  temp_unit: string;
  hardware_backend: string;
  backend_name: string;
  fan_ramp_rate_pct_per_sec: number;
}

function isValidHttpUrl(value: string): boolean {
  try {
    const u = new URL(value);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

/**
 * Application settings page.  Manages polling interval, retention, temp unit,
 * fan ramp rate, user accounts (RBAC), API keys, webhooks, push/email
 * notifications, drive monitoring settings, multi-machine hub, profile
 * schedules, noise profiling, report schedules, and config import/export.
 */
export function SettingsPage() {
  const confirm = useConfirm();
  const toast = useToast();
  const webhookPageSize = 10;
  const { role: currentRole } = useAuthStore();
  const isAdmin = currentRole === 'admin';
  const { backendName, updateCheck, setUpdateCheck } = useAppStore();
  const { setTempUnit, sensorLabels, setSensorLabels, setSensorLabel, removeSensorLabel, notificationsEnabled, setNotificationsEnabled } = useSettingsStore();
  const { all: allReadings } = useSensors();
  const [settings, setSettings] = useState<AppSettings | null>(null);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const savedTimerRef = useRef<ReturnType<typeof setTimeout>>();
  useEffect(() => () => { if (savedTimerRef.current) clearTimeout(savedTimerRef.current); }, []);
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
  const [newApiKeyScopes, setNewApiKeyScopes] = useState<Set<string>>(new Set());
  const [issuedApiKey, setIssuedApiKey] = useState<string | null>(null);
  const [users, setUsers] = useState<Array<{ id: number; username: string; role: string; created_at: string }>>([]);
  const [newUserName, setNewUserName] = useState('');
  const [newUserPassword, setNewUserPassword] = useState('');
  const [newUserRole, setNewUserRole] = useState<'admin' | 'viewer'>('viewer');
  const [webhook, setWebhook] = useState<WebhookConfig | null>(null);
  const [webhookSaving, setWebhookSaving] = useState(false);
  const [webhookSecretInput, setWebhookSecretInput] = useState('');
  const [webhookDeliveries, setWebhookDeliveries] = useState<WebhookDelivery[]>([]);
  const [webhookDeliveriesOffset, setWebhookDeliveriesOffset] = useState(0);
  const [webhookDeliveriesLoading, setWebhookDeliveriesLoading] = useState(false);
  const [appVersion, setAppVersion] = useState('...');
  const [pushSubs, setPushSubs] = useState<PushSubscription[]>([]);
  const [pushSubsBusy, setPushSubsBusy] = useState<Set<string>>(new Set());
  const [subscribing, _setSubscribing] = useState(false);
  const [emailSettings, setEmailSettings] = useState<EmailNotificationSettings | null>(null);
  const [emailPasswordInput, setEmailPasswordInput] = useState('');
  const [emailSaving, setEmailSaving] = useState(false);
  const [emailTestResult, setEmailTestResult] = useState<string | null>(null);
  const emailTestTimerRef = useRef<ReturnType<typeof setTimeout>>();
  useEffect(() => () => { if (emailTestTimerRef.current) clearTimeout(emailTestTimerRef.current); }, []);
  const [driveSettings, setDriveSettings] = useState<DriveSettings | null>(null);
  const [driveSaving, setDriveSaving] = useState(false);
  const [updateApplying, setUpdateApplying] = useState(false);
  const [updateMessage, setUpdateMessage] = useState<string | null>(null);
  const [exporting, setExporting] = useState(false);
  const [importing, setImporting] = useState(false);
  const [importResult, setImportResult] = useState<Record<string, number> | null>(null);

  type SettingsTab = 'general' | 'notifications' | 'automation' | 'security' | 'infrastructure';
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('general');

  const [myCurrentPw, setMyCurrentPw] = useState('');
  const [myNewPw, setMyNewPw] = useState('');
  const [myConfirmPw, setMyConfirmPw] = useState('');
  const [myPwBusy, setMyPwBusy] = useState(false);

  // Virtual sensors
  const [virtualSensors, setVirtualSensors] = useState<import('@/lib/types').VirtualSensor[]>([]);
  const [vsName, setVsName] = useState('');
  const [vsType, setVsType] = useState<import('@/lib/types').VirtualSensorType>('max');
  const [vsSourceIds, setVsSourceIds] = useState('');
  const [vsWeights, setVsWeights] = useState('');
  const [vsWindow, setVsWindow] = useState('');
  const [vsOffset, setVsOffset] = useState('0');
  const [vsEditId, setVsEditId] = useState<string | null>(null);
  const [vsBusy, setVsBusy] = useState(false);

  useEffect(() => {
    api.virtualSensors.list().then(r => setVirtualSensors(r.virtual_sensors)).catch(() => {});
  }, []);

  useEffect(() => {
    api.drives.getSettings().then(setDriveSettings).catch(() => {});
  }, []);

  async function handleSaveDriveSettings() {
    if (!driveSettings) return;
    setDriveSaving(true);
    try {
      const updated = await api.drives.updateSettings(driveSettings);
      setDriveSettings(updated);
    } catch (e: any) {
      toast(e?.message || 'Failed to save drive settings', 'error');
    } finally {
      setDriveSaving(false);
    }
  }

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
          history_retention_hours: 720,
          temp_unit: 'C',
          hardware_backend: 'auto',
          backend_name: backendName || 'Unknown',
          fan_ramp_rate_pct_per_sec: 0,
        });
      }
    };
    fetchSettings();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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

  const fetchUsers = async () => {
    try {
      const data = await authApi.listUsers();
      setUsers(data.users);
    } catch {
      setUsers([]);
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

  const fetchPushSubs = async () => {
    try {
      const data = await api.notifications.listPushSubscriptions();
      setPushSubs(data.subscriptions);
    } catch { setPushSubs([]); }
  };

  const fetchEmailSettings = async () => {
    try {
      const data = await api.notifications.getEmailSettings();
      setEmailSettings(data.settings);
    } catch { setEmailSettings(null); }
  };

  useEffect(() => {
    fetchMachines();
    fetchApiKeys();
    if (isAdmin) fetchUsers();
    fetchWebhook();
    fetchWebhookDeliveries(0);
    fetchPushSubs();
    fetchEmailSettings();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleSave = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      await api.updateSettings({
        sensor_poll_interval: settings.sensor_poll_interval,
        history_retention_hours: settings.history_retention_hours,
        temp_unit: settings.temp_unit,
        fan_ramp_rate_pct_per_sec: settings.fan_ramp_rate_pct_per_sec,
      });
      setTempUnit(settings.temp_unit as TempUnit);
      setSaved(true);
      savedTimerRef.current = setTimeout(() => setSaved(false), 2000);
    } catch {
      toast('Failed to save settings.', 'error');
    } finally {
      setSaving(false);
    }
  };

  const handleExport = async () => {
    try {
      const resp = await fetch(`${getApiBaseUrl()}/api/sensors/export?hours=24`, {
        credentials: 'include',
      });
      if (resp.status === 401) {
        window.dispatchEvent(new Event('drivechill:auth-expired'));
        return;
      }
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
      toast('Failed to export sensor data. Check your connection.', 'error');
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
      toast('Failed to save sensor label.', 'error');
    }
  };

  const handleDeleteLabel = async (sensorId: string) => {
    try {
      await api.deleteSensorLabel(sensorId);
      removeSensorLabel(sensorId);
      setEditingSensor(null);
    } catch {
      toast('Failed to reset sensor label.', 'error');
    }
  };

  const handleAddMachine = async () => {
    const name = machineName.trim();
    const baseUrl = machineUrl.trim();
    if (!name || !baseUrl) return;
    if (!isValidHttpUrl(baseUrl)) {
      toast('Machine URL must be a valid http(s) URL.', 'error');
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
      toast('Failed to add machine. Check URL and connectivity.', 'error');
    } finally {
      setMachineAddBusy(false);
    }
  };

  const handleDeleteMachine = async (machineId: string) => {
    if (!(await confirm('Remove this machine from registry?'))) return;
    setBusyMachineIds((prev) => new Set(prev).add(machineId));
    try {
      await api.deleteMachine(machineId);
      await fetchMachines();
    } catch {
      toast('Failed to remove machine.', 'error');
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
      const result = await api.verifyMachine(machineId);
      if (result.success === false) {
        toast(result.error || 'Machine verification failed.', 'error');
      }
      await fetchMachines();
    } catch {
      toast('Machine verification failed.', 'error');
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
      const scopes = newApiKeyScopes.size > 0 ? Array.from(newApiKeyScopes) : undefined;
      const data = await api.createApiKey(name, scopes);
      setIssuedApiKey(data.plaintext_key);
      setNewApiKeyName('');
      setNewApiKeyScopes(new Set());
      await fetchApiKeys();
    } catch {
      toast('Failed to create API key.', 'error');
    }
  };

  const handleRevokeApiKey = async (keyId: string) => {
    if (!(await confirm({ message: 'Revoke this API key? This cannot be undone.', danger: true }))) return;
    try {
      await api.revokeApiKey(keyId);
      await fetchApiKeys();
    } catch {
      toast('Failed to revoke API key.', 'error');
    }
  };

  const handleCreateUser = async () => {
    const name = newUserName.trim();
    if (!name || !newUserPassword) return;
    try {
      await authApi.createUser(name, newUserPassword, newUserRole);
      setNewUserName('');
      setNewUserPassword('');
      await fetchUsers();
      toast(`User "${name}" created.`, 'success');
    } catch {
      toast('Failed to create user.', 'error');
    }
  };

  const handleSetUserRole = async (userId: number, role: string) => {
    try {
      await authApi.setUserRole(userId, role);
      await fetchUsers();
    } catch {
      toast('Failed to update role.', 'error');
    }
  };

  const handleDeleteUser = async (userId: number, username: string) => {
    if (!(await confirm({ message: `Delete user "${username}"? This cannot be undone.`, danger: true }))) return;
    try {
      await authApi.deleteUser(userId);
      await fetchUsers();
      toast(`User "${username}" deleted.`, 'success');
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Failed to delete user.';
      toast(msg, 'error');
    }
  };

  const handleSaveWebhook = async () => {
    if (!webhook) return;
    if (webhook.target_url && !isValidHttpUrl(webhook.target_url)) {
      toast('Webhook target URL must be a valid http(s) URL.', 'error');
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
      toast('Failed to save webhook settings.', 'error');
    } finally {
      setWebhookSaving(false);
    }
  };

  const handleDeletePushSub = async (id: string) => {
    setPushSubsBusy((prev) => new Set(prev).add(id));
    try {
      await api.notifications.deletePushSubscription(id);
      await fetchPushSubs();
    } catch { toast('Failed to remove subscription.', 'error'); }
    finally {
      setPushSubsBusy((prev) => { const n = new Set(prev); n.delete(id); return n; });
    }
  };

  const handleTestPushSub = async (id: string) => {
    try {
      const r = await api.notifications.testPushSubscription(id);
      if (!r.success) toast('Test push failed — check server logs.', 'error');
      else toast('Test push sent!', 'success');
    } catch { toast('Test push failed.', 'error'); }
  };

  const handleSaveEmailSettings = async () => {
    if (!emailSettings) return;
    setEmailSaving(true);
    try {
      const payload: {
        enabled: boolean; smtp_host: string; smtp_port: number;
        smtp_username: string; sender_address: string; recipient_list: string[];
        use_tls: boolean; use_ssl: boolean; smtp_password?: string;
      } = {
        enabled: emailSettings.enabled,
        smtp_host: emailSettings.smtp_host,
        smtp_port: emailSettings.smtp_port,
        smtp_username: emailSettings.smtp_username,
        sender_address: emailSettings.sender_address,
        recipient_list: emailSettings.recipient_list,
        use_tls: emailSettings.use_tls,
        use_ssl: emailSettings.use_ssl,
        ...(emailPasswordInput ? { smtp_password: emailPasswordInput } : {}),
      };
      const data = await api.notifications.updateEmailSettings(payload);
      setEmailSettings(data.settings);
      setEmailPasswordInput('');
    } catch { toast('Failed to save email settings.', 'error'); }
    finally { setEmailSaving(false); }
  };

  const handleTestEmail = async () => {
    try {
      const r = await api.notifications.testEmail();
      setEmailTestResult(r.success ? 'Test email sent!' : (r.error || 'Send failed.'));
      if (emailTestTimerRef.current) clearTimeout(emailTestTimerRef.current);
      emailTestTimerRef.current = setTimeout(() => setEmailTestResult(null), 5000);
    } catch {
      setEmailTestResult('Failed to send test email.');
      if (emailTestTimerRef.current) clearTimeout(emailTestTimerRef.current);
      emailTestTimerRef.current = setTimeout(() => setEmailTestResult(null), 5000);
    }
  };

  const handleCopyIssuedKey = async () => {
    if (!issuedApiKey) return;
    try {
      await navigator.clipboard.writeText(issuedApiKey);
    } catch {
      // Fallback for non-HTTPS contexts where Clipboard API is unavailable
      try {
        const ta = document.createElement('textarea');
        ta.value = issuedApiKey;
        ta.style.position = 'fixed';
        ta.style.opacity = '0';
        document.body.appendChild(ta);
        ta.focus();
        ta.select();
        document.execCommand('copy');
        document.body.removeChild(ta);
      } catch {
        // Key is still visible in the panel above — user can select it manually
      }
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
      <ViewerBanner />

      {/* Settings tab bar */}
      <div className="flex gap-1 overflow-x-auto pb-1 -mb-2" style={{ scrollbarWidth: 'thin' }}>
        {([
          ['general', 'General'],
          ['notifications', 'Notifications'],
          ['automation', 'Automation'],
          ['security', 'Security'],
          ['infrastructure', 'Infrastructure'],
        ] as const).map(([key, label]) => (
          <button
            key={key}
            onClick={() => setSettingsTab(key)}
            className="px-4 py-2 text-sm font-medium rounded-lg whitespace-nowrap transition-colors"
            style={{
              background: settingsTab === key ? 'var(--accent)' : 'var(--card-bg)',
              color: settingsTab === key ? '#fff' : 'var(--text-secondary)',
              border: settingsTab === key ? 'none' : '1px solid var(--border)',
            }}
          >
            {label}
          </button>
        ))}
      </div>

      {/* General settings */}
      {settingsTab === 'general' && <div className="card p-6 animate-card-enter">
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
                  className={`px-4 py-2 rounded-lg text-sm font-medium border transition-all ${settings.temp_unit === unit ? 'ring-2' : ''
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
              Fan Speed Ramp Rate
            </label>
            <div className="flex items-center gap-3">
              <input
                type="range"
                min={0}
                max={50}
                step={1}
                value={settings.fan_ramp_rate_pct_per_sec}
                onChange={(e) => setSettings({ ...settings, fan_ramp_rate_pct_per_sec: Number(e.target.value) })}
                className="flex-1 accent-blue-500"
              />
              <span className="text-sm font-mono w-16 text-right" style={{ color: 'var(--text)' }}>
                {settings.fan_ramp_rate_pct_per_sec === 0 ? 'Off' : `${settings.fan_ramp_rate_pct_per_sec}%/s`}
              </span>
            </div>
            <p className="text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
              Limit how fast fan speeds change (0 = instant). Smooths transitions to reduce noise.
            </p>
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
            disabled={saving || !isAdmin}
            className="btn-primary flex items-center gap-2 text-sm disabled:opacity-50"
          >
            <Save size={14} />
            {saving ? 'Saving...' : saved ? 'Saved!' : 'Save Settings'}
          </button>
        </div>
      </div>}

      {/* Browser Notifications */}
      {settingsTab === 'notifications' && <div className="card p-6 animate-card-enter">
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
      </div>}

      {/* Web Push Notifications */}
      {settingsTab === 'notifications' && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Web Push Notifications</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Receive push notifications in this browser when alerts trigger. Requires a service worker and VAPID keys configured on the server.
        </p>
        <div className="flex items-center gap-3 mb-4">
          <button
            onClick={fetchPushSubs}
            disabled={subscribing}
            className="btn-secondary text-sm flex items-center gap-2"
          >
            <RefreshCw size={14} />
            Refresh subscriptions
          </button>
        </div>
        {pushSubs.length === 0 ? (
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>No push subscriptions registered.</p>
        ) : (
          <div className="space-y-2">
            {pushSubs.map((sub) => (
              <div
                key={sub.id}
                className="rounded-lg px-3 py-2"
                style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="text-xs font-mono truncate" style={{ color: 'var(--text)' }}>
                      {sub.endpoint.length > 60 ? sub.endpoint.slice(0, 60) + '…' : sub.endpoint}
                    </p>
                    {sub.user_agent && (
                      <p className="text-xs truncate" style={{ color: 'var(--text-secondary)' }}>{sub.user_agent}</p>
                    )}
                    <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                      Added {new Date(sub.created_at).toLocaleDateString()}
                    </p>
                  </div>
                  <div className="flex gap-2 shrink-0">
                    <button
                      onClick={() => handleTestPushSub(sub.id)}
                      disabled={pushSubsBusy.has(sub.id)}
                      className="btn-secondary text-xs px-2 py-1"
                    >
                      Test
                    </button>
                    <button
                      onClick={() => handleDeletePushSub(sub.id)}
                      disabled={pushSubsBusy.has(sub.id)}
                      className="btn-secondary text-xs px-2 py-1"
                    >
                      Remove
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>}

      {/* Email Notifications */}
      {settingsTab === 'notifications' && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>Email Notifications</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Send alert emails via SMTP when temperature thresholds are exceeded.
        </p>
        {emailSettings && (
          <div className="space-y-3">
            <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
              <input
                type="checkbox"
                checked={emailSettings.enabled}
                onChange={(e) => setEmailSettings({ ...emailSettings, enabled: e.target.checked })}
              />
              Enabled
            </label>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>SMTP Host</label>
              <input
                type="text"
                value={emailSettings.smtp_host}
                onChange={(e) => setEmailSettings({ ...emailSettings, smtp_host: e.target.value })}
                placeholder="smtp.example.com"
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>SMTP Port</label>
              <input
                type="number"
                value={emailSettings.smtp_port}
                onChange={(e) => setEmailSettings({ ...emailSettings, smtp_port: Number(e.target.value) })}
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>SMTP Username</label>
              <input
                type="text"
                value={emailSettings.smtp_username}
                onChange={(e) => setEmailSettings({ ...emailSettings, smtp_username: e.target.value })}
                placeholder="user@example.com"
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>
                SMTP Password{' '}
                <span style={{ color: emailSettings.has_password ? 'var(--success)' : 'var(--text-secondary)' }}>
                  ({emailSettings.has_password ? 'Password set' : 'No password set'})
                </span>
              </label>
              <input
                type="password"
                value={emailPasswordInput}
                onChange={(e) => setEmailPasswordInput(e.target.value)}
                placeholder="Leave blank to keep existing"
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Sender Address</label>
              <input
                type="text"
                value={emailSettings.sender_address}
                onChange={(e) => setEmailSettings({ ...emailSettings, sender_address: e.target.value })}
                placeholder="drivechill@example.com"
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium mb-1 block" style={{ color: 'var(--text-secondary)' }}>Recipients (comma-separated)</label>
              <input
                type="text"
                value={emailSettings.recipient_list.join(', ')}
                onChange={(e) => setEmailSettings({
                  ...emailSettings,
                  recipient_list: e.target.value.split(',').map((s) => s.trim()).filter(Boolean),
                })}
                placeholder="admin@example.com, ops@example.com"
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </div>
            <div className="flex gap-4">
              <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
                <input
                  type="checkbox"
                  checked={emailSettings.use_tls}
                  onChange={(e) => setEmailSettings({ ...emailSettings, use_tls: e.target.checked })}
                />
                STARTTLS (port 587)
              </label>
              <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
                <input
                  type="checkbox"
                  checked={emailSettings.use_ssl}
                  onChange={(e) => setEmailSettings({ ...emailSettings, use_ssl: e.target.checked })}
                />
                Implicit TLS (port 465)
              </label>
            </div>
            <div className="flex items-center gap-3 pt-1">
              <button
                onClick={handleSaveEmailSettings}
                disabled={emailSaving || !isAdmin}
                className="btn-primary text-sm px-4 disabled:opacity-50"
              >
                {emailSaving ? 'Saving...' : 'Save Email Settings'}
              </button>
              <button
                onClick={handleTestEmail}
                disabled={!isAdmin}
                className="btn-secondary text-sm px-4 disabled:opacity-50"
              >
                Send Test Email
              </button>
            </div>
            {emailTestResult && (
              <p
                className="text-xs"
                style={{ color: emailTestResult.toLowerCase().includes('sent') ? 'var(--success)' : 'var(--danger)' }}
              >
                {emailTestResult}
              </p>
            )}
          </div>
        )}
        {!emailSettings && (
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
            Email settings unavailable — check server connectivity.
          </p>
        )}
      </div>}

      {/* Sensor Labels */}
      {settingsTab === 'general' && uniqueSensors.size > 0 && (
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
                      <button onClick={() => handleSaveLabel(sensor.id)} className="p-1 rounded hover:bg-green-500/20" aria-label="Save label">
                        <Check size={14} style={{ color: 'var(--success)' }} />
                      </button>
                      {label && (
                        <button onClick={() => handleDeleteLabel(sensor.id)} className="p-1 rounded hover:bg-red-500/20" aria-label="Reset to default">
                          <X size={14} style={{ color: 'var(--danger)' }} />
                        </button>
                      )}
                      <button onClick={() => setEditingSensor(null)} className="p-1 rounded" aria-label="Cancel editing">
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
      {settingsTab === 'infrastructure' && <div className="card p-6 animate-card-enter">
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
              disabled={machineAddBusy || !machineName.trim() || !machineUrl.trim() || !isAdmin}
              className="btn-primary text-sm px-4 disabled:opacity-50"
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
                  disabled={busyMachineIds.has(machine.id) || !isAdmin}
                  className="btn-secondary text-xs px-3 disabled:opacity-50"
                >
                  Verify
                </button>
                <button
                  onClick={() => handleDeleteMachine(machine.id)}
                  disabled={busyMachineIds.has(machine.id) || !isAdmin}
                  className="btn-secondary text-xs px-3 disabled:opacity-50"
                >
                  Remove
                </button>
              </div>
            ))}
          </div>
        )}
      </div>}

      {/* API keys — admin only */}
      {settingsTab === 'security' && isAdmin && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-2" style={{ color: 'var(--text)' }}>API Keys</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Generate keys for machine-to-machine access. Select scopes below — leave all unchecked for a read-only <code>read:sensors</code> key.
        </p>
        <div className="flex flex-col gap-3 mb-3">
          <input
            type="text"
            value={newApiKeyName}
            onChange={(e) => setNewApiKeyName(e.target.value)}
            placeholder="Key name (e.g. Hub Main)"
            className="px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          {/* Scope picker */}
          <div className="rounded-lg p-3" style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
            <p className="text-xs font-medium mb-2" style={{ color: 'var(--text-secondary)' }}>Scopes — leave all unchecked for default <code>read:sensors</code></p>
            <div className="grid grid-cols-2 sm:grid-cols-3 gap-x-4 gap-y-1">
              {/* Full-access wildcard */}
              <label key="*" className="flex items-center gap-1.5 cursor-pointer col-span-full">
                <input
                  type="checkbox"
                  checked={newApiKeyScopes.has('*')}
                  onChange={(e) => {
                    const next = new Set(newApiKeyScopes);
                    if (e.target.checked) {
                      next.clear();
                      next.add('*');
                    } else {
                      next.delete('*');
                    }
                    setNewApiKeyScopes(next);
                  }}
                  className="rounded"
                />
                <span className="text-xs font-medium" style={{ color: 'var(--accent)' }}>* (full access — all domains)</span>
              </label>
              {/* Per-domain scopes (disabled when * is selected) */}
              {(['alerts','analytics','auth','drives','fans','machines','notifications','profiles','quiet_hours','sensors','settings','temperature_targets','webhooks'] as const).flatMap(domain =>
                (['read','write'] as const).map(action => {
                  const scope = `${action}:${domain}`;
                  return (
                    <label key={scope} className="flex items-center gap-1.5 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={newApiKeyScopes.has(scope)}
                        disabled={newApiKeyScopes.has('*')}
                        onChange={(e) => {
                          const next = new Set(newApiKeyScopes);
                          e.target.checked ? next.add(scope) : next.delete(scope);
                          setNewApiKeyScopes(next);
                        }}
                        className="rounded"
                      />
                      <span className="text-xs" style={{ color: newApiKeyScopes.has('*') ? 'var(--text-secondary)' : 'var(--text)' }}>{scope}</span>
                    </label>
                  );
                })
              )}
            </div>
          </div>
          <button onClick={handleCreateApiKey} className="btn-primary text-sm px-4 self-start">
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
            <div key={k.id} className="rounded-lg px-3 py-2" style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
              <div className="flex items-center justify-between gap-2">
                <div className="min-w-0 flex items-center gap-2">
                  <p className="text-sm truncate" style={{ color: 'var(--text)' }}>{k.name}</p>
                  <span
                    className={`badge ${k.role === 'viewer' ? '' : 'badge-success'}`}
                    style={k.role === 'viewer' ? { background: 'var(--surface-200)', color: 'var(--text-secondary)' } : undefined}
                  >
                    {k.role ?? 'admin'}
                  </span>
                </div>
                <button onClick={() => handleRevokeApiKey(k.id)} className="btn-secondary text-xs px-3 shrink-0">
                  Revoke
                </button>
              </div>
              <p className="text-xs mt-0.5" style={{ color: 'var(--text-secondary)' }}>{k.key_prefix}...</p>
              {k.scopes && k.scopes.length > 0 && (
                <div className="flex flex-wrap gap-1 mt-1.5">
                  {k.scopes.map(s => (
                    <span key={s} className="badge text-xs" style={{ background: 'var(--surface-200)', color: 'var(--text-secondary)', fontSize: '0.65rem' }}>
                      {s}
                    </span>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>}

      {/* Change My Password */}
      {settingsTab === 'security' && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>
          Change My Password
        </h3>
        <div className="space-y-3">
          <input
            type="password"
            autoComplete="current-password"
            value={myCurrentPw}
            onChange={(e) => setMyCurrentPw(e.target.value)}
            placeholder="Current password"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <input
            type="password"
            autoComplete="new-password"
            value={myNewPw}
            onChange={(e) => setMyNewPw(e.target.value)}
            placeholder="New password (min 8 chars)"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          <input
            type="password"
            autoComplete="new-password"
            value={myConfirmPw}
            onChange={(e) => setMyConfirmPw(e.target.value)}
            placeholder="Confirm new password"
            className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
            style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
          />
          {myNewPw && myConfirmPw && myNewPw !== myConfirmPw && (
            <p style={{ color: 'var(--danger)', fontSize: '12px', margin: '2px 0 0' }}>Passwords do not match</p>
          )}
          <button
            className="btn-primary text-xs px-4 py-2"
            disabled={myPwBusy || !myCurrentPw || myNewPw.length < 8 || myNewPw !== myConfirmPw}
            onClick={async () => {
              setMyPwBusy(true);
              try {
                await authApi.changeMyPassword(myCurrentPw, myNewPw);
                toast('Password changed successfully.');
                closeWebSocket();
                const session = await authApi.checkSession();
                if (session) {
                  useAuthStore.getState().setAuth(
                    session.auth_required,
                    session.authenticated,
                    session.username,
                    session.role,
                  );
                }
              } catch (err: any) {
                toast(err?.message || 'Password change failed.', 'error');
              } finally {
                setMyCurrentPw('');
                setMyNewPw('');
                setMyConfirmPw('');
                setMyPwBusy(false);
              }
            }}
          >
            {myPwBusy ? 'Changing...' : 'Change Password'}
          </button>
        </div>
      </div>}

      {/* User Management (admin only) */}
      {settingsTab === 'security' && isAdmin && (
        <div className="card p-6 animate-card-enter">
          <h3 className="text-base font-semibold mb-1" style={{ color: 'var(--text)' }}>User Management</h3>
          <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
            Create and manage users. Viewers have read-only access.
          </p>
          <div className="space-y-2 mb-4">
            {users.length === 0 ? (
              <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>No users found.</p>
            ) : users.map((u) => (
              <div key={u.id} className="flex items-center justify-between rounded-lg px-3 py-2" style={{ background: 'var(--bg)', border: '1px solid var(--border)' }}>
                <div className="min-w-0 flex items-center gap-2">
                  <p className="text-sm truncate" style={{ color: 'var(--text)' }}>{u.username}</p>
                  <span className={`badge ${u.role === 'admin' ? 'badge-success' : ''}`} style={u.role !== 'admin' ? { background: 'var(--surface-200)', color: 'var(--text-secondary)' } : undefined}>
                    {u.role}
                  </span>
                </div>
                <div className="flex items-center gap-2">
                  <select
                    value={u.role}
                    onChange={(e) => handleSetUserRole(u.id, e.target.value)}
                    className="text-xs rounded border px-1 py-1 outline-none"
                    style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                  >
                    <option value="admin">admin</option>
                    <option value="viewer">viewer</option>
                  </select>
                  <button onClick={() => handleDeleteUser(u.id, u.username)} className="btn-secondary text-xs px-2 py-1" style={{ color: 'var(--danger)' }}>
                    Delete
                  </button>
                </div>
              </div>
            ))}
          </div>
          <div className="flex flex-col sm:flex-row gap-2">
            <input
              type="text"
              value={newUserName}
              onChange={(e) => setNewUserName(e.target.value)}
              placeholder="Username"
              className="flex-1 px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            />
            <input
              type="password"
              value={newUserPassword}
              onChange={(e) => setNewUserPassword(e.target.value)}
              placeholder="Password (min 8 chars)"
              className="flex-1 px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            />
            <select
              value={newUserRole}
              onChange={(e) => setNewUserRole(e.target.value as 'admin' | 'viewer')}
              className="px-3 py-2 rounded-lg text-sm border outline-none"
              style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
            >
              <option value="admin">admin</option>
              <option value="viewer">viewer</option>
            </select>
            <button onClick={handleCreateUser} disabled={!newUserName.trim() || newUserPassword.length < 8} className="btn-primary text-sm px-4 disabled:opacity-50">
              Create User
            </button>
          </div>
        </div>
      )}

      {/* Webhooks — admin only */}
      {settingsTab === 'notifications' && isAdmin && <div className="card p-6 animate-card-enter">
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
      </div>}

      {/* Data export */}
      {settingsTab === 'infrastructure' && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>Data Export</h3>
        <p className="text-sm mb-4" style={{ color: 'var(--text-secondary)' }}>
          Export historical sensor data as CSV for analysis in spreadsheet tools.
        </p>
        <button onClick={handleExport} className="btn-secondary flex items-center gap-2 text-sm">
          <Download size={14} />
          Export Last 24 Hours
        </button>
      </div>}

      {/* System info */}
      {settingsTab === 'general' && <div className="card p-6 animate-card-enter">
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
      </div>}

      {/* Storage Monitoring */}
      {settingsTab === 'infrastructure' && <div className="card p-6 animate-card-enter">
        <div className="flex items-center gap-2 mb-1">
          <HardDrive size={16} style={{ color: 'var(--accent)' }} />
          <h3 className="text-base font-semibold" style={{ color: 'var(--text)' }}>Storage Monitoring</h3>
        </div>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Drive health, temperature, and SMART monitoring via smartmontools.
        </p>
        {driveSettings ? (
          <div className="space-y-4">
            <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
              <input
                type="checkbox"
                checked={driveSettings.enabled}
                onChange={(e) => setDriveSettings({ ...driveSettings, enabled: e.target.checked })}
              />
              Enable drive monitoring
            </label>
            <label className="flex items-center gap-2 text-sm" style={{ color: 'var(--text)' }}>
              <input
                type="checkbox"
                checked={driveSettings.smartctl_provider_enabled}
                onChange={(e) => setDriveSettings({ ...driveSettings, smartctl_provider_enabled: e.target.checked })}
              />
              Use smartctl (recommended)
            </label>
            <div>
              <label className="block text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>smartctl path</label>
              <input
                type="text"
                value={driveSettings.smartctl_path}
                onChange={(e) => setDriveSettings({ ...driveSettings, smartctl_path: e.target.value })}
                className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                placeholder="smartctl"
              />
            </div>
            <div className="grid grid-cols-2 gap-3">
              {([
                ['HDD warn °C', 'hdd_temp_warning_c'],
                ['HDD crit °C', 'hdd_temp_critical_c'],
                ['SSD warn °C', 'ssd_temp_warning_c'],
                ['SSD crit °C', 'ssd_temp_critical_c'],
                ['NVMe warn °C', 'nvme_temp_warning_c'],
                ['NVMe crit °C', 'nvme_temp_critical_c'],
              ] as [string, keyof DriveSettings][]).map(([label, key]) => (
                <div key={key}>
                  <label className="block text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>{label}</label>
                  <input
                    type="number"
                    value={driveSettings[key] as number}
                    onChange={(e) => setDriveSettings({ ...driveSettings, [key]: Number(e.target.value) })}
                    className="w-full px-3 py-2 rounded-lg text-sm border outline-none"
                    style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
                  />
                </div>
              ))}
            </div>
            <button
              onClick={handleSaveDriveSettings}
              disabled={driveSaving || !isAdmin}
              className="btn-primary flex items-center gap-2 disabled:opacity-50"
            >
              <Save size={14} />
              {driveSaving ? 'Saving…' : 'Save Drive Settings'}
            </button>
          </div>
        ) : (
          <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>Loading…</p>
        )}
      </div>}

      {/* Updates */}
      {settingsTab === 'general' && <div className="card p-5 animate-card-enter">
        <h3 className="section-title mb-4 flex items-center gap-2">
          <ArrowUpCircle size={16} />
          Updates
        </h3>
        {updateCheck ? (
          <div className="space-y-3">
            <div className="flex flex-wrap gap-4 text-sm">
              <span style={{ color: 'var(--text-secondary)' }}>
                Current: <strong style={{ color: 'var(--text)' }}>v{updateCheck.current}</strong>
              </span>
              <span style={{ color: 'var(--text-secondary)' }}>
                Latest:{' '}
                {isValidHttpUrl(updateCheck.release_url ?? '') ? (
                  <a
                    href={updateCheck.release_url}
                    target="_blank"
                    rel="noopener noreferrer"
                    style={{ color: 'var(--accent)' }}
                  >
                    v{updateCheck.latest}
                  </a>
                ) : (
                  <strong style={{ color: 'var(--accent)' }}>v{updateCheck.latest}</strong>
                )}
              </span>
              <span>
                {updateCheck.update_available ? (
                  <span className="badge badge-warning">Update available</span>
                ) : (
                  <span className="badge badge-success">Up to date</span>
                )}
              </span>
            </div>

            {updateMessage && (
              <div
                className="p-3 rounded-lg text-xs font-mono whitespace-pre-wrap"
                style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text-secondary)' }}
              >
                {updateMessage}
              </div>
            )}

            {updateCheck.update_available && (
              updateCheck.deployment === 'docker' ? (
                <div className="space-y-2">
                  <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                    Pull the new image and restart your container:
                  </p>
                  <div className="flex items-center gap-2">
                    <code
                      className="flex-1 px-3 py-2 rounded-lg text-xs font-mono"
                      style={{ background: 'var(--bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
                    >
                      docker compose pull &amp;&amp; docker compose up -d
                    </code>
                    <button
                      className="btn-secondary text-xs px-3 py-2"
                      onClick={() => {
                        navigator.clipboard.writeText('docker compose pull && docker compose up -d');
                        setUpdateMessage('Copied to clipboard.');
                        setTimeout(() => setUpdateMessage(null), 2000);
                      }}
                    >
                      Copy
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  className="btn-primary flex items-center gap-2 disabled:opacity-50"
                  disabled={updateApplying || !isAdmin}
                  onClick={async () => {
                    if (!(await confirm(`Update DriveChill to v${updateCheck.latest}?\n\nThe service will stop and restart automatically. You will need to reconnect in ~30 seconds.`))) return;
                    setUpdateApplying(true);
                    setUpdateMessage(null);
                    try {
                      const result = await api.update.apply();
                      setUpdateMessage(result.message ?? 'Update started.');
                      if (result.status === 'update_started') {
                        setUpdateCheck({ ...updateCheck, update_available: false });
                      }
                    } catch (e: any) {
                      const body = e?.detail;
                      const msg = (body && typeof body === 'object' && typeof body.detail === 'string')
                        ? body.detail
                        : e?.message ?? 'Unknown error';
                      setUpdateMessage(`Error: ${msg}`);
                    } finally {
                      setUpdateApplying(false);
                    }
                  }}
                >
                  <ArrowUpCircle size={14} />
                  {updateApplying ? 'Starting update…' : `Update to v${updateCheck.latest}`}
                </button>
              )
            )}
          </div>
        ) : (
          <div className="flex items-center gap-3">
            <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
              Checking for updates…
            </p>
            <button
              className="btn-secondary text-xs px-3 py-1.5 flex items-center gap-1.5"
              onClick={async () => {
                try {
                  const info = await api.update.check();
                  setUpdateCheck(info);
                } catch { /* ignore */ }
              }}
            >
              <RefreshCw size={12} />
              Retry
            </button>
          </div>
        )}
      </div>}

      {/* Notification Channels */}
      {settingsTab === 'notifications' && <NotificationChannelForm isAdmin={isAdmin} toast={toast} confirm={confirm} />}

      {/* Virtual Sensors */}
      {settingsTab === 'automation' && <div className="card p-6 animate-card-enter">
        <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>Virtual Sensors</h3>
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
          Create computed sensors from real sensor readings. Virtual sensor IDs can be used
          anywhere a real sensor ID is accepted (fan curves, temperature targets, alerts).
        </p>

        {/* List existing */}
        {virtualSensors.length > 0 && (
          <div className="space-y-2 mb-4">
            {virtualSensors.map(vs => (
              <div key={vs.id} className="flex items-center justify-between p-3 rounded text-xs"
                style={{ background: 'var(--surface-200)', color: 'var(--text)' }}>
                <div className="flex-1 min-w-0">
                  <div className="font-medium">{vs.name}</div>
                  <div style={{ color: 'var(--text-secondary)' }}>
                    <span className="badge" style={{ fontSize: '0.65rem' }}>{vs.type}</span>
                    {' '}← {vs.source_ids.join(', ')}
                    {vs.offset !== 0 && <span> (offset: {vs.offset})</span>}
                    {vs.type === 'moving_avg' && vs.window_seconds && <span> (window: {vs.window_seconds}s)</span>}
                    {vs.type === 'weighted' && vs.weights && <span> (weights: {vs.weights.join(', ')})</span>}
                  </div>
                  <div style={{ color: 'var(--text-secondary)', opacity: 0.7 }}>ID: {vs.id}</div>
                </div>
                <div className="flex items-center gap-2 ml-2 shrink-0">
                  {isAdmin && (
                    <>
                      <button className="btn-secondary text-xs px-2 py-1"
                        onClick={() => {
                          setVsEditId(vs.id);
                          setVsName(vs.name);
                          setVsType(vs.type);
                          setVsSourceIds(vs.source_ids.join(', '));
                          setVsWeights(vs.weights?.join(', ') || '');
                          setVsWindow(vs.window_seconds?.toString() || '');
                          setVsOffset(vs.offset.toString());
                        }}>
                        <Pencil size={12} />
                      </button>
                      <button className="btn-secondary text-xs px-2 py-1"
                        style={{ color: 'var(--danger)' }}
                        onClick={async () => {
                          const confirmed = await confirm(`Delete virtual sensor "${vs.name}"?`);
                          if (!confirmed) return;
                          try {
                            await api.virtualSensors.delete(vs.id);
                            setVirtualSensors(prev => prev.filter(v => v.id !== vs.id));
                            toast('Virtual sensor deleted.');
                          } catch (err: any) {
                            toast(err?.message || 'Delete failed.', 'error');
                          }
                        }}>
                        <X size={12} />
                      </button>
                    </>
                  )}
                </div>
              </div>
            ))}
          </div>
        )}

        {/* Create/Edit form — admin only */}
        {isAdmin && (
          <div className="space-y-3 p-4 rounded" style={{ background: 'var(--surface-200)' }}>
            <div className="text-xs font-medium" style={{ color: 'var(--text)' }}>
              {vsEditId ? 'Edit Virtual Sensor' : 'Create Virtual Sensor'}
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div>
                <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Name</label>
                <input className="w-full p-2 rounded text-xs" style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                  value={vsName} onChange={e => setVsName(e.target.value)} placeholder="CPU Max (all cores)" />
              </div>
              <div>
                <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Type</label>
                <select className="w-full p-2 rounded text-xs" style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                  value={vsType} onChange={e => setVsType(e.target.value as any)}>
                  <option value="max">MAX</option>
                  <option value="min">MIN</option>
                  <option value="avg">AVG</option>
                  <option value="weighted">Weighted</option>
                  <option value="delta">Delta (A − B)</option>
                  <option value="moving_avg">Moving Average</option>
                </select>
              </div>
            </div>
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
                Source Sensor IDs <span style={{ opacity: 0.6 }}>(comma-separated)</span>
              </label>
              <input className="w-full p-2 rounded text-xs" style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={vsSourceIds} onChange={e => setVsSourceIds(e.target.value)}
                placeholder="cpu_temp_0, cpu_temp_1, cpu_temp_2" />
            </div>
            {vsType === 'weighted' && (
              <div>
                <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
                  Weights <span style={{ opacity: 0.6 }}>(comma-separated, same order as source IDs)</span>
                </label>
                <input className="w-full p-2 rounded text-xs" style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                  value={vsWeights} onChange={e => setVsWeights(e.target.value)} placeholder="1.0, 0.5, 0.5" />
              </div>
            )}
            {vsType === 'moving_avg' && (
              <div>
                <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
                  Window (seconds)
                </label>
                <input className="w-full p-2 rounded text-xs" type="number" min="1" step="1"
                  style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                  value={vsWindow} onChange={e => setVsWindow(e.target.value)} placeholder="30" />
              </div>
            )}
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
                Offset <span style={{ opacity: 0.6 }}>(added after computation)</span>
              </label>
              <input className="w-full p-2 rounded text-xs" type="number" step="0.1"
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={vsOffset} onChange={e => setVsOffset(e.target.value)} />
            </div>

            {/* Formula preview */}
            <div className="text-xs p-2 rounded" style={{ background: 'var(--bg)', color: 'var(--text-secondary)', fontFamily: 'monospace' }}>
              {(() => {
                const ids = vsSourceIds.split(',').map(s => s.trim()).filter(Boolean);
                const off = parseFloat(vsOffset) || 0;
                const offStr = off !== 0 ? ` + ${off}` : '';
                if (ids.length === 0) return 'Enter source sensor IDs to see formula preview';
                if (vsType === 'delta') return ids.length >= 2 ? `${ids[0]} − ${ids[1]}${offStr}` : 'Delta requires exactly 2 sources';
                if (vsType === 'weighted') {
                  const w = vsWeights.split(',').map(s => parseFloat(s.trim())).filter(n => !isNaN(n));
                  if (w.length === ids.length) return `(${ids.map((id, i) => `${id}×${w[i]}`).join(' + ')}) / ${w.reduce((a, b) => a + b, 0)}${offStr}`;
                  return `avg(${ids.join(', ')})${offStr}  ← weights needed`;
                }
                if (vsType === 'moving_avg') return `EMA(avg(${ids.join(', ')}), window=${vsWindow || '30'}s)${offStr}`;
                return `${vsType}(${ids.join(', ')})${offStr}`;
              })()}
            </div>

            <div className="flex gap-2">
              <button className="btn-primary text-xs px-4 py-2" disabled={vsBusy || !vsName.trim() || !vsSourceIds.trim()}
                onClick={async () => {
                  setVsBusy(true);
                  try {
                    const sourceIds = vsSourceIds.split(',').map(s => s.trim()).filter(Boolean);
                    const weights = vsWeights ? vsWeights.split(',').map(s => parseFloat(s.trim())).filter(n => !isNaN(n)) : undefined;
                    const body: import('@/lib/types').VirtualSensorRequest = {
                      name: vsName.trim(),
                      type: vsType,
                      source_ids: sourceIds,
                      weights: weights && weights.length > 0 ? weights : undefined,
                      window_seconds: vsWindow ? parseFloat(vsWindow) : undefined,
                      offset: parseFloat(vsOffset) || 0,
                      enabled: true,
                    };
                    if (vsEditId) {
                      await api.virtualSensors.update(vsEditId, body);
                      toast('Virtual sensor updated.');
                    } else {
                      await api.virtualSensors.create(body);
                      toast('Virtual sensor created.');
                    }
                    // Refresh list
                    const r = await api.virtualSensors.list();
                    setVirtualSensors(r.virtual_sensors);
                    // Reset form
                    setVsEditId(null); setVsName(''); setVsType('max');
                    setVsSourceIds(''); setVsWeights(''); setVsWindow(''); setVsOffset('0');
                  } catch (err: any) {
                    toast(err?.message || 'Save failed.', 'error');
                  } finally {
                    setVsBusy(false);
                  }
                }}>
                {vsBusy ? 'Saving...' : vsEditId ? 'Update' : 'Create'}
              </button>
              {vsEditId && (
                <button className="btn-secondary text-xs px-4 py-2"
                  onClick={() => {
                    setVsEditId(null); setVsName(''); setVsType('max');
                    setVsSourceIds(''); setVsWeights(''); setVsWindow(''); setVsOffset('0');
                  }}>
                  Cancel
                </button>
              )}
            </div>
          </div>
        )}
      </div>}

      {/* Import / Export — admin only */}
      {settingsTab === 'infrastructure' && isAdmin && (
        <div className="card p-6 animate-card-enter">
          <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>Import / Export</h3>
          <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
            Export your entire configuration (profiles, alert rules, temperature targets, quiet hours,
            webhook settings, sensor labels, and app settings) as a JSON file, or import a previously
            exported file to restore or clone a configuration.
          </p>
          <div className="flex flex-wrap gap-3">
            <button
              className="btn-primary text-sm px-4 py-2 flex items-center gap-2"
              disabled={exporting}
              onClick={async () => {
                setExporting(true);
                try {
                  const data = await api.exportConfig();
                  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
                  const url = URL.createObjectURL(blob);
                  const a = document.createElement('a');
                  const date = new Date().toISOString().slice(0, 10);
                  a.href = url;
                  a.download = `drivechill-config-${date}.json`;
                  document.body.appendChild(a);
                  a.click();
                  document.body.removeChild(a);
                  URL.revokeObjectURL(url);
                  toast('Configuration exported successfully.');
                } catch (err: any) {
                  toast(err?.message || 'Export failed.', 'error');
                } finally {
                  setExporting(false);
                }
              }}
            >
              <Download size={14} />
              {exporting ? 'Exporting...' : 'Export Config'}
            </button>

            <button
              className="btn-secondary text-sm px-4 py-2 flex items-center gap-2"
              disabled={importing}
              onClick={() => {
                const input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,application/json';
                input.onchange = async () => {
                  const file = input.files?.[0];
                  if (!file) return;
                  setImporting(true);
                  setImportResult(null);
                  try {
                    const text = await file.text();
                    const data = JSON.parse(text);
                    if (data.export_version !== 1) {
                      toast('Unsupported export file format (expected export_version: 1).', 'error');
                      return;
                    }
                    const confirmed = await confirm(
                      'This will overwrite alert rules, temperature targets, and quiet hours with the imported values. Profiles and sensor labels will be merged. Continue?'
                    );
                    if (!confirmed) return;
                    const result = await api.importConfig(data);
                    setImportResult(result.imported);
                    toast('Configuration imported successfully.');
                    // Refresh settings to reflect any changes
                    try {
                      const refreshed = await api.getSettings();
                      setSettings(refreshed);
                    } catch { /* best effort */ }
                  } catch (err: any) {
                    toast(err?.message || 'Import failed.', 'error');
                  } finally {
                    setImporting(false);
                  }
                };
                input.click();
              }}
            >
              <Upload size={14} />
              {importing ? 'Importing...' : 'Import Config'}
            </button>
          </div>

          {importResult && (
            <div className="mt-4 p-3 rounded text-xs" style={{ background: 'var(--surface-200)', color: 'var(--text)' }}>
              <strong>Import results:</strong>
              <ul className="mt-1 space-y-0.5 list-disc list-inside" style={{ color: 'var(--text-secondary)' }}>
                {Object.entries(importResult).map(([key, count]) => (
                  <li key={key}>{key.replace(/_/g, ' ')}: {count}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}

      {/* Profile Schedules */}
      {settingsTab === 'automation' && <ProfileScheduleEditor isAdmin={isAdmin} toast={toast} confirm={confirm} />}

      {/* Scheduled Reports */}
      {settingsTab === 'automation' && <ReportScheduleForm isAdmin={isAdmin} toast={toast} confirm={confirm} />}

      {/* Noise Profiler */}
      {settingsTab === 'automation' && <NoiseProfiler />}

      {/* Help */}
      <div className="card p-4 flex items-start gap-3 animate-card-enter" style={{ background: 'var(--accent-muted)' }}>
        <Info size={16} className="mt-0.5 shrink-0" style={{ color: 'var(--accent)' }} />
        <div className="text-xs leading-relaxed" style={{ color: 'var(--text-secondary)' }}>
          <strong style={{ color: 'var(--text)' }}>Tip:</strong> On Windows, hardware sensors are read
          directly via the bundled LibreHardwareMonitor library — no separate process is required.
          On Linux/Docker, ensure lm-sensors is installed and <code>sensors-detect</code> has been run.
        </div>
      </div>
    </div>
  );
}
