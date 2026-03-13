'use client';

import { useState, useEffect } from 'react';
import { X, Sparkles } from 'lucide-react';

const APP_VERSION = '3.6.0';
const STORAGE_KEY = 'drivechill_changelog_dismissed';

const CHANGELOG: Record<string, string[]> = {
  '3.6.0': [
    'MQTT notification channels: structured config form (broker URL, QoS, retain, telemetry)',
    'Machine health check: "Check Now" button with latency display',
    'Analytics: period comparison cards (24h vs previous 24h) and interactive trend charts',
    'Hardware mutex: serialised fan-speed writes prevent concurrent I/O races',
    'Audit test coverage: 29 new tests for internal auth, profile seeding, session TTL',
  ],
  '2.1.0': [
    'Temperature Targets: set a target temp for any drive and auto-control linked fans',
    'Relationship Map: visual SVG diagram of drive-to-fan connections with thermal-state colours',
    'Multi-drive shared fan support: hottest drive wins when multiple targets share a fan',
    'Proportional fan control with configurable tolerance band and floor speed',
  ],
  '2.0.0': [
    'Analytics v2.0: custom date-range queries, multi-sensor filtering, and auto-sized buckets',
    'New Correlation panel: Pearson r between any two sensors with scatter plot',
    'Sensor filter chips and custom start/end date pickers on the Analytics page',
    'Drive detail panel: 24-hour temperature mini-sparkline',
    '"New cooling curve" button on Drives page creates a pre-configured storage cooling draft',
    'History retention default raised to 30 days (was 24 hours) for all installations',
    'Analytics anomalies now include severity badge (warning / critical)',
  ],
  '1.6.0': [
    'Drive monitoring: SMART health, temperature, and self-test support via smartmontools',
    'New Drives page with health badges, temperature display, and SMART attribute drill-in',
    'Drive temperatures injected into the fan-curve and alert pipeline as hdd_temp sensors',
    'Analytics: temperature values now respect your °C/°F preference',
    'Fan curve editor: larger touch targets for reliable mobile dragging',
    'Temp unit preference synced from backend on every startup',
    'Playwright E2E test suite covering dashboard, curves, alerts, and settings',
  ],
  '1.5.0': [
    'Safety hardening: panic mode now overrides released fan control',
    'Authentication: session auth + CSRF protection across all write APIs',
    'WebSocket improvements: reconnect guards and periodic session revalidation',
    'Backup/restore: FK validation and full settings replacement on import',
    'Fan curves: composite sensors (MAX of multiple temperature sources)',
    'Per-fan settings: min speed floor and zero-RPM support',
    'Fan benchmark workflow with persisted fan tuning',
    'Auto-start support for Windows Task Scheduler and Linux systemd user service',
  ],
};

export function ChangelogBanner() {
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    try {
      const dismissed = localStorage.getItem(STORAGE_KEY);
      if (dismissed !== APP_VERSION) {
        setVisible(true);
      }
    } catch {
      // localStorage unavailable (incognito, restricted iframe, etc.)
    }
  }, []);

  const dismiss = () => {
    try { localStorage.setItem(STORAGE_KEY, APP_VERSION); } catch { /* best-effort */ }
    setVisible(false);
  };

  if (!visible) return null;

  const entries = CHANGELOG[APP_VERSION] ?? [];
  if (entries.length === 0) return null;

  return (
    <div
      className="px-6 py-3 text-sm flex items-start gap-3"
      style={{
        background: 'var(--accent-muted)',
        borderBottom: '1px solid var(--accent)',
        color: 'var(--accent)',
      }}
    >
      <Sparkles size={16} className="mt-0.5 shrink-0" />
      <div className="flex-1 min-w-0">
        <span className="font-semibold">What&apos;s new in v{APP_VERSION}</span>
        <span className="ml-2 text-xs opacity-75">
          {entries.slice(0, 3).join(' | ')}
          {entries.length > 3 && ` | +${entries.length - 3} more`}
        </span>
      </div>
      <button
        onClick={dismiss}
        className="min-h-11 min-w-11 p-1 rounded hover:opacity-70 transition-opacity shrink-0"
        title="Dismiss"
      >
        <X size={14} />
      </button>
    </div>
  );
}
