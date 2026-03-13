'use client';

import { useState, useEffect, useCallback } from 'react';
import { Pencil, X } from 'lucide-react';
import type { NotificationChannel, NotificationChannelType } from '@/lib/types';
import { api } from '@/lib/api';

interface NotificationChannelFormProps {
  isAdmin: boolean;
  toast: (msg: string, type?: 'error') => void;
  confirm: (msg: string) => Promise<boolean>;
}

const CHANNEL_HINTS: Record<NotificationChannelType, string> = {
  discord: '— { "webhook_url": "https://discord.com/api/webhooks/..." }',
  slack: '— { "webhook_url": "https://hooks.slack.com/services/..." }',
  ntfy: '— { "topic": "my-alerts", "url": "https://ntfy.sh" }',
  generic_webhook: '— { "url": "https://...", "hmac_secret": "optional" }',
  mqtt: '',
};

const CHANNEL_PLACEHOLDERS: Record<NotificationChannelType, string> = {
  discord: '{ "webhook_url": "" }',
  slack: '{ "webhook_url": "" }',
  ntfy: '{ "topic": "", "url": "https://ntfy.sh" }',
  generic_webhook: '{ "url": "" }',
  mqtt: '',
};

const CHANNEL_TYPE_LABELS: Record<NotificationChannelType, string> = {
  discord: 'Discord',
  slack: 'Slack',
  ntfy: 'ntfy.sh',
  generic_webhook: 'Generic Webhook',
  mqtt: 'MQTT',
};

interface MqttConfig {
  broker_url: string;
  topic_prefix: string;
  username: string;
  password: string;
  client_id: string;
  qos: number;
  retain: boolean;
  publish_telemetry: boolean;
}

const DEFAULT_MQTT: MqttConfig = {
  broker_url: 'mqtt://192.168.1.100:1883',
  topic_prefix: 'drivechill',
  username: '',
  password: '',
  client_id: 'drivechill-hub',
  qos: 1,
  retain: false,
  publish_telemetry: false,
};

function MqttConfigFields({ mqtt, onChange }: { mqtt: MqttConfig; onChange: (m: MqttConfig) => void }) {
  const inputStyle = { background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' };
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Broker URL</label>
          <input className="w-full p-2 rounded text-xs" style={inputStyle}
            value={mqtt.broker_url} onChange={e => onChange({ ...mqtt, broker_url: e.target.value })}
            placeholder="mqtt://192.168.1.100:1883" />
        </div>
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Topic Prefix</label>
          <input className="w-full p-2 rounded text-xs" style={inputStyle}
            value={mqtt.topic_prefix} onChange={e => onChange({ ...mqtt, topic_prefix: e.target.value })}
            placeholder="drivechill" />
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Username</label>
          <input className="w-full p-2 rounded text-xs" style={inputStyle}
            value={mqtt.username} onChange={e => onChange({ ...mqtt, username: e.target.value })}
            placeholder="(optional)" />
        </div>
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Password</label>
          <input className="w-full p-2 rounded text-xs" type="password" style={inputStyle}
            value={mqtt.password} onChange={e => onChange({ ...mqtt, password: e.target.value })}
            placeholder="(optional)" />
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Client ID</label>
          <input className="w-full p-2 rounded text-xs" style={inputStyle}
            value={mqtt.client_id} onChange={e => onChange({ ...mqtt, client_id: e.target.value })}
            placeholder="drivechill-hub" />
        </div>
        <div>
          <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>QoS</label>
          <select className="w-full p-2 rounded text-xs" style={inputStyle}
            value={mqtt.qos} onChange={e => onChange({ ...mqtt, qos: Number(e.target.value) })}>
            <option value={0}>0 — At most once</option>
            <option value={1}>1 — At least once</option>
            <option value={2}>2 — Exactly once</option>
          </select>
        </div>
      </div>
      <div className="flex items-center gap-4">
        <label className="flex items-center gap-2 text-xs cursor-pointer" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={mqtt.retain}
            onChange={e => onChange({ ...mqtt, retain: e.target.checked })} />
          Retain messages
        </label>
        <label className="flex items-center gap-2 text-xs cursor-pointer" style={{ color: 'var(--text-secondary)' }}>
          <input type="checkbox" checked={mqtt.publish_telemetry}
            onChange={e => onChange({ ...mqtt, publish_telemetry: e.target.checked })} />
          Publish telemetry (sensor data every poll)
        </label>
      </div>
    </div>
  );
}

