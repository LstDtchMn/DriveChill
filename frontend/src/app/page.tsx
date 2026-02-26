'use client';

import { useEffect } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useWebSocket } from '@/hooks/useWebSocket';
import { Sidebar } from '@/components/layout/Sidebar';
import { Header } from '@/components/layout/Header';
import { SystemOverview } from '@/components/dashboard/SystemOverview';
import { FanCurvesPage } from '@/components/fan-curves/FanCurvesPage';
import { AlertsPage } from '@/components/alerts/AlertsPage';
import { SettingsPage } from '@/components/settings/SettingsPage';
import { api } from '@/lib/api';

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

  // Connect WebSocket
  useWebSocket();

  // Fetch initial data
  useEffect(() => {
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
    };
    init();
  }, [setBackendName, setSafeMode]);

  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <div className="flex-1 flex flex-col min-w-0">
        <Header />
        <SafeModeBanner />
        <main className="flex-1 overflow-y-auto p-6" style={{ background: 'var(--bg)' }}>
          <PageContent />
        </main>
      </div>
    </div>
  );
}
