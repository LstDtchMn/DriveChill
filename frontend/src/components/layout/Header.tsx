'use client';

import { useState, useRef, useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useAuthStore } from '@/stores/authStore';
import { ThemeToggle } from './ThemeToggle';
import { Bell, ShieldOff, Play, LogOut, ArrowUpCircle } from 'lucide-react';
import { api, authApi } from '@/lib/api';

const PAGE_TITLES: Record<string, string> = {
  dashboard: 'Dashboard',
  curves: 'Fan Curves',
  'temperature-targets': 'Temperature Targets',
  alerts: 'Alerts & Logging',
  drives: 'Drives',
  analytics: 'Analytics',
  'quiet-hours': 'Quiet Hours',
  settings: 'Settings',
};

export function Header() {
  const { currentPage, backendName, activeAlerts, setPage, safeMode, setSafeMode, updateCheck } = useAppStore();
  const { authRequired, authenticated, username, logout: authLogout } = useAuthStore();
  const [releasing, setReleasing] = useState(false);
  const [resuming, setResuming] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  const toastTimerRef = useRef<ReturnType<typeof setTimeout>>();
  useEffect(() => () => { if (toastTimerRef.current) clearTimeout(toastTimerRef.current); }, []);

  const handleLogout = async () => {
    try {
      await authApi.logout();
    } catch {
      // best-effort
    }
    authLogout();
  };

  const showToast = (msg: string) => {
    setToast(msg);
    if (toastTimerRef.current) clearTimeout(toastTimerRef.current);
    toastTimerRef.current = setTimeout(() => setToast(null), 3000);
  };

  const handleRelease = async () => {
    if (releasing) return;
    setReleasing(true);
    try {
      await api.releaseFanControl();
      setSafeMode({ active: false, released: true, reason: 'released' });
      showToast('Fan control released - BIOS/auto mode active');
    } catch {
      showToast('Failed to release fan control');
    } finally {
      setReleasing(false);
    }
  };

  const handleResume = async () => {
    if (resuming) return;
    setResuming(true);
    try {
      await api.resumeFanControl();
      setSafeMode({ active: false, released: false, reason: null });
      showToast('Fan control resumed');
    } catch {
      showToast('No active profile - activate one first');
    } finally {
      setResuming(false);
    }
  };

  return (
    <header
      className="min-h-14 flex items-center justify-between gap-2 px-3 md:px-6 py-2 border-b shrink-0 relative"
      style={{ borderColor: 'var(--border)', background: 'var(--bg)' }}
    >
      <div>
        <h2 className="text-base md:text-lg font-semibold" style={{ color: 'var(--text)' }}>
          {PAGE_TITLES[currentPage] || 'Dashboard'}
        </h2>
      </div>

      <div className="flex items-center justify-end flex-wrap gap-2">
        {backendName && (
          <span
            className="hidden sm:inline text-xs px-2.5 py-1 rounded-full"
            style={{ background: 'var(--accent-muted)', color: 'var(--accent)' }}
          >
            {backendName}
          </span>
        )}

        {safeMode.released ? (
          <button
            onClick={handleResume}
            disabled={resuming}
            className="min-h-11 flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-colors"
            style={{ background: 'var(--success)', color: '#fff', opacity: resuming ? 0.6 : 1 }}
            title="Resume software fan control"
          >
            <Play size={13} />
            <span className="hidden sm:inline">{resuming ? 'Resuming...' : 'Resume Profile'}</span>
            <span className="sm:hidden">{resuming ? '...' : 'Resume'}</span>
          </button>
        ) : (
          <button
            onClick={handleRelease}
            disabled={releasing}
            className="min-h-11 flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold transition-colors"
            style={{ background: 'var(--danger)', color: '#fff', opacity: releasing ? 0.6 : 1 }}
            title="Release all fans to BIOS/auto mode immediately"
          >
            <ShieldOff size={13} />
            <span className="hidden sm:inline">{releasing ? 'Releasing...' : 'Release Fans'}</span>
            <span className="sm:hidden">{releasing ? '...' : 'Release'}</span>
          </button>
        )}

        {updateCheck?.update_available && (
          <button
            onClick={() => setPage('settings')}
            className="relative min-h-11 min-w-11 p-2 rounded-lg transition-colors hover:bg-surface-200"
            title={`Update available: v${updateCheck.latest}`}
          >
            <ArrowUpCircle size={18} style={{ color: 'var(--warning)' }} />
          </button>
        )}

        <button
          onClick={() => setPage('alerts')}
          className="relative min-h-11 min-w-11 p-2 rounded-lg transition-colors hover:bg-surface-200"
          title="Alerts"
        >
          <Bell size={18} style={{ color: 'var(--text-secondary)' }} />
          {activeAlerts.length > 0 && (
            <span className="absolute -top-0.5 -right-0.5 w-4 h-4 rounded-full bg-red-500 text-white text-[10px] flex items-center justify-center font-bold">
              {activeAlerts.length}
            </span>
          )}
        </button>

        <ThemeToggle />

        {authRequired && authenticated && (
          <button
            onClick={handleLogout}
            className="min-h-11 min-w-11 flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg text-xs transition-colors hover:bg-surface-200"
            style={{ color: 'var(--text-secondary)' }}
            title={username ? `Logged in as ${username}` : 'Log out'}
          >
            <LogOut size={14} />
          </button>
        )}
      </div>

      {toast && (
        <div
          className="absolute bottom-[-2.5rem] right-3 md:right-6 px-4 py-2 rounded-lg text-xs font-medium shadow-lg z-50"
          style={{ background: 'var(--surface-200)', color: 'var(--text)', border: '1px solid var(--border)' }}
        >
          {toast}
        </div>
      )}
    </header>
  );
}
