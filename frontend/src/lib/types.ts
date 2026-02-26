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

export type Page = 'dashboard' | 'curves' | 'alerts' | 'settings';
