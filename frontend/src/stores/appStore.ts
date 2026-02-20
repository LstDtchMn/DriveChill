import { create } from 'zustand';
import type { SensorReading, AlertEvent, Page, Profile, FanCurve, FanTestProgress } from '@/lib/types';

interface HistoryPoint {
  timestamp: string;
  readings: SensorReading[];
}

interface AppState {
  // Navigation
  currentPage: Page;
  setPage: (page: Page) => void;

  // Theme
  isDark: boolean;
  toggleTheme: () => void;

  // Sensor data
  readings: SensorReading[];
  setReadings: (readings: SensorReading[]) => void;
  history: HistoryPoint[];
  addHistoryPoint: (point: HistoryPoint) => void;

  // Fan control
  appliedSpeeds: Record<string, number>;
  setAppliedSpeeds: (speeds: Record<string, number>) => void;
  curves: FanCurve[];
  setCurves: (curves: FanCurve[]) => void;

  // Profiles
  profiles: Profile[];
  setProfiles: (profiles: Profile[]) => void;

  // Alerts
  alertEvents: AlertEvent[];
  addAlertEvents: (events: AlertEvent[]) => void;
  activeAlerts: string[];
  setActiveAlerts: (alerts: string[]) => void;
  clearAlerts: () => void;

  // Fan benchmark progress (live, from WebSocket)
  fanTestProgress: FanTestProgress[];
  setFanTestProgress: (progress: FanTestProgress[]) => void;

  // Connection
  connected: boolean;
  setConnected: (connected: boolean) => void;
  backendName: string;
  setBackendName: (name: string) => void;
}

export const useAppStore = create<AppState>((set) => ({
  // Navigation
  currentPage: 'dashboard',
  setPage: (page) => set({ currentPage: page }),

  // Theme
  isDark: true,
  toggleTheme: () =>
    set((state) => {
      const newDark = !state.isDark;
      if (typeof document !== 'undefined') {
        document.documentElement.classList.toggle('dark', newDark);
      }
      return { isDark: newDark };
    }),

  // Sensor data
  readings: [],
  setReadings: (readings) => set({ readings }),
  history: [],
  addHistoryPoint: (point) =>
    set((state) => {
      const history = [...state.history, point];
      // Keep last 5 minutes (300 points at 1/s)
      if (history.length > 300) {
        return { history: history.slice(-300) };
      }
      return { history };
    }),

  // Fan control
  appliedSpeeds: {},
  setAppliedSpeeds: (speeds) => set({ appliedSpeeds: speeds }),
  curves: [],
  setCurves: (curves) => set({ curves }),

  // Profiles
  profiles: [],
  setProfiles: (profiles) => set({ profiles }),

  // Alerts
  alertEvents: [],
  addAlertEvents: (events) =>
    set((state) => ({
      alertEvents: [...state.alertEvents, ...events].slice(-100),
    })),
  activeAlerts: [],
  setActiveAlerts: (alerts) => set({ activeAlerts: alerts }),
  clearAlerts: () => set({ alertEvents: [], activeAlerts: [] }),

  // Fan benchmark progress
  fanTestProgress: [],
  setFanTestProgress: (progress) => set({ fanTestProgress: progress }),

  // Connection
  connected: false,
  setConnected: (connected) => set({ connected }),
  backendName: '',
  setBackendName: (name) => set({ backendName: name }),
}));
