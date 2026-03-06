import { create } from 'zustand';
import type { SensorReading, AlertEvent, Page, Profile, FanCurve, FanTestProgress, SafeModeStatus, UpdateCheck, ControlSource } from '@/lib/types';

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
  controlSources: Record<string, ControlSource>;
  setControlSources: (sources: Record<string, ControlSource>) => void;
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

  // Safe-mode / panic state
  safeMode: SafeModeStatus;
  setSafeMode: (status: SafeModeStatus) => void;

  // Drive → curve navigation
  preselectedCurveSensorId: string | null;
  setPreselectedCurveSensorId: (sensorId: string | null) => void;

  // Drive → create new cooling curve
  createCoolingCurveSensorId: string | null;
  setCreateCoolingCurveSensorId: (sensorId: string | null) => void;

  // Connection
  connected: boolean;
  setConnected: (connected: boolean) => void;
  backendName: string;
  setBackendName: (name: string) => void;

  // Update check
  updateCheck: UpdateCheck | null;
  setUpdateCheck: (info: UpdateCheck | null) => void;
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
  controlSources: {},
  setControlSources: (sources) => set({ controlSources: sources }),
  curves: [],
  setCurves: (curves) => set({ curves }),

  // Profiles
  profiles: [],
  setProfiles: (profiles) => set({ profiles }),

  // Alerts
  alertEvents: [],
  addAlertEvents: (events) =>
    set((state) => {
      const existing = new Set(state.alertEvents.map((e) => `${e.timestamp}|${e.message}`));
      const novel = events.filter((e) => !existing.has(`${e.timestamp}|${e.message}`));
      if (novel.length === 0) return state;
      return { alertEvents: [...state.alertEvents, ...novel].slice(-100) };
    }),
  activeAlerts: [],
  setActiveAlerts: (alerts) => set({ activeAlerts: alerts }),
  clearAlerts: () => set({ alertEvents: [], activeAlerts: [] }),

  // Fan benchmark progress
  fanTestProgress: [],
  setFanTestProgress: (progress) => set({ fanTestProgress: progress }),

  // Safe-mode / panic state
  safeMode: { active: false, released: false, reason: null },
  setSafeMode: (status) => set({ safeMode: status }),

  // Drive → curve navigation
  preselectedCurveSensorId: null,
  setPreselectedCurveSensorId: (sensorId) => set({ preselectedCurveSensorId: sensorId }),

  // Drive → create new cooling curve
  createCoolingCurveSensorId: null,
  setCreateCoolingCurveSensorId: (sensorId) => set({ createCoolingCurveSensorId: sensorId }),

  // Connection
  connected: false,
  setConnected: (connected) => set({ connected }),
  backendName: '',
  setBackendName: (name) => set({ backendName: name }),

  // Update check
  updateCheck: null,
  setUpdateCheck: (info) => set({ updateCheck: info }),
}));
