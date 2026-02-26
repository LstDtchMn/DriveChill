const API_BASE = process.env.NEXT_PUBLIC_API_URL || 'http://localhost:8085';

export class APIError extends Error {
  status: number;
  detail: unknown;

  constructor(status: number, message: string, detail: unknown) {
    super(message);
    this.name = 'APIError';
    this.status = status;
    this.detail = detail;
  }
}

async function fetchAPI<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${API_BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (!res.ok) {
    let detail: unknown = null;
    try {
      detail = await res.json();
    } catch {
      detail = null;
    }
    throw new APIError(res.status, `API error: ${res.status} ${res.statusText}`, detail);
  }
  return res.json();
}

export const api = {
  // Sensors
  getSensors: () => fetchAPI<{ readings: any[]; backend: string }>('/api/sensors'),
  getHistory: (sensorId?: string, hours = 1) =>
    fetchAPI<{ data: any[] }>(`/api/sensors/history?hours=${hours}${sensorId ? `&sensor_id=${sensorId}` : ''}`),

  // Fans
  getFans: () => fetchAPI<{ fans: string[] }>('/api/fans'),
  getFanStatus: () => fetchAPI<{ safe_mode: any; curves_active: number; applied_speeds: Record<string, number> }>('/api/fans/status'),
  releaseFanControl: () => fetchAPI<{ success: boolean; message: string }>('/api/fans/release', { method: 'POST' }),
  resumeFanControl: () => fetchAPI<{ success: boolean; active_profile: any }>('/api/fans/resume', { method: 'POST' }),
  getCurves: () => fetchAPI<{ curves: any[] }>('/api/fans/curves'),
  updateCurve: (curve: any, allowDangerous = false) =>
    fetchAPI('/api/fans/curves', {
      method: 'PUT',
      body: JSON.stringify({ curve, allow_dangerous: allowDangerous }),
    }),
  deleteCurve: (id: string) =>
    fetchAPI(`/api/fans/curves/${id}`, { method: 'DELETE' }),

  // Profiles
  getProfiles: () => fetchAPI<{ profiles: any[] }>('/api/profiles'),
  activateProfile: (id: string) =>
    fetchAPI(`/api/profiles/${id}/activate`, { method: 'PUT' }),
  getPresetCurves: (id: string) => fetchAPI<{ points: any[] }>(`/api/profiles/${id}/preset-curves`),

  // Alerts
  getAlerts: () => fetchAPI<{ rules: any[]; events: any[]; active: string[] }>('/api/alerts'),
  addAlertRule: (rule: any) =>
    fetchAPI('/api/alerts/rules', { method: 'POST', body: JSON.stringify(rule) }),
  deleteAlertRule: (id: string) =>
    fetchAPI(`/api/alerts/rules/${id}`, { method: 'DELETE' }),
  clearAlerts: () => fetchAPI('/api/alerts/clear', { method: 'POST' }),

  // Settings
  getSettings: () => fetchAPI<any>('/api/settings'),
  updateSettings: (settings: any) =>
    fetchAPI('/api/settings', { method: 'PUT', body: JSON.stringify(settings) }),

  // Fan benchmarks
  fanTests: {
    start: (fanId: string, options?: Partial<{ steps: number; settle_ms: number; min_rpm_threshold: number }>) =>
      fetchAPI<{ ok: boolean; fan_id: string; estimated_duration_s: number }>(
        `/api/fans/${fanId}/test`,
        { method: 'POST', body: JSON.stringify(options ?? {}) }
      ),
    getResult: (fanId: string) =>
      fetchAPI<import('./types').FanTestResult>(`/api/fans/${fanId}/test`),
    cancel: (fanId: string) =>
      fetchAPI<{ ok: boolean }>(`/api/fans/${fanId}/test`, { method: 'DELETE' }),
  },

  // Health
  health: () => fetchAPI<any>('/api/health'),
};
