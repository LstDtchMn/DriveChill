'use client';

import { useState, useEffect } from 'react';
import { X, Sparkles } from 'lucide-react';

const APP_VERSION = '1.5.0';
const STORAGE_KEY = 'drivechill_changelog_dismissed';

const CHANGELOG: Record<string, string[]> = {
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
