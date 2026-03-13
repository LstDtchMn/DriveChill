'use client';

import React, { useState, useEffect, useRef } from 'react';
import { DAY_LABELS } from '@/hooks/useScheduleGrid';

interface SchedulePopoverProps {
  anchorRect: { top: number; left: number };
  initial?: {
    id?: string;
    profileId: string;
    startTime: string;
    endTime: string;
    days: number[];
  };
  profiles: { id: string; name: string }[];
  onSave: (data: {
    id?: string;
    profile_id: string;
    start_time: string;
    end_time: string;
    days_of_week: string;
  }) => void;
  onDelete?: (id: string) => void;
  onClose: () => void;
}

export function SchedulePopover({
  anchorRect,
  initial,
  profiles,
  onSave,
  onDelete,
  onClose,
}: SchedulePopoverProps) {
  const [profileId, setProfileId] = useState(initial?.profileId ?? profiles[0]?.id ?? '');
  const [startTime, setStartTime] = useState(initial?.startTime ?? '08:00');
  const [endTime, setEndTime] = useState(initial?.endTime ?? '18:00');
  const [days, setDays] = useState<number[]>(initial?.days ?? [0, 1, 2, 3, 4]);
  const popoverRef = useRef<HTMLDivElement>(null);

  // Clamp popover so it doesn't overflow the viewport
  const [position, setPosition] = useState({ top: anchorRect.top, left: anchorRect.left });
  useEffect(() => {
    if (!popoverRef.current) return;
    const rect = popoverRef.current.getBoundingClientRect();
    const viewW = window.innerWidth;
    const viewH = window.innerHeight;
    let { top, left } = anchorRect;
    if (left + rect.width > viewW - 8) left = viewW - rect.width - 8;
    if (top + rect.height > viewH - 8) top = viewH - rect.height - 8;
    if (left < 8) left = 8;
    if (top < 8) top = 8;
    setPosition({ top, left });
  }, [anchorRect]);

  const toggleDay = (day: number) => {
    setDays((prev) =>
      prev.includes(day) ? prev.filter((d) => d !== day) : [...prev, day].sort((a, b) => a - b),
    );
  };

  const handleSave = () => {
    if (!profileId) return;
    onSave({
      id: initial?.id,
      profile_id: profileId,
      start_time: startTime,
      end_time: endTime,
      days_of_week: days.join(','),
    });
  };

  return (
    <>
      {/* Backdrop */}
      <div
        onClick={onClose}
        style={{
          position: 'fixed',
          inset: 0,
          zIndex: 1000,
        }}
      />

      {/* Popover card */}
      <div
        ref={popoverRef}
        style={{
          position: 'fixed',
          top: position.top,
          left: position.left,
          zIndex: 1001,
          background: 'var(--card-bg)',
          border: '1px solid var(--border)',
          borderRadius: '10px',
          padding: '16px',
          minWidth: '280px',
          maxWidth: '320px',
          boxShadow: '0 8px 24px rgba(0,0,0,0.35)',
          color: 'var(--text)',
        }}
      >
        <p style={{ fontWeight: 600, fontSize: '14px', marginBottom: '12px' }}>
          {initial?.id ? 'Edit Schedule' : 'New Schedule Entry'}
        </p>

        {/* Profile selector */}
        <label style={{ display: 'block', fontSize: '12px', color: 'var(--text-secondary)', marginBottom: '4px' }}>
          Profile
        </label>
        <select
          value={profileId}
          onChange={(e) => setProfileId(e.target.value)}
          style={{
            width: '100%',
            padding: '6px 8px',
            borderRadius: '6px',
            background: 'var(--bg)',
            border: '1px solid var(--border)',
            color: 'var(--text)',
            fontSize: '13px',
            marginBottom: '12px',
          }}
        >
          {profiles.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </select>

        {/* Time inputs */}
        <div style={{ display: 'flex', gap: '8px', marginBottom: '12px' }}>
          <div style={{ flex: 1 }}>
            <label style={{ display: 'block', fontSize: '12px', color: 'var(--text-secondary)', marginBottom: '4px' }}>
              Start
            </label>
            <input
              type="time"
              value={startTime}
              onChange={(e) => setStartTime(e.target.value)}
              style={{
                width: '100%',
                padding: '6px 8px',
                borderRadius: '6px',
                background: 'var(--bg)',
                border: '1px solid var(--border)',
                color: 'var(--text)',
                fontSize: '13px',
              }}
            />
          </div>
          <div style={{ flex: 1 }}>
            <label style={{ display: 'block', fontSize: '12px', color: 'var(--text-secondary)', marginBottom: '4px' }}>
              End
            </label>
            <input
              type="time"
              value={endTime}
              onChange={(e) => setEndTime(e.target.value)}
              style={{
                width: '100%',
                padding: '6px 8px',
                borderRadius: '6px',
                background: 'var(--bg)',
                border: '1px solid var(--border)',
                color: 'var(--text)',
                fontSize: '13px',
              }}
            />
          </div>
        </div>

        {/* Day-of-week toggles */}
        <label style={{ display: 'block', fontSize: '12px', color: 'var(--text-secondary)', marginBottom: '6px' }}>
          Days
        </label>
        <div style={{ display: 'flex', gap: '4px', marginBottom: '16px', flexWrap: 'wrap' }}>
          {DAY_LABELS.map((label, idx) => (
            <button
              key={idx}
              onClick={() => toggleDay(idx)}
              style={{
                padding: '4px 8px',
                borderRadius: '6px',
                border: '1px solid',
                fontSize: '11px',
                fontWeight: 500,
                cursor: 'pointer',
                background: days.includes(idx) ? 'var(--accent)' : 'var(--bg)',
                borderColor: days.includes(idx) ? 'var(--accent)' : 'var(--border)',
                color: days.includes(idx) ? '#fff' : 'var(--text-secondary)',
                transition: 'all 0.15s',
              }}
            >
              {label}
            </button>
          ))}
        </div>

        {/* Buttons */}
        <div style={{ display: 'flex', gap: '8px', justifyContent: 'flex-end' }}>
          {initial?.id && onDelete && (
            <button
              onClick={() => { if (initial.id) onDelete(initial.id); }}
              className="btn-secondary"
              style={{ color: 'var(--danger)', borderColor: 'var(--danger)', fontSize: '13px' }}
            >
              Delete
            </button>
          )}
          <button onClick={onClose} className="btn-secondary" style={{ fontSize: '13px' }}>
            Cancel
          </button>
          <button
            onClick={handleSave}
            disabled={!profileId || days.length === 0}
            className="btn-primary"
            style={{ fontSize: '13px' }}
          >
            Save
          </button>
        </div>
      </div>
    </>
  );
}
