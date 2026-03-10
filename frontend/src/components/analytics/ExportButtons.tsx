'use client';

import { api } from '@/lib/api';

interface ExportButtonsProps {
  hours: number;
  customStart: string;
  customEnd: string;
  selectedSensorIds: string[];
  data: {
    stats: unknown[] | null;
    anomalies: unknown[] | null;
    history: unknown[] | null;
    regressions: unknown[] | null;
  };
}

export function ExportButtons({ hours, customStart, customEnd, selectedSensorIds, data }: ExportButtonsProps) {
  const buildOpts = () => {
    const opts: { start?: string; end?: string; sensorIds?: string[] } = {};
    if (customStart && customEnd) {
      opts.start = new Date(customStart).toISOString();
      opts.end   = new Date(customEnd).toISOString();
    }
    if (selectedSensorIds.length > 0) opts.sensorIds = selectedSensorIds;
    return opts;
  };

  const handleCsvExport = () => {
    const opts = buildOpts();
    const url = api.analytics.exportUrl('csv', hours, opts);
    const a = document.createElement('a');
    a.href = url;
    a.download = '';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
  };

  const handleJsonExport = () => {
    const payload = {
      exported_at: new Date().toISOString(),
      stats: data.stats,
      anomalies: data.anomalies,
      history: data.history,
      regressions: data.regressions,
    };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    const tag = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
    a.download = `drivechill-analytics-${tag}.json`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  };

  const handlePdfExport = () => {
    window.print();
  };

  return (
    <div className="export-buttons-row" style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
      <button onClick={handleCsvExport} className="btn-secondary text-xs" style={{ minHeight: 28 }}>
        Export CSV
      </button>
      <button onClick={handleJsonExport} className="btn-secondary text-xs" style={{ minHeight: 28 }}>
        Export JSON
      </button>
      <button onClick={handlePdfExport} className="btn-secondary text-xs" style={{ minHeight: 28 }} title="Print or save as PDF">
        Export PDF
      </button>
    </div>
  );
}
