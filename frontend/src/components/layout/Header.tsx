'use client';

import { useAppStore } from '@/stores/appStore';
import { ThemeToggle } from './ThemeToggle';
import { Bell } from 'lucide-react';

const PAGE_TITLES: Record<string, string> = {
  dashboard: 'Dashboard',
  curves: 'Fan Curves',
  alerts: 'Alerts & Logging',
  settings: 'Settings',
};

export function Header() {
  const { currentPage, backendName, activeAlerts, setPage } = useAppStore();

  return (
    <header
      className="h-14 flex items-center justify-between px-6 border-b shrink-0"
      style={{ borderColor: 'var(--border)', background: 'var(--bg)' }}
    >
      <div>
        <h2 className="text-lg font-semibold" style={{ color: 'var(--text)' }}>
          {PAGE_TITLES[currentPage] || 'Dashboard'}
        </h2>
      </div>

      <div className="flex items-center gap-3">
        {backendName && (
          <span className="text-xs px-2.5 py-1 rounded-full" style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}>
            {backendName}
          </span>
        )}

        {/* Alert bell */}
        <button
          onClick={() => setPage('alerts')}
          className="relative p-2 rounded-lg transition-colors hover:bg-surface-200"
        >
          <Bell size={18} style={{ color: 'var(--text-secondary)' }} />
          {activeAlerts.length > 0 && (
            <span className="absolute -top-0.5 -right-0.5 w-4 h-4 rounded-full bg-red-500 text-white text-[10px] flex items-center justify-center font-bold">
              {activeAlerts.length}
            </span>
          )}
        </button>

        <ThemeToggle />
      </div>
    </header>
  );
}
