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
  const { setBackendName } = useAppStore();

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
    };
    init();
  }, [setBackendName]);

  return (
    <div className="flex h-screen overflow-hidden">
      <Sidebar />
      <div className="flex-1 flex flex-col min-w-0">
        <Header />
        <main className="flex-1 overflow-y-auto p-6" style={{ background: 'var(--bg)' }}>
          <PageContent />
        </main>
      </div>
    </div>
  );
}
