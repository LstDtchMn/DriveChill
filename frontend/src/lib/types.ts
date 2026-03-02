export type SensorType =
  | 'cpu_temp'
  | 'gpu_temp'
  | 'hdd_temp'
  | 'case_temp'
  | 'cpu_load'
  | 'gpu_load'
  | 'fan_rpm'
  | 'fan_percent';

export interface SensorReading {
  id: string;
  name: string;
  sensor_type: SensorType;
  value: number;
  min_value: number | null;
  max_value: number | null;
  unit: string;
}

export interface FanCurvePoint {
  temp: number;
  speed: number;
}

export interface FanCurve {
  id: string;
  name: string;
  sensor_id: string;
  fan_id: string;
  points: FanCurvePoint[];
  enabled: boolean;
  sensor_ids: string[];
}

export interface FanSettings {
  fan_id: string;
  min_speed_pct: number;
  zero_rpm_capable: boolean;
}

export interface Profile {
  id: string;
  name: string;
  preset: string;
  curves: FanCurve[];
  is_active: boolean;
}

export interface AlertRule {
  id: string;
  sensor_id: string;
  threshold: number;
  name: string;
  enabled: boolean;
}

export interface AlertEvent {
  rule_id: string;
  sensor_id: string;
  sensor_name: string;
  threshold: number;
  actual_value: number;
  timestamp: string;
  message: string;
}

export interface FanTestOptions {
  steps: number;
  settle_ms: number;
  min_rpm_threshold: number;
}

export interface FanTestStep {
  speed_pct: number;
  rpm: number | null;
  spinning: boolean;
}

export interface FanTestResult {
  fan_id: string;
  status: 'running' | 'completed' | 'cancelled' | 'failed';
  started_at: string;
  completed_at: string | null;
  steps: FanTestStep[];
  min_operational_pct: number | null;
  max_rpm: number | null;
  options: FanTestOptions;
  error: string | null;
}

export interface FanTestProgress {
  fan_id: string;
  status: 'running' | 'completed' | 'cancelled' | 'failed';
  steps_done: number;
  steps_total: number;
  current_pct: number;
  current_rpm: number | null;
  steps: FanTestStep[];
  min_operational_pct: number | null;
}

export interface SafeModeStatus {
  active: boolean;
  released: boolean;
  reason: 'sensor_failure' | 'temp_panic' | 'released' | null;
}

export interface MachineSnapshotSummary {
  cpu_temp: number | null;
  gpu_temp: number | null;
  case_temp: number | null;
  fan_count: number;
  backend: string | null;
}

export interface MachineSnapshot {
  timestamp: string;
  health?: {
    status?: string;
    app?: string;
    api_version?: string;
    capabilities?: string[];
    version?: string;
    backend?: string;
  };
  summary?: MachineSnapshotSummary;
}

export interface RemoteProfile {
  id: string;
  name: string;
  preset: string;
  is_active: boolean;
}

export interface RemoteFan {
  id: string;
  name: string;
  speed_percent: number | null;
  rpm: number | null;
}

export interface MachineRemoteState {
  profiles: RemoteProfile[];
  fans: RemoteFan[];
  sensors: SensorReading[];
}

export interface MachineInfo {
  id: string;
  name: string;
  base_url: string;
  has_api_key: boolean;
  api_key_id: string | null;
  enabled: boolean;
  poll_interval_seconds: number;
  timeout_ms: number;
  status: 'unknown' | 'online' | 'degraded' | 'offline' | 'auth_error' | 'version_mismatch' | string;
  last_seen_at: string | null;
  last_error: string | null;
  consecutive_failures: number;
  created_at: string;
  updated_at: string;
  freshness_seconds: number | null;
  snapshot: MachineSnapshot | null;
  capabilities: string[];
  last_command_at: string | null;
}

export interface ApiKeyInfo {
  id: string;
  name: string;
  key_prefix: string;
  scopes?: string[];
  created_at: string;
  revoked_at: string | null;
  last_used_at: string | null;
}

export interface WebhookConfig {
  enabled: boolean;
  target_url: string;
  has_signing_secret: boolean;
  timeout_seconds: number;
  max_retries: number;
  retry_backoff_seconds: number;
  updated_at: string;
}

export interface WebhookDelivery {
  timestamp: string;
  event_type: string;
  target_url: string;
  attempt: number;
  success: boolean;
  http_status: number | null;
  latency_ms: number | null;
  error: string | null;
}

export interface WSMessage {
  type: 'sensor_update' | 'heartbeat';
  timestamp?: string;
  readings?: SensorReading[];
  applied_speeds?: Record<string, number>;
  alerts?: AlertEvent[];
  active_alerts?: string[];
  fan_test?: FanTestProgress[];
  safe_mode?: SafeModeStatus;
}

export type Page = 'dashboard' | 'curves' | 'alerts' | 'settings' | 'analytics';

export interface PushSubscription {
  id: string;
  endpoint: string;
  user_agent: string | null;
  created_at: string;
  last_used_at: string | null;
}

export interface EmailNotificationSettings {
  enabled: boolean;
  smtp_host: string;
  smtp_port: number;
  smtp_username: string;
  has_password: boolean;
  sender_address: string;
  recipient_list: string[];
  use_tls: boolean;
  use_ssl: boolean;
  updated_at: string;
}

// ─── Analytics ────────────────────────────────────────────────────────────────

export interface AnalyticsBucket {
  sensor_id: string;
  sensor_name: string;
  sensor_type: string;
  unit: string;
  timestamp_utc: string;
  avg_value: number;
  min_value: number;
  max_value: number;
  sample_count: number;
}

export interface AnalyticsStat {
  sensor_id: string;
  sensor_name: string;
  sensor_type: string;
  unit: string;
  min_value: number;
  max_value: number;
  avg_value: number;
  p95_value: number;
  sample_count: number;
}

export interface AnalyticsAnomaly {
  timestamp_utc: string;
  sensor_id: string;
  sensor_name: string;
  value: number;
  unit: string;
  z_score: number;
  mean: number;
  stdev: number;
}
