// In production the static export is served by the backend itself, so
// window.location.origin is the correct API base.  The hardcoded localhost
// fallback only applies during SSR/build (where window is unavailable).
const DEFAULT_API_BASE =
  typeof window !== 'undefined' ? window.location.origin : 'http://localhost:8085';
const DEFAULT_WS_URL =
  typeof window !== 'undefined'
    ? `${window.location.protocol === 'https:' ? 'wss:' : 'ws:'}//${window.location.host}/api/ws`
    : 'ws://localhost:8085/api/ws';

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

  if (!res.ok) {
    // Dispatch auth/forbidden events only on actual errors
    if (res.status === 401 && typeof window !== 'undefined') {
      window.dispatchEvent(new Event('drivechill:auth-expired'));
    }
    if (res.status === 403 && typeof window !== 'undefined') {
      window.dispatchEvent(new Event('drivechill:forbidden'));
    }
    let detail: unknown = null;
    try {
      detail = await res.json();
    } catch {
      detail = null;
    }
    throw new APIError(res.status, `API error: ${res.status} ${res.statusText}`, detail);
  }

  // HTTP 204 No Content — skip body parsing
  if (res.status === 204) return undefined as T;
  return res.json();
}

/** Auth-specific API calls. */
export const authApi = {
  checkSession: () =>
    fetchAPI<{ auth_required: boolean; authenticated: boolean; username?: string; role?: string }>(
      '/api/auth/session'
    ),
  listUsers: () =>
    fetchAPI<{ users: Array<{ id: number; username: string; role: string; created_at: string }> }>(
      '/api/auth/users'
    ),
  createUser: (username: string, password: string, role: string) =>
    fetchAPI<{ success: boolean; username: string; role: string }>('/api/auth/users', {
      method: 'POST',
      body: JSON.stringify({ username, password, role }),
    }),
  setUserRole: (userId: number, role: string) =>
    fetchAPI<{ success: boolean }>(`/api/auth/users/${userId}/role`, {
      method: 'PUT',
      body: JSON.stringify({ role }),
    }),
  changeUserPassword: (userId: number, password: string) =>
    fetchAPI<{ success: boolean }>(`/api/auth/users/${userId}/password`, {
      method: 'PUT',
      body: JSON.stringify({ password }),
    }),
  deleteUser: (userId: number) =>
    fetchAPI<{ success: boolean }>(`/api/auth/users/${userId}`, { method: 'DELETE' }),
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
  changeMyPassword: (currentPassword: string, newPassword: string) =>
    fetchAPI<{ success: boolean }>('/api/auth/me/password', {
      method: 'POST',
      body: JSON.stringify({ current_password: currentPassword, new_password: newPassword }),
    }),
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
  getFanStatus: () => fetchAPI<import('./types').FanControlStatus>('/api/fans/status'),
  releaseFanControl: () => fetchAPI<{ success: boolean; message: string }>('/api/fans/release', { method: 'POST' }),
  resumeFanControl: () => fetchAPI<{ success: boolean; active_profile: any }>('/api/fans/resume', { method: 'POST' }),
  getCurves: () => fetchAPI<{ curves: any[] }>('/api/fans/curves'),
  updateCurve: (curve: any, allowDangerous = false) =>
    fetchAPI<{ success: boolean; id?: string; curve?: any }>('/api/fans/curves', {
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
  exportConfig: () => fetchAPI<any>('/api/settings/export'),
  importConfig: (data: any) =>
    fetchAPI<{ success: boolean; imported: Record<string, number> }>('/api/settings/import', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

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

  // Drives
  drives: {
    list: () =>
      fetchAPI<{ drives: import('./types').DriveSummary[]; smartctl_available: boolean; total: number }>('/api/drives'),
    rescan: () =>
      fetchAPI<{ drives_found: number }>('/api/drives/rescan', { method: 'POST' }),
    getSettings: () =>
      fetchAPI<import('./types').DriveSettings>('/api/drives/settings'),
    updateSettings: (s: Partial<import('./types').DriveSettings>) =>
      fetchAPI<import('./types').DriveSettings>('/api/drives/settings', { method: 'PUT', body: JSON.stringify(s) }),
    get: (id: string) =>
      fetchAPI<import('./types').DriveDetail>(`/api/drives/${encodeURIComponent(id)}`),
    getAttributes: (id: string) =>
      fetchAPI<{ drive_id: string; attributes: import('./types').DriveRawAttribute[] }>(`/api/drives/${encodeURIComponent(id)}/attributes`),
    getHistory: (id: string, hours = 168) =>
      fetchAPI<{ drive_id: string; history: any[]; retention_limited: boolean }>(`/api/drives/${encodeURIComponent(id)}/history?hours=${hours}`),
    refresh: (id: string) =>
      fetchAPI<import('./types').DriveSummary>(`/api/drives/${encodeURIComponent(id)}/refresh`, { method: 'POST' }),
    startSelfTest: (id: string, type: 'short' | 'extended' | 'conveyance') =>
      fetchAPI<import('./types').DriveSelfTestRun>(`/api/drives/${encodeURIComponent(id)}/self-tests`, { method: 'POST', body: JSON.stringify({ type }) }),
    listSelfTests: (id: string) =>
      fetchAPI<{ drive_id: string; runs: import('./types').DriveSelfTestRun[] }>(`/api/drives/${encodeURIComponent(id)}/self-tests`),
    abortSelfTest: (id: string, runId: string) =>
      fetchAPI<{ success: boolean }>(`/api/drives/${encodeURIComponent(id)}/self-tests/${encodeURIComponent(runId)}/abort`, { method: 'POST' }),
    getDriveSettings: (id: string) =>
      fetchAPI<import('./types').DriveSettingsOverride>(`/api/drives/${encodeURIComponent(id)}/settings`),
    updateDriveSettings: (id: string, s: Partial<import('./types').DriveSettingsOverride>) =>
      fetchAPI<import('./types').DriveSettingsOverride>(`/api/drives/${encodeURIComponent(id)}/settings`, { method: 'PUT', body: JSON.stringify(s) }),
  },

  // Temperature targets
  temperatureTargets: {
    list: () =>
      fetchAPI<{ targets: import('./types').TemperatureTarget[] }>('/api/temperature-targets'),
    create: (target: {
      name: string; drive_id?: string | null; sensor_id: string;
      fan_ids: string[]; target_temp_c: number;
      tolerance_c?: number; min_fan_speed?: number;
    }) =>
      fetchAPI<import('./types').TemperatureTarget>('/api/temperature-targets', {
        method: 'POST', body: JSON.stringify(target),
      }),
    get: (id: string) =>
      fetchAPI<import('./types').TemperatureTarget>(`/api/temperature-targets/${encodeURIComponent(id)}`),
    update: (id: string, target: {
      name: string; drive_id?: string | null; sensor_id: string;
      fan_ids: string[]; target_temp_c: number;
      tolerance_c: number; min_fan_speed: number;
    }) =>
      fetchAPI<import('./types').TemperatureTarget>(`/api/temperature-targets/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify(target),
      }),
    delete: (id: string) =>
      fetchAPI<{ success: boolean }>(`/api/temperature-targets/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    toggle: (id: string, enabled: boolean) =>
      fetchAPI<import('./types').TemperatureTarget>(`/api/temperature-targets/${encodeURIComponent(id)}/enabled`, {
        method: 'PATCH', body: JSON.stringify({ enabled }),
      }),
  },

  // Analytics
  analytics: {
    getHistory: (
      hours = 24.0,
      sensorId?: string,
      bucketSeconds?: number,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({ hours: String(hours) });
      if (bucketSeconds) p.set('bucket_seconds', String(bucketSeconds));
      if (sensorId) p.set('sensor_id', sensorId);
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return fetchAPI<{
        buckets: import('./types').AnalyticsBucket[];
        series: Record<string, { timestamp: string; avg: number; min: number; max: number; count: number }[]>;
        bucket_seconds: number;
        requested_range: { start: string; end: string };
        returned_range:  { start: string; end: string };
        retention_limited: boolean;
      }>(`/api/analytics/history?${p}`);
    },
    getStats: (
      hours = 24.0,
      sensorId?: string,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({ hours: String(hours) });
      if (sensorId) p.set('sensor_id', sensorId);
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return fetchAPI<{
        stats: import('./types').AnalyticsStat[];
        requested_range: { start: string; end: string };
        returned_range:  { start: string; end: string };
      }>(`/api/analytics/stats?${p}`);
    },
    getAnomalies: (
      hours = 24.0,
      zScoreThreshold = 3.0,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({ hours: String(hours), z_score_threshold: String(zScoreThreshold) });
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return fetchAPI<{
        anomalies: import('./types').AnalyticsAnomaly[];
        z_score_threshold: number;
        requested_range: { start: string; end: string };
        returned_range: { start: string; end: string };
      }>(`/api/analytics/anomalies?${p}`);
    },
    getCorrelation: (
      sensorX: string,
      sensorY: string,
      hours = 24.0,
      opts?: { start?: string; end?: string },
    ) => {
      const p = new URLSearchParams({
        x_sensor_id: sensorX,
        y_sensor_id: sensorY,
        hours: String(hours),
      });
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      return fetchAPI<{
        x_sensor_id: string;
        y_sensor_id: string;
        correlation_coefficient: number;
        sample_count: number;
        samples: import('./types').AnalyticsCorrelationSample[];
      }>(`/api/analytics/correlation?${p}`);
    },
    getReport: (
      hours = 24.0,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({ hours: String(hours) });
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return fetchAPI<{
        generated_at: string;
        window_hours: number;
        requested_range: { start: string; end: string };
        returned_range:  { start: string; end: string };
        stats: import('./types').AnalyticsStat[];
        anomalies: import('./types').AnalyticsAnomaly[];
        top_anomalous_sensors: { sensor_id: string; sensor_name: string; count: number }[];
        regressions: import('./types').ThermalRegression[];
      }>(`/api/analytics/report?${p}`);
    },
    exportUrl: (
      format: 'csv' | 'json',
      hours: number,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({ format, hours: String(hours) });
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return `${API_BASE}/api/analytics/export?${p}`;
    },
    getRegression: (
      baselineDays = 30,
      recentHours = 24,
      thresholdDelta = 5.0,
      opts?: { start?: string; end?: string; sensorIds?: string[] },
    ) => {
      const p = new URLSearchParams({
        baseline_days: String(baselineDays),
        recent_hours: String(recentHours),
        threshold_delta: String(thresholdDelta),
      });
      if (opts?.start) p.set('start', opts.start);
      if (opts?.end)   p.set('end',   opts.end);
      if (opts?.sensorIds?.length) p.set('sensor_ids', opts.sensorIds.join(','));
      return fetchAPI<{
        regressions: import('./types').ThermalRegression[];
        baseline_period_days: number;
        recent_period_hours: number;
        threshold_delta: number;
      }>(`/api/analytics/regression?${p}`);
    },
  },

  // Quiet Hours
  quietHours: {
    list: () =>
      fetchAPI<{ rules: import('./types').QuietHoursRule[] }>('/api/quiet-hours'),
    create: (rule: { day_of_week: number; start_time: string; end_time: string; profile_id: string; enabled?: boolean }) =>
      fetchAPI<{ success: boolean; id: number }>('/api/quiet-hours', {
        method: 'POST', body: JSON.stringify(rule),
      }),
    update: (id: number, rule: { day_of_week: number; start_time: string; end_time: string; profile_id: string; enabled?: boolean }) =>
      fetchAPI<{ success: boolean }>(`/api/quiet-hours/${id}`, {
        method: 'PUT', body: JSON.stringify(rule),
      }),
    delete: (id: number) =>
      fetchAPI<{ success: boolean }>(`/api/quiet-hours/${id}`, { method: 'DELETE' }),
  },

  // Profile Schedules
  profileSchedules: {
    list: () =>
      fetchAPI<{ schedules: import('./types').ProfileSchedule[] }>('/api/profile-schedules'),
    create: (body: { profile_id: string; start_time: string; end_time: string; days_of_week: string; timezone?: string; enabled?: boolean }) =>
      fetchAPI<import('./types').ProfileSchedule>('/api/profile-schedules', {
        method: 'POST', body: JSON.stringify(body),
      }),
    update: (id: string, body: { profile_id: string; start_time: string; end_time: string; days_of_week: string; timezone?: string; enabled?: boolean }) =>
      fetchAPI<{ success: boolean }>(`/api/profile-schedules/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify(body),
      }),
    delete: (id: string) =>
      fetchAPI<void>(`/api/profile-schedules/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },

  update: {
    check: () =>
      fetchAPI<import('./types').UpdateCheck>('/api/update/check'),
    apply: () =>
      fetchAPI<import('./types').UpdateApplyResult>('/api/update/apply', { method: 'POST' }),
  },

  // Notification Channels
  notificationChannels: {
    list: () =>
      fetchAPI<{ channels: import('./types').NotificationChannel[] }>('/api/notification-channels'),
    get: (id: string) =>
      fetchAPI<import('./types').NotificationChannel>(`/api/notification-channels/${encodeURIComponent(id)}`),
    create: (body: { type: string; name: string; enabled?: boolean; config?: Record<string, unknown> }) =>
      fetchAPI<{ success: boolean; channel: import('./types').NotificationChannel }>('/api/notification-channels', {
        method: 'POST', body: JSON.stringify(body),
      }),
    update: (id: string, body: { name?: string; enabled?: boolean; config?: Record<string, unknown> }) =>
      fetchAPI<{ success: boolean; channel: import('./types').NotificationChannel }>(`/api/notification-channels/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify(body),
      }),
    delete: (id: string) =>
      fetchAPI<{ success: boolean }>(`/api/notification-channels/${encodeURIComponent(id)}`, { method: 'DELETE' }),
    test: (id: string) =>
      fetchAPI<{ success: boolean; error?: string }>(`/api/notification-channels/${encodeURIComponent(id)}/test`, { method: 'POST' }),
  },

  // Virtual Sensors
  virtualSensors: {
    list: () =>
      fetchAPI<{ virtual_sensors: import('./types').VirtualSensor[] }>('/api/virtual-sensors'),
    create: (body: import('./types').VirtualSensorRequest) =>
      fetchAPI<{ success: boolean; id: string }>('/api/virtual-sensors', {
        method: 'POST', body: JSON.stringify(body),
      }),
    update: (id: string, body: import('./types').VirtualSensorRequest) =>
      fetchAPI<{ success: boolean }>(`/api/virtual-sensors/${id}`, {
        method: 'PUT', body: JSON.stringify(body),
      }),
    delete: (id: string) =>
      fetchAPI<{ success: boolean }>(`/api/virtual-sensors/${id}`, { method: 'DELETE' }),
  },

  // Noise Profiles
  noiseProfiles: {
    list: () =>
      fetchAPI<{ profiles: import('./types').NoiseProfile[] }>('/api/noise-profiles'),
    get: (id: string) =>
      fetchAPI<import('./types').NoiseProfile>(`/api/noise-profiles/${encodeURIComponent(id)}`),
    create: (body: { fan_id: string; mode: string; data: import('./types').NoiseDataPoint[] }) =>
      fetchAPI<import('./types').NoiseProfile>('/api/noise-profiles', {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    delete: (id: string) =>
      fetchAPI<void>(`/api/noise-profiles/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },

  // Annotations
  annotations: {
    list: (start?: string, end?: string) => {
      const p = new URLSearchParams();
      if (start) p.set('start', start);
      if (end) p.set('end', end);
      const qs = p.toString();
      return fetchAPI<import('./types').Annotation[]>(`/api/annotations${qs ? `?${qs}` : ''}`);
    },
    create: (data: { timestamp_utc: string; label: string; description?: string }) =>
      fetchAPI<import('./types').Annotation>('/api/annotations', {
        method: 'POST',
        body: JSON.stringify(data),
      }),
    delete: (id: string) =>
      fetchAPI<void>(`/api/annotations/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },

  // Report Schedules
  reportSchedules: {
    list: () =>
      fetchAPI<{ schedules: import('./types').ReportSchedule[] }>('/api/report-schedules'),
    create: (body: Partial<import('./types').ReportSchedule>) =>
      fetchAPI<import('./types').ReportSchedule>('/api/report-schedules', {
        method: 'POST', body: JSON.stringify(body),
      }),
    update: (id: string, body: Partial<import('./types').ReportSchedule>) =>
      fetchAPI<import('./types').ReportSchedule>(`/api/report-schedules/${encodeURIComponent(id)}`, {
        method: 'PUT', body: JSON.stringify(body),
      }),
    delete: (id: string) =>
      fetchAPI<void>(`/api/report-schedules/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },

  // Manual fan speed (used by noise profiler sweep)
  setFanSpeed: (fanId: string, percent: number) =>
    fetchAPI<{ success: boolean; fan_id: string; speed: number }>('/api/fans/speed', {
      method: 'POST',
      body: JSON.stringify({ fan_id: fanId, speed: percent }),
    }),
};