export function NotificationChannelForm({ isAdmin, toast, confirm }: NotificationChannelFormProps) {
  const [channels, setChannels] = useState<NotificationChannel[]>([]);
  const [name, setName] = useState('');
  const [type, setType] = useState<NotificationChannelType>('discord');
  const [enabled, setEnabled] = useState(true);
  const [config, setConfig] = useState('');
  const [mqttConfig, setMqttConfig] = useState<MqttConfig>({ ...DEFAULT_MQTT });
  const [editId, setEditId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [testingId, setTestingId] = useState<string | null>(null);

  useEffect(() => {
    api.notificationChannels.list().then(r => setChannels(r.channels)).catch(() => toast('Failed to load notification channels.', 'error'));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const resetForm = useCallback(() => {
    setEditId(null);
    setName('');
    setType('discord');
    setEnabled(true);
    setConfig('');
    setMqttConfig({ ...DEFAULT_MQTT });
  }, []);

  const getConfigPayload = useCallback((): Record<string, unknown> | null => {
    if (type === 'mqtt') {
      if (!mqttConfig.broker_url.trim()) {
        toast('Broker URL is required.', 'error');
        return null;
      }
      return { ...mqttConfig };
    }
    try {
      return config.trim() ? JSON.parse(config) : {};
    } catch {
      toast('Config must be valid JSON.', 'error');
      return null;
    }
  }, [type, mqttConfig, config, toast]);

  const handleSave = async () => {
    const parsed = getConfigPayload();
    if (parsed === null) return;
    setBusy(true);
    try {
      if (editId) {
        await api.notificationChannels.update(editId, { name: name.trim(), enabled, config: parsed });
        toast('Channel updated.');
      } else {
        await api.notificationChannels.create({ type, name: name.trim(), enabled, config: parsed });
        toast('Channel created.');
      }
      const r = await api.notificationChannels.list();
      setChannels(r.channels);
      resetForm();
    } catch (err: any) {
      toast(err?.message || 'Save failed.', 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card p-6 animate-card-enter">
      <h3 className="text-base font-semibold mb-4" style={{ color: 'var(--text)' }}>
        Notification Channels
      </h3>
      <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
        Send alert notifications to Discord, Slack, ntfy.sh, MQTT, or a generic webhook endpoint.
        Channels are used alongside existing push/email notifications.
      </p>

      {/* Existing channels list */}
      {channels.length > 0 && (
        <div className="space-y-2 mb-4">
          {channels.map(ch => (
            <div key={ch.id} className="flex items-center justify-between p-3 rounded text-xs"
              style={{ background: 'var(--surface-200)', color: 'var(--text)' }}>
              <div className="flex-1 min-w-0">
                <div className="font-medium flex items-center gap-2">
                  {ch.name}
                  <span className="badge" style={{ fontSize: '0.65rem' }}>
                    {CHANNEL_TYPE_LABELS[ch.type] ?? ch.type.replace('_', ' ')}
                  </span>
                  {ch.enabled
                    ? <span className="badge badge-success" style={{ fontSize: '0.6rem' }}>ON</span>
                    : <span className="badge badge-danger" style={{ fontSize: '0.6rem' }}>OFF</span>}
                </div>
              </div>
              <div className="flex items-center gap-2 ml-2 shrink-0">
                {isAdmin && <button className="btn-secondary text-xs px-2 py-1"
                  disabled={testingId === ch.id}
                  onClick={async () => {
                    setTestingId(ch.id);
                    try {
                      const r = await api.notificationChannels.test(ch.id);
                      if (r.success) toast('Test notification sent!');
                      else toast(r.error || 'Test failed.', 'error');
                    } catch (err: any) {
                      toast(err?.message || 'Test failed.', 'error');
                    } finally {
                      setTestingId(null);
                    }
                  }}>
                  {testingId === ch.id ? '...' : 'Test'}
                </button>}
                {isAdmin && (
                  <>
                    <button className="btn-secondary text-xs px-2 py-1"
                      onClick={() => {
                        setEditId(ch.id);
                        setName(ch.name);
                        setType(ch.type);
                        setEnabled(ch.enabled);
                        if (ch.type === 'mqtt' && ch.config) {
                          const c = ch.config as Record<string, unknown>;
                          setMqttConfig({
                            broker_url: (c.broker_url as string) ?? DEFAULT_MQTT.broker_url,
                            topic_prefix: (c.topic_prefix as string) ?? DEFAULT_MQTT.topic_prefix,
                            username: (c.username as string) ?? '',
                            password: (c.password as string) ?? '',
                            client_id: (c.client_id as string) ?? DEFAULT_MQTT.client_id,
                            qos: typeof c.qos === 'number' ? c.qos : DEFAULT_MQTT.qos,
                            retain: typeof c.retain === 'boolean' ? c.retain : DEFAULT_MQTT.retain,
                            publish_telemetry: typeof c.publish_telemetry === 'boolean' ? c.publish_telemetry : DEFAULT_MQTT.publish_telemetry,
                          });
                          setConfig('');
                        } else {
                          setConfig(JSON.stringify(ch.config, null, 2));
                        }
                      }}>
                      <Pencil size={12} />
                    </button>
                    <button className="btn-secondary text-xs px-2 py-1"
                      style={{ color: 'var(--danger)' }}
                      onClick={async () => {
                        const confirmed = await confirm(`Delete channel "${ch.name}"?`);
                        if (!confirmed) return;
                        try {
                          await api.notificationChannels.delete(ch.id);
                          setChannels(prev => prev.filter(c => c.id !== ch.id));
                          toast('Channel deleted.');
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
            {editId ? 'Edit Channel' : 'Add Channel'}
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Name</label>
              <input className="w-full p-2 rounded text-xs"
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={name} onChange={e => setName(e.target.value)} placeholder="My MQTT Broker" />
            </div>
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>Type</label>
              <select className="w-full p-2 rounded text-xs"
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)' }}
                value={type} onChange={e => setType(e.target.value as NotificationChannelType)}
                disabled={!!editId}>
                {Object.entries(CHANNEL_TYPE_LABELS).map(([value, label]) => (
                  <option key={value} value={value}>{label}</option>
                ))}
              </select>
            </div>
          </div>
          <div className="flex items-center gap-2">
            <input type="checkbox" id="nc-enabled" checked={enabled}
              onChange={e => setEnabled(e.target.checked)} />
            <label htmlFor="nc-enabled" className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              Enabled
            </label>
          </div>
          {type === 'mqtt' ? (
            <MqttConfigFields mqtt={mqttConfig} onChange={setMqttConfig} />
          ) : (
            <div>
              <label className="text-xs block mb-1" style={{ color: 'var(--text-secondary)' }}>
                Config (JSON)
                <span style={{ opacity: 0.6, marginLeft: 4 }}>{CHANNEL_HINTS[type]}</span>
              </label>
              <textarea className="w-full p-2 rounded text-xs font-mono" rows={4}
                style={{ background: 'var(--bg)', color: 'var(--text)', border: '1px solid var(--border)', resize: 'vertical' }}
                value={config} onChange={e => setConfig(e.target.value)}
                placeholder={CHANNEL_PLACEHOLDERS[type]} />
            </div>
          )}
          <div className="flex gap-2">
            <button className="btn-primary text-xs px-4 py-2" disabled={busy || !name.trim()}
              onClick={handleSave}>
              {busy ? 'Saving...' : editId ? 'Update' : 'Create'}
            </button>
            {editId && (
              <button className="btn-secondary text-xs px-4 py-2" onClick={resetForm}>
                Cancel
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
