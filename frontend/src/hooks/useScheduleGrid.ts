'use client';
import { useMemo } from 'react';
import type { ProfileSchedule } from '@/lib/types';

/** 0=Monday, 6=Sunday */
export const DAY_LABELS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;
export const TIME_LABELS = [
  '00:00', '02:00', '04:00', '06:00', '08:00', '10:00',
  '12:00', '14:00', '16:00', '18:00', '20:00', '22:00',
] as const;

export interface GridCell {
  day: number;       // 0-6
  hour: number;      // 0,2,4,...22
  scheduleId: string | null;
  profileId: string | null;
  profileName: string | null;
}

interface Profile {
  id: string;
  name: string;
}

/**
 * Deterministic color from profile name → HSL hue.
 * Same name always produces the same color.
 */
export function profileColor(name: string): { bg: string; text: string } {
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = name.charCodeAt(i) + ((hash << 5) - hash);
  }
  const hue = Math.abs(hash) % 360;
  return {
    bg: `hsla(${hue}, 40%, 25%, 1)`,
    text: `hsla(${hue}, 70%, 70%, 1)`,
  };
}

/**
 * Parse "HH:MM" → total minutes since midnight.
 */
function parseTime(hhmm: string): number {
  const [h, m] = hhmm.split(':').map(Number);
  return h * 60 + m;
}

export function useScheduleGrid(
  schedules: ProfileSchedule[],
  profiles: Profile[],
): GridCell[][] {
  return useMemo(() => {
    const profileMap = new Map(profiles.map(p => [p.id, p.name]));

    // Initialize 12 rows × 7 cols
    const grid: GridCell[][] = Array.from({ length: 12 }, (_, row) =>
      Array.from({ length: 7 }, (_, col) => ({
        day: col,
        hour: row * 2,
        scheduleId: null,
        profileId: null,
        profileName: null,
      })),
    );

    for (const sched of schedules) {
      if (!sched.enabled) continue;
      const days = sched.days_of_week.split(',').map(Number);
      const startMin = parseTime(sched.start_time);
      const endMin = parseTime(sched.end_time);

      for (const day of days) {
        if (day < 0 || day > 6) continue;

        // Handle overnight (endMin <= startMin means wraps past midnight)
        const isOvernight = endMin <= startMin;
        for (let row = 0; row < 12; row++) {
          const cellStart = row * 120; // minutes
          const cellEnd = cellStart + 120;

          let inRange: boolean;
          if (isOvernight) {
            // e.g., 22:00→06:00: cell is covered if cellStart >= 22:00 OR cellEnd <= 06:00
            inRange = cellStart >= startMin || cellEnd <= endMin;
          } else {
            inRange = cellStart >= startMin && cellStart < endMin;
          }

          if (inRange) {
            grid[row][day] = {
              day,
              hour: row * 2,
              scheduleId: sched.id,
              profileId: sched.profile_id,
              profileName: profileMap.get(sched.profile_id) ?? sched.profile_id,
            };
          }
        }
      }
    }

    return grid;
  }, [schedules, profiles]);
}
