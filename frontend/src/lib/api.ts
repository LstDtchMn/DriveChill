const DEFAULT_API_BASE = 'http://localhost:8085';
const DEFAULT_WS_URL = 'ws://localhost:8085/api/ws';

function normalizeLoopbackHost(value: string): string {
  // Guard against malformed loopback forms like 127.0.01 or 127.0.1.
  // Use regex with word boundary / port-colon / slash / end-of-string to avoid
  // mangling valid IPs like 127.0.1.1.
  return value
    .replace(/:\/\/127\.0\.01(?=[:/]|$)/, '://127.0.0.1')
    .replace(/:\/\/127\.0\.1(?=[:/]|$)/, '://127.0.0.1');
}

function resolveApiBase(raw?: string): string {
  const candidate = normalizeLoopbackHost((raw ?? '').trim());
  if (!candidate) return DEFAULT_API_BASE;

  try {
    const url = new URL(candidate);
    return `${url.protocol}//${url.host}`;
  } catch {
    return DEFAULT_API_BASE;
  }
}

function resolveWsUrl(raw: string | undefined, apiBase: string): string {
  const candidate = normalizeLoopbackHost((raw ?? '').trim());
  if (candidate) {
    try {
      return new URL(candidate).toString();
    } catch {
      // Fall through to API-derived URL.
    }
  }

  try {
    const apiUrl = new URL(apiBase);
    apiUrl.protocol = apiUrl.protocol === 'https:' ? 'wss:' : 'ws:';
    apiUrl.pathname = '/api/ws';
    apiUrl.search = '';
    apiUrl.hash = '';
    return apiUrl.toString();
  } catch {
    return DEFAULT_WS_URL;
  }
}

const API_BASE = resolveApiBase(process.env.NEXT_PUBLIC_API_URL);
const WS_URL = resolveWsUrl(process.env.NEXT_PUBLIC_WS_URL, API_BASE);

export function getApiBaseUrl(): string {
  return API_BASE;
}

export function getWsUrl(): string {
  return WS_URL;
}

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

/**
 * Read the CSRF cookie that the backend sets on login.
 * Returns the token string, or null if the cookie is absent.
 */
function getCsrfToken(): string | null {
  if (typeof document === 'undefined') return null;
  const match = document.cookie.match(/(?:^|;\s*)drivechill_csrf=([^;]+)/);
  return match ? decodeURIComponent(match[1]) : null;
}

async function fetchAPI<T>(path: string, options?: RequestInit): Promise<T> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...(options?.headers as Record<string, string> | undefined),
  };

  // Inject CSRF token on state-changing requests
  if (['POST', 'PUT', 'DELETE', 'PATCH'].includes(method)) {
    const csrf = getCsrfToken();
    if (csrf) {
      headers['X-CSRF-Token'] = csrf;
    }
  }

  const res = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers,
    credentials: 'include',
  });

  if (res.status === 401) {
    // Notify the app that the session has expired
    if (typeof window !== 'undefined') {
      window.dispatchEvent(new Event('drivechill:auth-expired'));
    }
  }

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

/** Auth-specific API calls. */
export const authApi = {
  checkSession: () =>
    fetchAPI<{ auth_required: boolean; authenticated: boolean; username?: string }>(
      '/api/auth/session'
    ),
  login: (username: string, password: string) =>
    fetchAPI<{ success: boolean; username: string }>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }),
  logout: () =>
    fetchAPI<{ success: boolean }>('/api/auth/logout', { method: 'POST' }),
  setup: (username: string, password: string) =>
    fetchAPI<{ success: boolean; username: string }>('/api/auth/setup', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }),
  status: () =>
    fetchAPI<{ auth_enabled: boolean }>('/api/auth/status'),
};

