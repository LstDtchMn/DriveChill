'use client';

import { useAppStore } from '@/stores/appStore';
import { LayoutDashboard, Activity, Bell, Settings, Snowflake, BarChart2, HardDrive, Thermometer } from 'lucide-react';
import type { Page } from '@/lib/types';

const NAV_ITEMS: { page: Page; label: string; icon: typeof LayoutDashboard }[] = [
  { page: 'dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { page: 'curves', label: 'Fan Curves', icon: Activity },
  { page: 'temperature-targets', label: 'Temp Targets', icon: Thermometer },
  { page: 'alerts', label: 'Alerts', icon: Bell },
  { page: 'drives', label: 'Drives', icon: HardDrive },
  { page: 'analytics', label: 'Analytics', icon: BarChart2 },
  { page: 'settings', label: 'Settings', icon: Settings },
];

export function Sidebar() {
  const { currentPage, setPage, connected, activeAlerts } = useAppStore();

  return (
    <aside className="w-64 h-screen flex flex-col border-r shrink-0" style={{ borderColor: 'var(--border)', background: 'var(--bg-secondary)' }}>
      {/* Logo */}
      <div className="flex items-center gap-3 px-5 py-5 border-b" style={{ borderColor: 'var(--border)' }}>
        <div className="w-9 h-9 rounded-xl flex items-center justify-center" style={{ background: 'var(--accent-muted)' }}>
          <Snowflake size={20} style={{ color: 'var(--accent)' }} />
        </div>
        <div>
          <h1 className="text-base font-bold" style={{ color: 'var(--text)' }}>DriveChill</h1>
          <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>Fan Controller</p>
        </div>
      </div>

      {/* Navigation */}
      <nav className="flex-1 px-3 py-4 space-y-1">
        {NAV_ITEMS.map(({ page, label, icon: Icon }) => {
          const isActive = currentPage === page;
          const hasAlert = page === 'alerts' && activeAlerts.length > 0;

          return (
            <button
              key={page}
              onClick={() => setPage(page)}
              className={`
                w-full min-h-11 flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm font-medium
                transition-all duration-200 relative
                ${isActive
                  ? 'text-white'
                  : 'hover:bg-surface-200'
                }
              `}
              style={isActive ? { background: 'var(--accent)', color: 'white' } : { color: 'var(--text-secondary)' }}
            >
              <Icon size={18} />
              <span>{label}</span>
              {hasAlert && (
                <span className="absolute right-3 w-2 h-2 rounded-full bg-red-500 animate-pulse" />
              )}
            </button>
          );
        })}
      </nav>

      {/* Connection status */}
      <div className="px-5 py-4 border-t" style={{ borderColor: 'var(--border)' }}>
        <div className="flex items-center gap-2">
          <div
            className={`w-2 h-2 rounded-full ${connected ? 'bg-green-500' : 'bg-red-500'}`}
            style={connected ? {} : { animation: 'pulse 1.5s infinite' }}
          />
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>
            {connected ? 'Connected' : 'Connecting...'}
          </span>
        </div>
      </div>
    </aside>
  );
}

export function MobileNav() {
  const { currentPage, setPage, activeAlerts } = useAppStore();

  return (
    <nav
      className="fixed bottom-0 left-0 right-0 border-t px-2 py-2 z-40"
      style={{ borderColor: 'var(--border)', background: 'var(--bg-secondary)' }}
    >
      <div className="grid grid-cols-7 gap-1">
        {NAV_ITEMS.map(({ page, label, icon: Icon }) => {
          const isActive = currentPage === page;
          const hasAlert = page === 'alerts' && activeAlerts.length > 0;
          return (
            <button
              key={page}
              onClick={() => setPage(page)}
              className="min-h-11 rounded-lg flex flex-col items-center justify-center gap-1 text-[11px] relative"
              style={isActive
                ? { background: 'var(--accent-muted)', color: 'var(--accent)' }
                : { color: 'var(--text-secondary)' }}
            >
              <Icon size={16} />
              <span className="leading-none">{label}</span>
              {hasAlert && (
                <span className="absolute top-2 right-3 w-2 h-2 rounded-full bg-red-500" />
              )}
            </button>
          );
        })}
      </div>
    </nav>
  );
}
