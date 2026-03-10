'use client';

import { useState, useEffect } from 'react';
import { Plus, Trash2, Clock, Calendar } from 'lucide-react';
import type { ProfileSchedule, Profile } from '@/lib/types';
import { api } from '@/lib/api';

interface ProfileScheduleEditorProps {
  isAdmin: boolean;
  toast: (msg: string, type?: 'error') => void;
  confirm: (msg: string) => Promise<boolean>;
}

const DAY_LABELS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];

export function ProfileScheduleEditor({ isAdmin, toast, confirm }: ProfileScheduleEditorProps) {
  const [schedules, setSchedules] = useState<ProfileSchedule[]>([]);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  // Form state
  const [profileId, setProfileId] = useState('');
  const [startTime, setStartTime] = useState('09:00');
  const [endTime, setEndTime] = useState('17:00');
  const [selectedDays, setSelectedDays] = useState<number[]>([0, 1, 2, 3, 4]);
  const [timezone, setTimezone] = useState(() => {
    try { return Intl.DateTimeFormat().resolvedOptions().timeZone; } catch { return 'UTC'; }
  });

  useEffect(() => {
    Promise.all([
      api.profileSchedules.list(),
      api.getProfiles(),
    ])
      .then(([schedData, profData]) => {
        setSchedules(schedData.schedules);
        setProfiles(profData.profiles);
        if (profData.profiles.length > 0 && !profileId) {
          setProfileId(profData.profiles[0].id);
        }
      })
      .catch(() => toast('Failed to load profile schedules', 'error'))
      .finally(() => setLoading(false));
  }, []);

  function toggleDay(day: number) {
    setSelectedDays(prev =>
      prev.includes(day) ? prev.filter(d => d !== day) : [...prev, day].sort()
    );
  }

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!profileId) {
      toast('Select a profile', 'error');
      return;
    }
    if (selectedDays.length === 0) {
      toast('Select at least one day', 'error');
      return;
    }
    if (!/^\d{2}:\d{2}$/.test(startTime) || !/^\d{2}:\d{2}$/.test(endTime)) {
      toast('Times must be in HH:MM format', 'error');
      return;
    }
    setSaving(true);
    try {
      const created = await api.profileSchedules.create({
        profile_id: profileId,
        start_time: startTime,
        end_time: endTime,
        days_of_week: selectedDays.join(','),
        timezone,
        enabled: true,
      });
      setSchedules(prev => [...prev, created]);
      toast('Schedule created');
    } catch {
      toast('Failed to create schedule', 'error');
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(id: string) {
    if (!(await confirm('Delete this profile schedule?'))) return;
    try {
      await api.profileSchedules.delete(id);
      setSchedules(prev => prev.filter(s => s.id !== id));
      toast('Schedule deleted');
    } catch {
      toast('Failed to delete schedule', 'error');
    }
  }

  async function handleToggle(schedule: ProfileSchedule) {
    try {
      await api.profileSchedules.update(schedule.id, {
        profile_id: schedule.profile_id,
        start_time: schedule.start_time,
        end_time: schedule.end_time,
        days_of_week: schedule.days_of_week,
        timezone: schedule.timezone,
        enabled: !schedule.enabled,
      });
      setSchedules(prev =>
        prev.map(s => s.id === schedule.id ? { ...s, enabled: !s.enabled } : s)
      );
    } catch {
      toast('Failed to toggle schedule', 'error');
    }
  }

  function getProfileName(pid: string): string {
    return profiles.find(p => p.id === pid)?.name || pid;
  }

  function formatDays(daysStr: string): string {
    return daysStr.split(',').map(d => DAY_LABELS[parseInt(d.trim())] || d).join(', ');
  }

  if (loading) return null;

  return (
    <div className="card p-4 space-y-4 animate-card-enter">
      <h3 className="section-title flex items-center gap-2">
        <Calendar size={16} />
        Profile Schedules
      </h3>
      <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
        Automatically switch profiles based on time of day. Schedules are lower priority than
        quiet hours and alert-triggered switches.
      </p>

      {/* Existing schedules */}
      {schedules.length > 0 && (
        <div className="space-y-2">
          {schedules.map(schedule => (
            <div
              key={schedule.id}
              className="flex items-center justify-between p-3 rounded"
              style={{
                background: 'var(--surface-200)',
                opacity: schedule.enabled ? 1 : 0.5,
              }}
            >
              <div className="flex items-center gap-3 flex-1 min-w-0">
                <button
                  onClick={() => handleToggle(schedule)}
                  disabled={!isAdmin}
                  className="shrink-0"
                  style={{
                    width: 36, height: 20, borderRadius: 10, border: 'none', cursor: isAdmin ? 'pointer' : 'default',
                    background: schedule.enabled ? 'var(--accent)' : 'var(--border)',
                    position: 'relative', transition: 'background 0.2s',
                  }}
                >
                  <span style={{
                    position: 'absolute', top: 2, left: schedule.enabled ? 18 : 2,
                    width: 16, height: 16, borderRadius: '50%', background: '#fff',
                    transition: 'left 0.2s',
                  }} />
                </button>
                <div className="flex-1 min-w-0">
                  <div className="text-sm font-medium truncate" style={{ color: 'var(--text)' }}>
                    {getProfileName(schedule.profile_id)}
                  </div>
                  <div className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                    <Clock size={10} className="inline mr-1" />
                    {schedule.start_time} &ndash; {schedule.end_time}
                    {' '}&middot;{' '}
                    {formatDays(schedule.days_of_week)}
                    {schedule.timezone !== 'UTC' && (
                      <span> &middot; {schedule.timezone}</span>
                    )}
                  </div>
                </div>
              </div>
              {isAdmin && (
                <button
                  onClick={() => handleDelete(schedule.id)}
                  className="shrink-0 ml-2 p-1 rounded hover:bg-red-500/10"
                  style={{ color: 'var(--danger)' }}
                  title="Delete schedule"
                >
                  <Trash2 size={14} />
                </button>
              )}
            </div>
          ))}
        </div>
      )}

      {/* Create form (admin only) */}
      {isAdmin && (
        <form onSubmit={handleCreate} className="space-y-3 pt-2" style={{ borderTop: '1px solid var(--border)' }}>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Profile</label>
              <select
                value={profileId}
                onChange={e => setProfileId(e.target.value)}
                className="w-full text-sm px-2 py-1.5 rounded"
                style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              >
                {profiles.map(p => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </div>
            <div>
              <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Timezone</label>
              <input
                type="text"
                value={timezone}
                onChange={e => setTimezone(e.target.value)}
                className="w-full text-sm px-2 py-1.5 rounded"
                style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              />
            </div>
          </div>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Start Time</label>
              <input
                type="time"
                value={startTime}
                onChange={e => setStartTime(e.target.value)}
                className="w-full text-sm px-2 py-1.5 rounded"
                style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              />
            </div>
            <div>
              <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>End Time</label>
              <input
                type="time"
                value={endTime}
                onChange={e => setEndTime(e.target.value)}
                className="w-full text-sm px-2 py-1.5 rounded"
                style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
              />
            </div>
          </div>
          <div>
            <label className="text-xs font-medium block mb-1" style={{ color: 'var(--text-secondary)' }}>Days</label>
            <div className="flex gap-1">
              {DAY_LABELS.map((label, i) => (
                <button
                  key={i}
                  type="button"
                  onClick={() => toggleDay(i)}
                  className="px-2 py-1 text-xs rounded font-medium"
                  style={{
                    background: selectedDays.includes(i) ? 'var(--accent)' : 'var(--surface-200)',
                    color: selectedDays.includes(i) ? '#fff' : 'var(--text-secondary)',
                    border: '1px solid var(--border)',
                    cursor: 'pointer',
                  }}
                >
                  {label}
                </button>
              ))}
            </div>
          </div>
          <button
            type="submit"
            disabled={saving || !profileId || selectedDays.length === 0}
            className="btn-primary text-xs flex items-center gap-1"
          >
            <Plus size={12} />
            {saving ? 'Creating...' : 'Add Schedule'}
          </button>
        </form>
      )}
    </div>
  );
}
