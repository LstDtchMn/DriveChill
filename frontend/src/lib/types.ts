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

export type Page = 'dashboard' | 'curves' | 'alerts' | 'settings' | 'analytics' | 'drives' | 'temperature-targets';

// ── Temperature targets ──────────────────────────────────────────────────────

export interface TemperatureTarget {
  id: string;
  name: string;
  drive_id: string | null;
  sensor_id: string;
  fan_ids: string[];
  target_temp_c: number;
  tolerance_c: number;
  min_fan_speed: number;
  enabled: boolean;
}

// ── Drive monitoring types ────────────────────────────────────────────────────

export interface DriveCapabilitySet {
  smart_read: boolean;
  smart_self_test_short: boolean;
  smart_self_test_extended: boolean;
  smart_self_test_conveyance: boolean;
  smart_self_test_abort: boolean;
  temperature_source: string;
  health_source: string;
}

export interface DriveRawAttribute {
  key: string;
  name: string;
  normalized_value: number | null;
  worst_value: number | null;
  threshold: number | null;
  raw_value: string;
  status: string;
  source_kind: string;
}

export interface DriveSelfTestRun {
  id: string;
  drive_id: string;
  type: string;
  status: string;
  progress_percent: number | null;
  started_at: string;
  finished_at: string | null;
  failure_message: string | null;
}

export type DriveHealthStatus = 'healthy' | 'warning' | 'critical' | 'unknown';

export interface DriveSummary {
  id: string;
  name: string;
  model: string;
  serial_masked: string;
  device_path_masked: string;
  bus_type: string;
  media_type: string;
  capacity_bytes: number;
  temperature_c: number | null;
  health_status: DriveHealthStatus;
  health_percent: number | null;
  smart_available: boolean;
  native_available: boolean;
  supports_self_test: boolean;
  supports_abort: boolean;
  last_updated_at: string | null;
}

export interface DriveDetail extends DriveSummary {
  serial_full: string;
  device_path: string;
  firmware_version: string;
  interface_speed: string | null;
  rotation_rate_rpm: number | null;
  power_on_hours: number | null;
  power_cycle_count: number | null;
  unsafe_shutdowns: number | null;
  wear_percent_used: number | null;
  available_spare_percent: number | null;
  reallocated_sectors: number | null;
  pending_sectors: number | null;
  uncorrectable_errors: number | null;
  media_errors: number | null;
  predicted_failure: boolean;
  temperature_warning_c: number;
  temperature_critical_c: number;
  capabilities: DriveCapabilitySet;
  last_self_test: DriveSelfTestRun | null;
  raw_attributes: DriveRawAttribute[];
}

export interface DriveSettings {
  enabled: boolean;
  smartctl_provider_enabled: boolean;
  native_provider_enabled: boolean;
  smartctl_path: string;
  fast_poll_seconds: number;
  health_poll_seconds: number;
  rescan_poll_seconds: number;
  hdd_temp_warning_c: number;
  hdd_temp_critical_c: number;
  ssd_temp_warning_c: number;
  ssd_temp_critical_c: number;
  nvme_temp_warning_c: number;
  nvme_temp_critical_c: number;
  wear_warning_percent_used: number;
  wear_critical_percent_used: number;
}

export interface DriveSettingsOverride {
  drive_id: string;
  temp_warning_c: number | null;
  temp_critical_c: number | null;
  alerts_enabled: boolean | null;
  curve_picker_enabled: boolean | null;
}

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
  p95_value: number | null;
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
  severity?: 'warning' | 'critical';
}

export interface AnalyticsCorrelationSample {
  epoch: number;
  x: number;
  y: number;
}

export interface ThermalRegression {
  sensor_id: string;
  sensor_name: string;
  baseline_avg: number;
  recent_avg: number;
  delta: number;
  severity: 'warning' | 'critical';
  message: string;
}

export interface UpdateCheck {
  current: string;
  latest: string;
  update_available: boolean;
  release_url: string;
  deployment: 'windows_service' | 'windows_standalone' | 'docker' | 'other';
}

export interface UpdateApplyResult {
  status: 'update_started' | 'manual_required';
  version?: string;
  message?: string;
  command?: string;
}
