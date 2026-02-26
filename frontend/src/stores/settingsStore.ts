import { create } from 'zustand';
import type { TempUnit } from '@/lib/tempUnit';

interface SettingsState {
  tempUnit: TempUnit;
  sensorLabels: Record<string, string>;
  notificationsEnabled: boolean;
  setTempUnit: (unit: TempUnit) => void;
  setSensorLabels: (labels: Record<string, string>) => void;
  setSensorLabel: (sensorId: string, label: string) => void;
  removeSensorLabel: (sensorId: string) => void;
  setNotificationsEnabled: (enabled: boolean) => void;
}

function loadNotificationPref(): boolean {
  try {
    return localStorage.getItem('drivechill_notifications') === 'true';
  } catch {
    return false;
  }
}

export const useSettingsStore = create<SettingsState>((set) => ({
  tempUnit: 'C',
  sensorLabels: {},
  notificationsEnabled: typeof window !== 'undefined' ? loadNotificationPref() : false,
  setTempUnit: (unit) => set({ tempUnit: unit }),
  setSensorLabels: (labels) => set({ sensorLabels: labels }),
  setSensorLabel: (sensorId, label) =>
    set((state) => ({
      sensorLabels: { ...state.sensorLabels, [sensorId]: label },
    })),
  removeSensorLabel: (sensorId) =>
    set((state) => {
      const { [sensorId]: _, ...rest } = state.sensorLabels;
      return { sensorLabels: rest };
    }),
  setNotificationsEnabled: (enabled) => {
    try { localStorage.setItem('drivechill_notifications', String(enabled)); } catch { /* best-effort */ }
    set({ notificationsEnabled: enabled });
  },
}));
