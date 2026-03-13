'use client';

import React, { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import { ProfileSchedule } from '@/lib/types';
import { useScheduleGrid, DAY_LABELS, TIME_LABELS, profileColor } from '@/hooks/useScheduleGrid';
import { SchedulePopover } from './SchedulePopover';
import { useCanWrite } from '@/hooks/useCanWrite';
import { useToast } from '@/components/ui/ToastProvider';
import { useConfirm } from '@/components/ui/ConfirmDialog';

interface PopoverState {
  anchorRect: { top: number; left: number };
  initial?: {
    id?: string;
    profileId: string;
    startTime: string;
    endTime: string;
    days: number[];
  };
  /** When creating, which day column was clicked (0-6) */
  defaultDay?: number;
  /** When creating, which hour row was clicked */
  defaultHour?: number;
}

function nowMinutes(): number {
  const d = new Date();
  return d.getHours() * 60 + d.getMinutes();
}

/** Fraction of the day (0–1) that corresponds to the current time. */
function nowFraction(): number {
  return nowMinutes() / (24 * 60);
}

export function ScheduleCalendarGrid() {
  const canWrite = useCanWrite();
  const toast = useToast();
  const confirm = useConfirm();

  const [schedules, setSchedules] = useState<ProfileSchedule[]>([]);
  const [profiles, setProfiles] = useState<{ id: string; name: string }[]>([]);
  const [loading, setLoading] = useState(true);
  const [popover, setPopover] = useState<PopoverState | null>(null);

  // Mobile: which day column is selected (null = show all days, only used on mobile)
  const [mobileDay, setMobileDay] = useState<number>(() => {
    // Default to today's day (0=Mon ... 6=Sun); JS getDay: 0=Sun, 1=Mon ... 6=Sat
    const jsDay = new Date().getDay();
    return jsDay === 0 ? 6 : jsDay - 1;
  });
  const [isMobile, setIsMobile] = useState(false);

  // "Now" indicator — recalculate every 60s
  const [nowFrac, setNowFrac] = useState(nowFraction());
  useEffect(() => {
    const id = setInterval(() => setNowFrac(nowFraction()), 60_000);
    return () => clearInterval(id);
  }, []);

  // Responsive detection
  useEffect(() => {
    const check = () => setIsMobile(window.innerWidth < 640);
    check();
    window.addEventListener('resize', check);
    return () => window.removeEventListener('resize', check);
  }, []);

  const fetchData = useCallback(async () => {
    try {
      const [schedRes, profRes] = await Promise.all([
        api.profileSchedules.list(),
        api.getProfiles(),
      ]);
      setSchedules(schedRes.schedules ?? []);
      setProfiles((profRes.profiles ?? []).map((p: { id: string; name: string }) => ({
        id: p.id,
        name: p.name,
      })));
    } catch {
      toast('Failed to load schedule data.', 'error');
    } finally {
      setLoading(false);
    }
  }, [toast]);

  useEffect(() => { fetchData(); }, [fetchData]);

  const grid = useScheduleGrid(schedules, profiles);

  // ── Handlers ─────────────────────────────────────────────────────────────

  const handleCellClick = (
    e: React.MouseEvent,
    cell: { scheduleId: string | null; profileId: string | null; day: number; hour: number },
  ) => {
    if (!canWrite) return;
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const anchor = { top: rect.bottom + 6, left: rect.left };

    if (cell.scheduleId) {
      // Edit existing schedule
      const sched = schedules.find((s) => s.id === cell.scheduleId);
      if (!sched) return;
      setPopover({
        anchorRect: anchor,
        initial: {
          id: sched.id,
          profileId: sched.profile_id,
          startTime: sched.start_time,
          endTime: sched.end_time,
          days: sched.days_of_week.split(',').map(Number),
        },
      });
    } else {
      // New schedule — pre-fill the clicked day and a 2-hour window starting at clicked hour
      const endHour = (cell.hour + 2) % 24;
      const pad = (n: number) => String(n).padStart(2, '0');
      setPopover({
        anchorRect: anchor,
        defaultDay: cell.day,
        defaultHour: cell.hour,
        initial: {
          profileId: profiles[0]?.id ?? '',
          startTime: `${pad(cell.hour)}:00`,
          endTime: `${pad(endHour)}:00`,
          days: [cell.day],
        },
      });
    }
  };

  const handleSave = async (data: {
    id?: string;
    profile_id: string;
    start_time: string;
    end_time: string;
    days_of_week: string;
  }) => {
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      if (data.id) {
        await api.profileSchedules.update(data.id, {
          profile_id: data.profile_id,
          start_time: data.start_time,
          end_time: data.end_time,
          days_of_week: data.days_of_week,
          timezone: tz,
        });
        toast('Schedule updated.', 'success');
      } else {
        await api.profileSchedules.create({
          profile_id: data.profile_id,
          start_time: data.start_time,
          end_time: data.end_time,
          days_of_week: data.days_of_week,
          timezone: tz,
        });
        toast('Schedule created.', 'success');
      }
      setPopover(null);
      await fetchData();
    } catch {
      toast('Failed to save schedule.', 'error');
    }
  };

  const handleDelete = async (id: string) => {
    const ok = await confirm({ message: 'Delete this schedule entry?', danger: true });
    if (!ok) return;
    try {
      await api.profileSchedules.delete(id);
      toast('Schedule deleted.', 'success');
      setPopover(null);
      await fetchData();
    } catch {
      toast('Failed to delete schedule.', 'error');
    }
  };

  // ── Render helpers ────────────────────────────────────────────────────────

  /** Which day columns to render (all 7 on desktop, 1 on mobile). */
  const visibleDays: number[] = isMobile ? [mobileDay] : [0, 1, 2, 3, 4, 5, 6];

  const CELL_H = 40; // px per 2-hour row
  const AXIS_W = 50; // px for time axis

  if (loading) {
    return (
      <div style={{ padding: '32px', textAlign: 'center', color: 'var(--text-secondary)' }}>
        Loading schedule…
      </div>
    );
  }

  return (
    <div style={{ position: 'relative' }}>
      {/* Mobile day selector tabs */}
      {isMobile && (
        <div style={{ display: 'flex', overflowX: 'auto', gap: '4px', marginBottom: '8px', paddingBottom: '4px' }}>
          {DAY_LABELS.map((label, idx) => (
            <button
              key={idx}
              onClick={() => setMobileDay(idx)}
              style={{
                padding: '6px 12px',
                borderRadius: '6px',
                border: '1px solid',
                fontSize: '12px',
                fontWeight: mobileDay === idx ? 600 : 400,
                cursor: 'pointer',
                whiteSpace: 'nowrap',
                background: mobileDay === idx ? 'var(--accent)' : 'var(--bg)',
                borderColor: mobileDay === idx ? 'var(--accent)' : 'var(--border)',
                color: mobileDay === idx ? '#fff' : 'var(--text-secondary)',
              }}
            >
              {label}
            </button>
          ))}
        </div>
      )}

      {/* Grid wrapper — relative for the "Now" line */}
      <div style={{ overflowX: 'auto' }}>
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: `${AXIS_W}px ${visibleDays.map(() => '1fr').join(' ')}`,
            position: 'relative',
            minWidth: isMobile ? 0 : '520px',
          }}
        >
          {/* Header row: empty axis + day labels */}
          <div style={{ height: '32px' }} />
          {visibleDays.map((dayIdx) => (
            <div
              key={dayIdx}
              style={{
                height: '32px',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                fontSize: '12px',
                fontWeight: 600,
                color: 'var(--text-secondary)',
                borderBottom: '1px solid var(--border)',
              }}
            >
              {DAY_LABELS[dayIdx]}
            </div>
          ))}

          {/* Time rows */}
          {TIME_LABELS.map((timeLabel, rowIdx) => (
            <React.Fragment key={rowIdx}>
              {/* Time axis label */}
              <div
                style={{
                  height: `${CELL_H}px`,
                  display: 'flex',
                  alignItems: 'flex-start',
                  paddingTop: '4px',
                  paddingRight: '8px',
                  justifyContent: 'flex-end',
                  fontSize: '11px',
                  color: 'var(--text-secondary)',
                  flexShrink: 0,
                }}
              >
                {timeLabel}
              </div>

              {/* Day cells for this row */}
              {visibleDays.map((dayIdx) => {
                const cell = grid[rowIdx]?.[dayIdx];
                const hasSchedule = !!cell?.scheduleId;
                const colors = hasSchedule && cell.profileName
                  ? profileColor(cell.profileName)
                  : null;

                return (
                  <div
                    key={dayIdx}
                    onClick={(e) => cell && handleCellClick(e, cell)}
                    title={
                      hasSchedule && cell.profileName
                        ? cell.profileName
                        : canWrite
                        ? 'Click to add schedule'
                        : undefined
                    }
                    style={{
                      height: `${CELL_H}px`,
                      border: '1px solid var(--surface-200)',
                      borderRadius: '4px',
                      margin: '1px',
                      cursor: canWrite ? 'pointer' : 'default',
                      background: colors ? colors.bg : 'transparent',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      transition: 'opacity 0.1s',
                      overflow: 'hidden',
                    }}
                    onMouseEnter={(e) => {
                      if (canWrite) (e.currentTarget as HTMLElement).style.opacity = '0.8';
                    }}
                    onMouseLeave={(e) => {
                      (e.currentTarget as HTMLElement).style.opacity = '1';
                    }}
                  >
                    {hasSchedule && colors && cell.profileName && (
                      <span
                        style={{
                          fontSize: '10px',
                          fontWeight: 600,
                          color: colors.text,
                          padding: '0 4px',
                          whiteSpace: 'nowrap',
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          maxWidth: '100%',
                        }}
                      >
                        {cell.profileName}
                      </span>
                    )}
                  </div>
                );
              })}
            </React.Fragment>
          ))}

          {/* "Now" indicator — horizontal line across the grid area */}
          {(() => {
            const gridBodyHeight = TIME_LABELS.length * CELL_H;
            const headerH = 32;
            const topPx = headerH + nowFrac * gridBodyHeight;
            return (
              <div
                style={{
                  position: 'absolute',
                  top: `${topPx}px`,
                  left: `${AXIS_W}px`,
                  right: '0',
                  height: '2px',
                  background: 'var(--accent)',
                  pointerEvents: 'none',
                  zIndex: 10,
                  opacity: 0.8,
                }}
              >
                {/* Small circle at left edge */}
                <div
                  style={{
                    position: 'absolute',
                    left: '-4px',
                    top: '-3px',
                    width: '8px',
                    height: '8px',
                    borderRadius: '50%',
                    background: 'var(--accent)',
                  }}
                />
              </div>
            );
          })()}
        </div>
      </div>

      {/* Legend */}
      {profiles.length > 0 && (
        <div
          style={{
            marginTop: '12px',
            display: 'flex',
            flexWrap: 'wrap',
            gap: '8px',
            padding: '8px 0',
            borderTop: '1px solid var(--border)',
          }}
        >
          {profiles.map((p) => {
            const colors = profileColor(p.name);
            return (
              <span
                key={p.id}
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  gap: '5px',
                  fontSize: '11px',
                  color: 'var(--text-secondary)',
                }}
              >
                <span
                  style={{
                    display: 'inline-block',
                    width: '10px',
                    height: '10px',
                    borderRadius: '2px',
                    background: colors.bg,
                    border: `1px solid ${colors.text}`,
                    flexShrink: 0,
                  }}
                />
                {p.name}
              </span>
            );
          })}
        </div>
      )}

      {!canWrite && (
        <p style={{ fontSize: '12px', color: 'var(--text-secondary)', marginTop: '8px' }}>
          Viewer mode — schedule editing is disabled.
        </p>
      )}

      {/* Popover */}
      {popover && (
        <SchedulePopover
          anchorRect={popover.anchorRect}
          initial={popover.initial}
          profiles={profiles}
          onSave={handleSave}
          onDelete={handleDelete}
          onClose={() => setPopover(null)}
        />
      )}
    </div>
  );
}
