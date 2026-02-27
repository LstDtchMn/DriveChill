'use client';

import { useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useAuthStore } from '@/stores/authStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useWebSocket } from '@/hooks/useWebSocket';
import { useNotifications } from '@/hooks/useNotifications';
import { MobileNav, Sidebar } from '@/components/layout/Sidebar';
import { Header } from '@/components/layout/Header';
import { SystemOverview } from '@/components/dashboard/SystemOverview';
import { FanCurvesPage } from '@/components/fan-curves/FanCurvesPage';
import { AlertsPage } from '@/components/alerts/AlertsPage';
import { SettingsPage } from '@/components/settings/SettingsPage';
import { LoginPage } from '@/components/auth/LoginPage';
import { ChangelogBanner } from '@/components/layout/ChangelogBanner';
import { api, authApi } from '@/lib/api';
import type { TempUnit } from '@/lib/tempUnit';

const SAFE_MODE_MESSAGES: Record<string, string> = {
  sensor_failure: 'Sensor read failures exceeded limit — all fans forced to 100% until readings recover.',
  temp_panic: 'Temperature panic threshold exceeded — all fans forced to 100% until temps drop.',
  released: 'Fan control released. Fans are in BIOS/auto mode. Click "Resume Profile" to restore software control.',
};

function SafeModeBanner() {
  const safeMode = useAppStore((s) => s.safeMode);

  if (!safeMode.active && !safeMode.released) return null;

  const msg = safeMode.reason ? SAFE_MODE_MESSAGES[safeMode.reason] : null;
  if (!msg) return null;

  const isReleased = safeMode.released && !safeMode.active;
  const bg = isReleased ? 'var(--accent-muted)' : '#7f1d1d';
  const border = isReleased ? 'var(--accent)' : '#ef4444';
  const color = isReleased ? 'var(--accent)' : '#fca5a5';

  return (
    <div
      className="px-6 py-2 text-sm font-medium flex items-center gap-2"
      style={{ background: bg, borderBottom: `1px solid ${border}`, color }}
    >
      <span className="font-bold">{isReleased ? 'ℹ' : '⚠'}</span>
      {msg}
    </div>
  );
}

function PageContent() {
  const currentPage = useAppStore((s) => s.currentPage);

  switch (currentPage) {
    case 'dashboard':
      return <SystemOverview />;
    case 'curves':
      return <FanCurvesPage />;
    case 'alerts':
      return <AlertsPage />;
    case 'settings':
      return <SettingsPage />;
    default:
      return <SystemOverview />;
  }
}

export default function Home() {
  const { setBackendName, setSafeMode } = useAppStore();
  const { authRequired, authenticated, checking, setAuth, logout } = useAuthStore();
  const { setTempUnit, setSensorLabels } = useSettingsStore();

  // Connect WebSocket only after auth check resolves and user is allowed in
  const wsEnabled = !checking && (!authRequired || authenticated);
  useWebSocket(wsEnabled);
  useNotifications();

  // Check auth session on mount
  useEffect(() => {
    const checkAuth = async () => {
      try {
        const session = await authApi.checkSession();
        setAuth(session.auth_required, session.authenticated, session.username);
      } catch {
        // Backend unreachable — assume no auth required so the dashboard
        // still renders and shows "Disconnected" rather than a login wall.
        setAuth(false, true);
      }
    };
    checkAuth();
  }, [setAuth]);

  // Listen for 401 events (session expiry)
  useEffect(() => {
    const handler = () => logout();
    window.addEventListener('drivechill:auth-expired', handler);
    return () => window.removeEventListener('drivechill:auth-expired', handler);
  }, [logout]);

  // Fetch initial data (only when authenticated)
  useEffect(() => {
    if (checking || (authRequired && !authenticated)) return;

    const init = async () => {
      try {
        const health = await api.health();
        setBackendName(health.backend);
      } catch {
        setBackendName('Disconnected');
      }

      // Fetch initial fan status so the panic button reflects current state
      // without waiting for the first WebSocket message.
      try {
        const status = await api.getFanStatus();
        if (status.safe_mode) {
          setSafeMode(status.safe_mode);
        }
      } catch {
        // Non-critical — WS will sync this shortly
      }

      // Load user settings (temp unit) and sensor labels
      try {
        const s = await api.getSettings();
        if (s.temp_unit === 'F' || s.temp_unit === 'C') {
          setTempUnit(s.temp_unit as TempUnit);
        }
      } catch { /* non-critical */ }

      try {
        const { labels } = await api.getSensorLabels();
        setSensorLabels(labels);
      } catch { /* non-critical */ }
    };
    init();
  }, [setBackendName, setSafeMode, setTempUnit, setSensorLabels, checking, authRequired, authenticated]);

  // Show loading spinner while checking session
  if (checking) {
    return (
      <div
        className="min-h-screen flex items-center justify-center"
        style={{ background: 'var(--bg)' }}
      >
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
          Loading...
        </p>
      </div>
    );
  }

  // Show login page when auth is required and user is not authenticated
  if (authRequired && !authenticated) {
    return <LoginPage />;
  }

  return (
    <div className="min-h-screen md:h-screen md:flex">
      <div className="hidden md:block">
        <Sidebar />
      </div>
      <div className="flex-1 flex flex-col min-w-0">
        <Header />
        <ChangelogBanner />
        <SafeModeBanner />
        <main className="flex-1 overflow-y-auto px-3 py-4 pb-24 md:p-6 md:pb-6" style={{ background: 'var(--bg)' }}>
          <PageContent />
        </main>
      </div>
      <div className="md:hidden">
        <MobileNav />
      </div>
    </div>
  );
}
