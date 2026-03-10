export type WidgetType =
  | 'cooling_score'
  | 'temp_gauges'
  | 'fan_speeds'
  | 'drive_health'
  | 'system_load'
  | 'temp_chart';

export interface WidgetConfig {
  type: WidgetType;
  visible: boolean;
  order: number;
}

export const DEFAULT_WIDGETS: WidgetConfig[] = [
  { type: 'cooling_score', order: 0, visible: true },
  { type: 'temp_gauges',   order: 1, visible: true },
  { type: 'fan_speeds',    order: 2, visible: true },
  { type: 'system_load',   order: 3, visible: true },
  { type: 'drive_health',  order: 4, visible: true },
  { type: 'temp_chart',    order: 5, visible: true },
];

const STORAGE_KEY = 'drivechill_widget_layout';

const WIDGET_LABELS: Record<WidgetType, string> = {
  cooling_score: 'Machines / Cooling Score',
  temp_gauges:   'Temperature Gauges',
  fan_speeds:    'Fan Speeds',
  drive_health:  'Drive Health (Storage)',
  system_load:   'System Load',
  temp_chart:    'Temperature Chart',
};

export function getWidgetLabel(type: WidgetType): string {
  return WIDGET_LABELS[type];
}

/**
 * Load widget config from localStorage, merging with defaults so newly
 * added widget types always appear (at the end, hidden by default).
 */
export function loadWidgetConfig(): WidgetConfig[] {
  try {
    const raw = typeof window !== 'undefined' ? localStorage.getItem(STORAGE_KEY) : null;
    if (!raw) return DEFAULT_WIDGETS;

    const saved: WidgetConfig[] = JSON.parse(raw);

    // Merge: keep saved config, append any new widget types not yet stored
    const savedTypes = new Set(saved.map((w) => w.type));
    const maxOrder = saved.reduce((m, w) => Math.max(m, w.order), -1);
    let nextOrder = maxOrder + 1;

    const merged = [...saved];
    for (const def of DEFAULT_WIDGETS) {
      if (!savedTypes.has(def.type)) {
        merged.push({ type: def.type, visible: false, order: nextOrder++ });
      }
    }

    return merged;
  } catch {
    return DEFAULT_WIDGETS;
  }
}

export function saveWidgetConfig(widgets: WidgetConfig[]): void {
  try {
    if (typeof window !== 'undefined') {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(widgets));
    }
  } catch {
    // storage may be unavailable (private mode, quota exceeded)
  }
}
