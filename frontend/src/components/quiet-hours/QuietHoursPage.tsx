'use client';

import { useEffect, useState, useCallback } from 'react';
import { api } from '@/lib/api';
import type { QuietHoursRule } from '@/lib/types';
import { Plus, Trash2, Edit3, Moon } from 'lucide-react';
import { useConfirm } from '@/components/ui/ConfirmDialog';
import { useCanWrite } from '@/hooks/useCanWrite';
import { ViewerBanner } from '@/components/ui/ViewerBanner';

const DAY_NAMES = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const DAY_SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

interface Profile {
  id: string;
  name: string;
}

interface RuleFormData {
  day_of_week: number;
  start_time: string;
  end_time: string;
  profile_id: string;
  enabled: boolean;
}

const emptyForm: RuleFormData = {
  day_of_week: 0,
  start_time: '22:00',
  end_time: '07:00',
  profile_id: '',
  enabled: true,
};

export function QuietHoursPage() {
  const canWrite = useCanWrite();
  const confirm = useConfirm();
  const [rules, setRules] = useState<QuietHoursRule[]>([]);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [form, setForm] = useState<RuleFormData>(emptyForm);
  const [saving, setSaving] = useState(false);

  const fetchData = useCallback(async () => {
    setLoading(true);
    try {
      const [rulesRes, profilesRes] = await Promise.all([
        api.quietHours.list(),
        api.getProfiles(),
      ]);
      setRules(rulesRes.rules);
      setProfiles(profilesRes.profiles.map((p: any) => ({ id: p.id, name: p.name })));
      setError(null);
    } catch (e: any) {
      setError(e?.message ?? 'Failed to load data');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchData(); }, [fetchData]);

  const handleCreate = () => {
    setEditingId(null);
    setForm({
      ...emptyForm,
      profile_id: profiles[0]?.id ?? '',
    });
    setShowForm(true);
  };

  const handleEdit = (rule: QuietHoursRule) => {
    setEditingId(rule.id);
    setForm({
      day_of_week: rule.day_of_week,
      start_time: rule.start_time,
      end_time: rule.end_time,
      profile_id: rule.profile_id,
      enabled: rule.enabled,
    });
    setShowForm(true);
  };

  const handleDelete = async (rule: QuietHoursRule) => {
    const ok = await confirm(
      `Delete quiet hours rule for ${DAY_NAMES[rule.day_of_week]} ${rule.start_time}–${rule.end_time}?`
    );
    if (!ok) return;
    try {
      await api.quietHours.delete(rule.id);
      await fetchData();
    } catch { /* ignore */ }
  };

  const handleSave = async () => {
    if (!form.profile_id) return;
    setSaving(true);
    try {
      if (editingId != null) {
        await api.quietHours.update(editingId, form);
      } else {
        await api.quietHours.create(form);
      }
      setShowForm(false);
      setEditingId(null);
      await fetchData();
    } catch (e: any) {
      setError(e?.message ?? 'Failed to save rule');
    } finally {
      setSaving(false);
    }
  };

  const handleCancel = () => {
    setShowForm(false);
    setEditingId(null);
  };

  const profileName = (id: string) => profiles.find(p => p.id === id)?.name ?? id;

  // Group rules by day for the weekly grid
  const rulesByDay: QuietHoursRule[][] = Array.from({ length: 7 }, (_, d) =>
    rules.filter(r => r.day_of_week === d).sort((a, b) => a.start_time.localeCompare(b.start_time))
  );

  if (loading) {
    return (
      <div className="max-w-4xl mx-auto animate-fade-in">
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>Loading quiet hours...</p>
      </div>
    );
  }

  return (
    <div className="max-w-4xl mx-auto space-y-4 animate-fade-in">
      {!canWrite && <ViewerBanner />}

      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <div className="w-9 h-9 rounded-xl flex items-center justify-center" style={{ background: 'var(--accent-muted)' }}>
            <Moon size={18} style={{ color: 'var(--accent)' }} />
          </div>
          <div>
            <h2 className="text-lg font-bold" style={{ color: 'var(--text)' }}>Quiet Hours</h2>
            <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
              Schedule profile changes for noise-sensitive times
            </p>
          </div>
        </div>
        {canWrite && (
          <button onClick={handleCreate} className="btn-primary text-sm flex items-center gap-1.5">
            <Plus size={14} />
            Add Rule
          </button>
        )}
      </div>

      {error && (
        <div className="rounded-lg px-4 py-2 text-sm" style={{ background: '#7f1d1d', color: '#fca5a5' }}>
          {error}
        </div>
      )}

      {/* Create/Edit Form */}
      {showForm && (
        <div className="card p-5 animate-card-enter">
          <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>
            {editingId != null ? 'Edit Rule' : 'New Quiet Hours Rule'}
          </h3>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <label className="flex flex-col gap-1">
              <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Day of Week</span>
              <select
                value={form.day_of_week}
                onChange={e => setForm({ ...form, day_of_week: Number(e.target.value) })}
                className="px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              >
                {DAY_NAMES.map((name, i) => (
                  <option key={i} value={i}>{name}</option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Profile</span>
              <select
                value={form.profile_id}
                onChange={e => setForm({ ...form, profile_id: e.target.value })}
                className="px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              >
                {profiles.length === 0 && <option value="">No profiles</option>}
                {profiles.map(p => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Start Time</span>
              <input
                type="time"
                value={form.start_time}
                onChange={e => setForm({ ...form, start_time: e.target.value })}
                className="px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>End Time</span>
              <input
                type="time"
                value={form.end_time}
                onChange={e => setForm({ ...form, end_time: e.target.value })}
                className="px-3 py-2 rounded-lg text-sm border outline-none"
                style={{ background: 'var(--bg)', borderColor: 'var(--border)', color: 'var(--text)' }}
              />
            </label>
          </div>

          <label className="flex items-center gap-2 mt-3 text-xs" style={{ color: 'var(--text-secondary)' }}>
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={e => setForm({ ...form, enabled: e.target.checked })}
            />
            Enabled
          </label>

          <div className="flex gap-2 mt-4">
            <button onClick={handleSave} disabled={saving || !form.profile_id} className="btn-primary text-sm px-4">
              {saving ? 'Saving...' : editingId != null ? 'Update' : 'Create'}
            </button>
            <button onClick={handleCancel} className="btn-secondary text-sm px-4">Cancel</button>
          </div>
        </div>
      )}

      {/* Weekly Grid View */}
      <div className="card p-5 animate-card-enter">
        <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>Weekly Schedule</h3>
        {rules.length === 0 ? (
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
            No quiet hours rules configured. Click &ldquo;Add Rule&rdquo; to create one.
          </p>
        ) : (
          <div className="space-y-1">
            {DAY_NAMES.map((dayName, dayIdx) => {
              const dayRules = rulesByDay[dayIdx];
              return (
                <div
                  key={dayIdx}
                  className="rounded-lg px-3 py-2"
                  style={{ background: dayRules.length > 0 ? 'var(--bg)' : 'transparent', border: dayRules.length > 0 ? '1px solid var(--border)' : '1px solid transparent' }}
                >
                  <div className="flex items-center gap-3">
                    <span
                      className="text-xs font-medium w-12 shrink-0"
                      style={{ color: dayRules.length > 0 ? 'var(--text)' : 'var(--text-secondary)' }}
                    >
                      {DAY_SHORT[dayIdx]}
                    </span>
                    {dayRules.length === 0 ? (
                      <span className="text-xs" style={{ color: 'var(--text-secondary)', opacity: 0.5 }}>—</span>
                    ) : (
                      <div className="flex flex-wrap gap-2 flex-1">
                        {dayRules.map(rule => (
                          <div key={rule.id} className="flex items-center gap-2">
                            <span
                              className="badge text-xs"
                              style={{
                                background: rule.enabled ? 'var(--accent-muted)' : 'var(--surface-200)',
                                color: rule.enabled ? 'var(--accent)' : 'var(--text-secondary)',
                              }}
                            >
                              {rule.start_time}–{rule.end_time}
                            </span>
                            <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                              {profileName(rule.profile_id)}
                            </span>
                            {canWrite && (
                              <>
                                <button onClick={() => handleEdit(rule)} className="p-0.5" style={{ color: 'var(--text-secondary)' }}>
                                  <Edit3 size={11} />
                                </button>
                                <button onClick={() => handleDelete(rule)} className="p-0.5" style={{ color: 'var(--text-secondary)' }}>
                                  <Trash2 size={11} />
                                </button>
                              </>
                            )}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Timeline visualization */}
      {rules.length > 0 && (
        <div className="card p-5 animate-card-enter">
          <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>Timeline</h3>
          <div className="space-y-2">
            {DAY_NAMES.map((dayName, dayIdx) => {
              const dayRules = rulesByDay[dayIdx];
              return (
                <div key={dayIdx} className="flex items-center gap-2">
                  <span className="text-xs w-8 shrink-0" style={{ color: 'var(--text-secondary)' }}>{DAY_SHORT[dayIdx]}</span>
                  <div className="flex-1 relative" style={{ height: 16, background: 'var(--surface-200)', borderRadius: 4 }}>
                    {dayRules.map(rule => {
                      const [sh, sm] = rule.start_time.split(':').map(Number);
                      const [eh, em] = rule.end_time.split(':').map(Number);
                      const startMin = sh * 60 + sm;
                      let endMin = eh * 60 + em;
                      // Handle overnight spans: show as ending at midnight (visual simplification)
                      if (endMin <= startMin) endMin = 24 * 60;
                      const left = (startMin / (24 * 60)) * 100;
                      const width = Math.max(((endMin - startMin) / (24 * 60)) * 100, 1);
                      return (
                        <div
                          key={rule.id}
                          className="absolute top-0 bottom-0 rounded"
                          style={{
                            left: `${left}%`,
                            width: `${width}%`,
                            background: rule.enabled ? 'var(--accent)' : 'var(--border)',
                            opacity: rule.enabled ? 0.7 : 0.3,
                          }}
                          title={`${rule.start_time}–${rule.end_time}: ${profileName(rule.profile_id)}`}
                        />
                      );
                    })}
                  </div>
                </div>
              );
            })}
            {/* Time labels */}
            <div className="flex items-center gap-2">
              <span className="w-8 shrink-0" />
              <div className="flex-1 flex justify-between">
                {[0, 6, 12, 18, 24].map(h => (
                  <span key={h} className="text-xs" style={{ color: 'var(--text-secondary)', fontSize: '0.6rem' }}>
                    {h === 24 ? '24:00' : `${String(h).padStart(2, '0')}:00`}
                  </span>
                ))}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