export const api = {
  // Sensors
  getSensors: () => fetchAPI<{ readings: any[]; backend: string }>('/api/sensors'),
  getSensorLabels: () => fetchAPI<{ labels: Record<string, string> }>('/api/sensors/labels'),
  setSensorLabel: (sensorId: string, label: string) =>
    fetchAPI(`/api/sensors/${encodeURIComponent(sensorId)}/label`, {
      method: 'PUT',
      body: JSON.stringify({ label }),
    }),
  deleteSensorLabel: (sensorId: string) =>
    fetchAPI(`/api/sensors/${encodeURIComponent(sensorId)}/label`, { method: 'DELETE' }),
  getHistory: (sensorId?: string, hours = 1) =>
    fetchAPI<{ data: any[] }>(`/api/sensors/history?hours=${hours}${sensorId ? `&sensor_id=${encodeURIComponent(sensorId)}` : ''}`),

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

  // Fan settings (min speed floor, zero-RPM)
  getAllFanSettings: () =>
    fetchAPI<{ fan_settings: Record<string, { min_speed_pct: number; zero_rpm_capable: boolean }> }>('/api/fans/settings'),
  getFanSettings: (fanId: string) =>
    fetchAPI<{ fan_id: string; min_speed_pct: number; zero_rpm_capable: boolean }>(`/api/fans/${fanId}/settings`),
  updateFanSettings: (fanId: string, settings: { min_speed_pct: number; zero_rpm_capable: boolean }) =>
    fetchAPI(`/api/fans/${fanId}/settings`, {
      method: 'PUT',
      body: JSON.stringify(settings),
    }),

  // Profiles
  getProfiles: () => fetchAPI<{ profiles: any[] }>('/api/profiles'),
  activateProfile: (id: string) =>
    fetchAPI(`/api/profiles/${id}/activate`, { method: 'PUT' }),
  getPresetCurves: (id: string) => fetchAPI<{ points: any[] }>(`/api/profiles/${id}/preset-curves`),
  exportProfile: (id: string) =>
    fetchAPI<{ export_version: number; profile: any }>(`/api/profiles/${id}/export`),
  importProfile: (profile: { name?: string; preset?: string; curves?: any[] }) =>
    fetchAPI<{ success: boolean; profile: any }>('/api/profiles/import', {
      method: 'POST',
      body: JSON.stringify(profile),
    }),

  // Alerts
  getAlerts: () => fetchAPI<{ rules: any[]; events: any[]; active: string[] }>('/api/alerts'),
  addAlertRule: (rule: any) =>
    fetchAPI<{ success: boolean; rule: import('@/lib/types').AlertRule }>('/api/alerts/rules', { method: 'POST', body: JSON.stringify(rule) }),
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
  health: () =>
    fetchAPI<{
      status: string;
      app: string;
      api_version: string;
      capabilities: string[];
      version: string;
      backend: string;
    }>('/api/health'),

  // Multi-machine hub registry / snapshots
  getMachines: () =>
    fetchAPI<{ machines: import('./types').MachineInfo[] }>('/api/machines'),
  createMachine: (machine: {
    name: string;
    base_url: string;
    api_key?: string;
    enabled?: boolean;
    poll_interval_seconds?: number;
    timeout_ms?: number;
  }) =>
    fetchAPI<{ machine: import('./types').MachineInfo }>('/api/machines', {
      method: 'POST',
      body: JSON.stringify(machine),
    }),
  updateMachine: (
    machineId: string,
    machine: Partial<{
      name: string;
      base_url: string;
      api_key: string;
      enabled: boolean;
      poll_interval_seconds: number;
      timeout_ms: number;
    }>
  ) =>
    fetchAPI<{ machine: import('./types').MachineInfo }>(
      `/api/machines/${encodeURIComponent(machineId)}`,
      {
        method: 'PUT',
        body: JSON.stringify(machine),
      }
    ),
  deleteMachine: (machineId: string) =>
    fetchAPI<{ success: boolean }>(`/api/machines/${encodeURIComponent(machineId)}`, {
      method: 'DELETE',
    }),
  getMachineSnapshot: (machineId: string) =>
    fetchAPI<{ machine_id: string; snapshot: import('./types').MachineSnapshot }>(
      `/api/machines/${encodeURIComponent(machineId)}/snapshot`
    ),
  verifyMachine: (machineId: string) =>
    fetchAPI<{ success: boolean; status: string; snapshot?: import('./types').MachineSnapshot; error?: string }>(
      `/api/machines/${encodeURIComponent(machineId)}/verify`,
      { method: 'POST' }
    ),
  getMachineState: (machineId: string) =>
    fetchAPI<{ state: import('./types').MachineRemoteState }>(`/api/machines/${encodeURIComponent(machineId)}/state`),
  activateRemoteProfile: (machineId: string, profileId: string) =>
    fetchAPI<{ success: boolean }>(`/api/machines/${encodeURIComponent(machineId)}/profiles/${encodeURIComponent(profileId)}/activate`, { method: 'POST' }),
  releaseRemoteFans: (machineId: string) =>
    fetchAPI<{ success: boolean }>(`/api/machines/${encodeURIComponent(machineId)}/fans/release`, { method: 'POST' }),
  updateRemoteFanSettings: (machineId: string, fanId: string, settings: { min_speed_pct?: number; zero_rpm_capable?: boolean }) =>
    fetchAPI<{ success: boolean }>(`/api/machines/${encodeURIComponent(machineId)}/fans/${encodeURIComponent(fanId)}/settings`, {
      method: 'PUT',
      body: JSON.stringify(settings),
    }),

  // Notifications (push subscriptions + email)
  notifications: {
    // Push subscriptions
    listPushSubscriptions: () =>
      fetchAPI<{ subscriptions: import('./types').PushSubscription[] }>('/api/notifications/push-subscriptions'),
    createPushSubscription: (sub: { endpoint: string; p256dh: string; auth: string; user_agent?: string }) =>
      fetchAPI<{ success: boolean; subscription: import('./types').PushSubscription }>('/api/notifications/push-subscriptions', {
        method: 'POST',
        body: JSON.stringify(sub),
      }),
    deletePushSubscription: (id: string) =>
      fetchAPI<{ success: boolean }>(`/api/notifications/push-subscriptions/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    testPushSubscription: (subscriptionId: string) =>
      fetchAPI<{ success: boolean }>('/api/notifications/push-subscriptions/test', {
        method: 'POST',
        body: JSON.stringify({ subscription_id: subscriptionId }),
      }),
    // Email
    getEmailSettings: () =>
      fetchAPI<{ settings: import('./types').EmailNotificationSettings }>('/api/notifications/email'),
    updateEmailSettings: (settings: Partial<{
      enabled: boolean; smtp_host: string; smtp_port: number;
      smtp_username: string; smtp_password: string;
      sender_address: string; recipient_list: string[];
      use_tls: boolean; use_ssl: boolean;
    }>) =>
      fetchAPI<{ success: boolean; settings: import('./types').EmailNotificationSettings }>('/api/notifications/email', {
        method: 'PUT',
        body: JSON.stringify(settings),
      }),
    testEmail: () =>
      fetchAPI<{ success: boolean; error: string | null }>('/api/notifications/email/test', { method: 'POST' }),
  },

  // API keys (agent-to-hub auth)
  listApiKeys: () =>
    fetchAPI<{ api_keys: import('./types').ApiKeyInfo[] }>('/api/auth/api-keys'),
  createApiKey: (name: string, scopes?: string[]) =>
    fetchAPI<{ api_key: import('./types').ApiKeyInfo; plaintext_key: string }>('/api/auth/api-keys', {
      method: 'POST',
      body: JSON.stringify(scopes && scopes.length > 0 ? { name, scopes } : { name }),
    }),
  revokeApiKey: (keyId: string) =>
    fetchAPI<{ success: boolean }>(`/api/auth/api-keys/${encodeURIComponent(keyId)}`, {
      method: 'DELETE',
    }),

  // Webhooks
  getWebhookConfig: () =>
    fetchAPI<{ config: import('./types').WebhookConfig }>('/api/webhooks'),
  updateWebhookConfig: (config: Partial<import('./types').WebhookConfig> & { signing_secret?: string | null }) =>
    fetchAPI<{ success: boolean; config: import('./types').WebhookConfig }>('/api/webhooks', {
      method: 'PUT',
      body: JSON.stringify(config),
    }),
  getWebhookDeliveries: (limit = 100, offset = 0) =>
    fetchAPI<{ deliveries: import('./types').WebhookDelivery[] }>(
      `/api/webhooks/deliveries?limit=${Math.max(1, Math.min(500, limit))}&offset=${Math.max(0, offset)}`
    ),

  // Analytics
  analytics: {
    getHistory: (hours = 1.0, sensorId?: string, bucketSeconds = 60) =>
      fetchAPI<{ buckets: import('./types').AnalyticsBucket[] }>(
        `/api/analytics/history?hours=${hours}&bucket_seconds=${bucketSeconds}${sensorId ? `&sensor_id=${encodeURIComponent(sensorId)}` : ''}`
      ),
    getStats: (hours = 24.0, sensorId?: string) =>
      fetchAPI<{ stats: import('./types').AnalyticsStat[] }>(
        `/api/analytics/stats?hours=${hours}${sensorId ? `&sensor_id=${encodeURIComponent(sensorId)}` : ''}`
      ),
    getAnomalies: (hours = 24.0, zScoreThreshold = 3.0) =>
      fetchAPI<{ anomalies: import('./types').AnalyticsAnomaly[] }>(
        `/api/analytics/anomalies?hours=${hours}&z_score_threshold=${zScoreThreshold}`
      ),
    getReport: (hours = 24.0) =>
      fetchAPI<{
        generated_at: string;
        window_hours: number;
        stats: import('./types').AnalyticsStat[];
        anomalies: import('./types').AnalyticsAnomaly[];
        top_anomalous_sensors: { sensor_id: string; sensor_name: string; count: number }[];
      }>(`/api/analytics/report?hours=${hours}`),
  },
};
