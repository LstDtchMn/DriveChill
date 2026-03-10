'use client';

import { useState, useEffect } from 'react';
import { Plus, Trash2, Clock } from 'lucide-react';
import type { ReportSchedule } from '@/lib/types';
import { api } from '@/lib/api';

interface ReportScheduleFormProps {
  isAdmin: boolean;
  toast: (msg: string, type?: 'error') => void;
  confirm: (msg: string) => Promise<boolean>;
}

export function ReportScheduleForm({ isAdmin, toast, confirm }: ReportScheduleFormProps) {
  const [schedules, setSchedules] = useState<ReportSchedule[]>([]);
  const [frequency, setFrequency] = useState<'daily' | 'weekly'>('daily');
  const [timeUtc, setTimeUtc] = useState('08:00');
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.reportSchedules.list()
      .then(data => setSchedules(data.schedules))
      .catch(() => toast('Failed to load report schedules', 'error'))
      .finally(() => setLoading(false));
  }, []);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!timeUtc.match(/^\d{2}:\d{2}$/)) {
      toast('Time must be in HH:MM format (UTC)', 'error');
      return;
    }
    setSaving(true);
    try {
      const created = await api.reportSchedules.create({ frequency, time_utc: timeUtc, timezone: 'UTC', enabled: true });
      setSchedules(prev => [...prev, created]);
      setTimeUtc('08:00');
      setFrequency('daily');
      toast('Report schedule created');
    } catch {
      toast('Failed to create report schedule', 'error');
    } finally {
      setSaving(false);
    }
  }

  async function handleToggle(schedule: ReportSchedule) {
    try {
      const updated = await api.reportSchedules.update(schedule.id, { enabled: !schedule.enabled });
      setSchedules(prev => prev.map(s => s.id === updated.id ? updated : s));
    } catch {
      toast('Failed to update schedule', 'error');
    }
  }

  async function handleDelete(id: string) {
    const ok = await confirm('Delete this report schedule?');
    if (!ok) return;
    try {
      await api.reportSchedules.delete(id);
      setSchedules(prev => prev.filter(s => s.id !== id));
      toast('Report schedule deleted');
    } catch {
      toast('Failed to delete report schedule', 'error');
    }
  }

  return (
    <div className="card p-6 animate-card-enter">
      <h3 className="section-title mb-4 flex items-center gap-2">
        <Clock size={16} style={{ color: 'var(--accent)' }} />
        Scheduled Analytics Reports
      </h3>
      <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>
        Automatically email an analytics summary at a scheduled time. Reports are sent via
        the configured SMTP settings.
      </p>

      {/* Existing schedules */}
      {loading ? (
        <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>Loading…</p>
      ) : schedules.length === 0 ? (
        <p className="text-xs mb-4" style={{ color: 'var(--text-secondary)' }}>No schedules configured.</p>
      ) : (
        <div className="space-y-2 mb-4">
          {schedules.map(s => (
            <div
              key={s.id}
              className="flex items-center gap-3 p-3 rounded"
              style={{ background: 'var(--surface-200)', border: '1px solid var(--border)' }}
            >
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap">
                  <span
                    className="text-sm font-medium capitalize"
                    style={{ color: 'var(--text)' }}
                  >
                    {s.frequency}
                  </span>
                  <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                    at {s.time_utc} UTC
                  </span>
                  <span
                    className={`badge ${s.enabled ? 'badge-success' : ''}`}
                    style={!s.enabled ? { background: 'var(--surface-200)', color: 'var(--text-secondary)' } : {}}
                  >
                    {s.enabled ? 'Enabled' : 'Disabled'}
                  </span>
                </div>
                {s.last_sent_at && (
                  <p className="text-xs mt-0.5" style={{ color: 'var(--text-secondary)' }}>
                    Last sent: {new Date(s.last_sent_at).toLocaleString()}
                  </p>
                )}
              </div>

              {isAdmin && (
                <div className="flex items-center gap-2 shrink-0">
                  <button
                    onClick={() => handleToggle(s)}
                    className="btn-secondary text-xs px-2 py-1"
                    title={s.enabled ? 'Disable' : 'Enable'}
                  >
                    {s.enabled ? 'Disable' : 'Enable'}
                  </button>
                  <button
                    onClick={() => handleDelete(s.id)}
                    className="btn-secondary"
                    style={{ color: 'var(--danger)' }}
                    title="Delete schedule"
                  >
                    <Trash2 size={14} />
                  </button>
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {/* Create form */}
      {isAdmin && (
        <form onSubmit={handleCreate} className="flex items-end gap-2 flex-wrap">
          <div className="flex flex-col gap-1">
            <label className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              Frequency
            </label>
            <select
              value={frequency}
              onChange={e => setFrequency(e.target.value as 'daily' | 'weekly')}
              className="text-sm px-2 py-1.5 rounded"
              style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
            >
              <option value="daily">Daily</option>
              <option value="weekly">Weekly</option>
            </select>
          </div>

          <div className="flex flex-col gap-1">
            <label className="text-xs font-medium" style={{ color: 'var(--text-secondary)' }}>
              Time (UTC, HH:MM)
            </label>
            <input
              type="time"
              value={timeUtc}
              onChange={e => setTimeUtc(e.target.value)}
              className="text-sm px-2 py-1.5 rounded"
              style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', color: 'var(--text)' }}
              required
            />
          </div>

          <button
            type="submit"
            className="btn-primary flex items-center gap-1.5 text-sm"
            disabled={saving}
          >
            <Plus size={14} />
            {saving ? 'Saving…' : 'Add Schedule'}
          </button>
        </form>
      )}
    </div>
  );
}
