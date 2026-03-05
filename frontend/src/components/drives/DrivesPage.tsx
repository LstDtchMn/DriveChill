'use client';

import { useEffect, useState } from 'react';
import { api } from '@/lib/api';
import type { DriveSummary, DriveDetail, DriveSelfTestRun, DriveSettings, DriveHealthStatus } from '@/lib/types';
import { RefreshCw, HardDrive, Thermometer, ChevronLeft, Activity, AlertTriangle, Wind, Plus } from 'lucide-react';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { formatTemp } from '@/lib/tempUnit';

// ── Helpers ───────────────────────────────────────────────────────────────────

function formatBytes(bytes: number): string {
  if (bytes <= 0) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit >= 3 ? 1 : 0)} ${units[unit]}`;
}

function healthColor(status: DriveHealthStatus): string {
  switch (status) {
    case 'healthy':  return 'var(--success)';
    case 'warning':  return 'var(--warning)';
    case 'critical': return 'var(--danger)';
    default:         return 'var(--text-secondary)';
  }
}

function healthBadgeClass(status: DriveHealthStatus): string {
  switch (status) {
    case 'healthy':  return 'badge badge-success';
    case 'warning':  return 'badge badge-warning';
    case 'critical': return 'badge badge-danger';
    default:         return 'badge';
  }
}

function mediaIcon(mediaType: string): string {
  switch (mediaType) {
    case 'nvme': return 'NVMe';
    case 'ssd':  return 'SSD';
    case 'hdd':  return 'HDD';
    default:     return mediaType.toUpperCase();
  }
}

// ── Drive list card ───────────────────────────────────────────────────────────

function DriveCard({ drive, onClick }: { drive: DriveSummary; onClick: () => void }) {
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const tempColor = drive.temperature_c == null ? 'var(--text-secondary)'
    : drive.temperature_c >= 60 ? 'var(--danger)'
    : drive.temperature_c >= 50 ? 'var(--warning)'
    : 'var(--success)';

  return (
    <button
      onClick={onClick}
      className="card w-full text-left transition-all duration-200 hover:opacity-90"
      style={{ cursor: 'pointer' }}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="flex items-center gap-3 min-w-0">
          <div
            className="w-10 h-10 rounded-lg flex items-center justify-center shrink-0"
            style={{ background: 'var(--accent-muted)' }}
          >
            <HardDrive size={20} style={{ color: 'var(--accent)' }} />
          </div>
          <div className="min-w-0">
            <p className="text-sm font-semibold truncate" style={{ color: 'var(--text)' }}>
              {drive.name}
            </p>
            <p className="text-xs truncate" style={{ color: 'var(--text-secondary)' }}>
              {drive.model || drive.device_path_masked} · {mediaIcon(drive.media_type)} · {formatBytes(drive.capacity_bytes)}
            </p>
          </div>
        </div>

        <div className="flex items-center gap-3 shrink-0">
          {drive.temperature_c != null && (
            <div className="flex items-center gap-1">
              <Thermometer size={14} style={{ color: tempColor }} />
              <span className="text-sm font-medium" style={{ color: tempColor }}>
                {formatTemp(drive.temperature_c, tempUnit)}
              </span>
            </div>
          )}
          <span className={healthBadgeClass(drive.health_status)} style={{ textTransform: 'capitalize' }}>
            {drive.health_status}
          </span>
        </div>
      </div>

      {drive.health_percent != null && (
        <div className="mt-3">
          <div className="flex justify-between text-xs mb-1" style={{ color: 'var(--text-secondary)' }}>
            <span>Health</span>
            <span>{drive.health_percent.toFixed(0)}%</span>
          </div>
          <div className="h-1.5 rounded-full" style={{ background: 'var(--surface-200)' }}>
            <div
              className="h-1.5 rounded-full transition-all duration-500"
              style={{
                width: `${drive.health_percent}%`,
                background: healthColor(drive.health_status),
              }}
            />
          </div>
        </div>
      )}

      {!drive.smart_available && (
        <p className="mt-2 text-xs" style={{ color: 'var(--text-secondary)' }}>
          SMART unavailable — install smartmontools for full monitoring
        </p>
      )}
    </button>
  );
}

// ── Drive temperature history mini-chart ──────────────────────────────────────

function TempHistoryCard({ driveId }: { driveId: string }) {
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const [points, setPoints] = useState<Array<{ recorded_at: string; temperature_c: number | null }>>([]);
  const [retentionLimited, setRetentionLimited] = useState(false);

  useEffect(() => {
    api.drives.getHistory(driveId, 24)
      .then((r) => {
        setPoints(r.history);
        setRetentionLimited(r.retention_limited);
      })
      .catch(() => {});
  }, [driveId]);

  const tempPoints = points.filter((p) => p.temperature_c != null) as Array<{ recorded_at: string; temperature_c: number }>;
  if (tempPoints.length < 2) return null;

  const temps = tempPoints.map((p) => p.temperature_c);
  const minTemp = Math.min(...temps);
  const maxTemp = Math.max(...temps);
  const range = maxTemp - minTemp || 1;

  const W = 300;
  const H = 56;
  const PAD = 6;

  const svgPoints = tempPoints
    .map((p, i) => {
      const x = PAD + (i / (tempPoints.length - 1)) * (W - PAD * 2);
      const y = H - PAD - ((p.temperature_c - minTemp) / range) * (H - PAD * 2);
      return `${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');

  return (
    <div className="card">
      <div className="flex items-center justify-between mb-2">
        <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>Temperature — last 24h</h3>
        {retentionLimited && (
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>retention limited</span>
        )}
      </div>
      <svg width="100%" viewBox={`0 0 ${W} ${H}`} style={{ height: H, display: 'block' }}>
        <polyline
          points={svgPoints}
          fill="none"
          stroke="var(--accent)"
          strokeWidth={1.5}
          strokeLinejoin="round"
          strokeLinecap="round"
        />
      </svg>
      <div className="flex justify-between text-xs mt-1" style={{ color: 'var(--text-secondary)' }}>
        <span>{formatTemp(minTemp, tempUnit)}</span>
        <span>{formatTemp(maxTemp, tempUnit)}</span>
      </div>
    </div>
  );
}

// ── Drive detail panel ────────────────────────────────────────────────────────

function DetailRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex justify-between items-center py-2" style={{ borderBottom: '1px solid var(--border)' }}>
      <span className="text-sm" style={{ color: 'var(--text-secondary)' }}>{label}</span>
      <span className="text-sm font-medium" style={{ color: 'var(--text)' }}>{value ?? '—'}</span>
    </div>
  );
}

function SelfTestRow({ run }: { run: DriveSelfTestRun }) {
  const statusColor = run.status === 'passed' ? 'var(--success)'
    : run.status === 'failed' ? 'var(--danger)'
    : run.status === 'running' ? 'var(--accent)'
    : 'var(--text-secondary)';

  return (
    <div className="flex items-center justify-between py-2" style={{ borderBottom: '1px solid var(--border)' }}>
      <div>
        <span className="text-sm font-medium capitalize" style={{ color: 'var(--text)' }}>{run.type}</span>
        <span className="text-xs ml-2" style={{ color: 'var(--text-secondary)' }}>
          {new Date(run.started_at).toLocaleDateString()}
        </span>
      </div>
      <div className="flex items-center gap-2">
        {run.status === 'running' && run.progress_percent != null && (
          <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>{run.progress_percent.toFixed(0)}%</span>
        )}
        <span className="text-xs font-medium capitalize" style={{ color: statusColor }}>{run.status}</span>
      </div>
    </div>
  );
}

function DriveDetailPanel({
  drive,
  onBack,
  onRefresh,
}: {
  drive: DriveDetail;
  onBack: () => void;
  onRefresh: () => void;
}) {
  const tempUnit = useSettingsStore((s) => s.tempUnit);
  const setPage = useAppStore((s) => s.setPage);
  const setPreselectedCurveSensorId = useAppStore((s) => s.setPreselectedCurveSensorId);
  const setCreateCoolingCurveSensorId = useAppStore((s) => s.setCreateCoolingCurveSensorId);
  const [selfTests, setSelfTests] = useState<DriveSelfTestRun[]>([]);
  const [testRunning, setTestRunning] = useState(false);

  function handleUseCooling() {
    setPreselectedCurveSensorId(`hdd_temp_${drive.id}`);
    setPage('curves');
  }

  function handleCreateCoolingCurve() {
    setCreateCoolingCurveSensorId(`hdd_temp_${drive.id}`);
    setPage('curves');
  }

  useEffect(() => {
    api.drives.listSelfTests(drive.id).then((r) => setSelfTests(r.runs)).catch(() => {});
  }, [drive.id]);

  async function startShortTest() {
    if (!drive.supports_self_test) return;
    setTestRunning(true);
    try {
      await api.drives.startSelfTest(drive.id, 'short');
      const r = await api.drives.listSelfTests(drive.id);
      setSelfTests(r.runs);
    } catch {
      // ignore
    } finally {
      setTestRunning(false);
    }
  }

  async function abortTest() {
    const running = selfTests.find((t) => t.status === 'running');
    if (!running) return;
    try {
      await api.drives.abortSelfTest(drive.id, running.id);
      const r = await api.drives.listSelfTests(drive.id);
      setSelfTests(r.runs);
    } catch {
      // ignore
    }
  }

  const runningTest = selfTests.find((t) => t.status === 'running');

  return (
    <div className="animate-fade-in">
      <div className="flex items-center gap-3 mb-6">
        <button
          onClick={onBack}
          className="btn-secondary flex items-center gap-2 text-sm"
        >
          <ChevronLeft size={16} />
          Back
        </button>
        <h2 className="section-title" style={{ margin: 0 }}>{drive.name}</h2>
        <div className="flex items-center gap-2 ml-auto">
          {drive.temperature_c != null && (
            <>
              <button
                onClick={handleUseCooling}
                className="btn-secondary flex items-center gap-2 text-sm"
                title="Add this drive's temperature sensor to the active fan curve"
              >
                <Wind size={14} />
                Use for cooling
              </button>
              <button
                onClick={handleCreateCoolingCurve}
                className="btn-secondary flex items-center gap-2 text-sm"
                title="Create a new fan curve pre-configured for storage cooling"
              >
                <Plus size={14} />
                New cooling curve
              </button>
            </>
          )}
          <button
            onClick={onRefresh}
            className="btn-secondary flex items-center gap-2 text-sm"
          >
            <RefreshCw size={14} />
            Refresh
          </button>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Overview */}
        <div className="card">
          <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>Overview</h3>
          <DetailRow label="Model" value={drive.model || '—'} />
          <DetailRow label="Serial" value={drive.serial_masked} />
          <DetailRow label="Device" value={drive.device_path_masked} />
          <DetailRow label="Bus" value={drive.bus_type.toUpperCase()} />
          <DetailRow label="Media" value={mediaIcon(drive.media_type)} />
          <DetailRow label="Capacity" value={formatBytes(drive.capacity_bytes)} />
          <DetailRow label="Firmware" value={drive.firmware_version || '—'} />
          {drive.rotation_rate_rpm != null && (
            <DetailRow label="Rotation" value={`${drive.rotation_rate_rpm} RPM`} />
          )}
        </div>

        {/* Health */}
        <div className="card">
          <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>Health</h3>
          <DetailRow
            label="Status"
            value={
              <span className={healthBadgeClass(drive.health_status)} style={{ textTransform: 'capitalize' }}>
                {drive.health_status}
              </span>
            }
          />
          {drive.health_percent != null && (
            <DetailRow label="Score" value={`${drive.health_percent.toFixed(0)}%`} />
          )}
          <DetailRow label="Temperature" value={drive.temperature_c != null ? formatTemp(drive.temperature_c, tempUnit) : '—'} />
          <DetailRow label="Temp Warning" value={drive.temperature_warning_c != null ? formatTemp(drive.temperature_warning_c, tempUnit) : '—'} />
          <DetailRow label="Temp Critical" value={drive.temperature_critical_c != null ? formatTemp(drive.temperature_critical_c, tempUnit) : '—'} />
          {drive.wear_percent_used != null && (
            <DetailRow label="Wear Used" value={`${drive.wear_percent_used}%`} />
          )}
          {drive.available_spare_percent != null && (
            <DetailRow label="Spare" value={`${drive.available_spare_percent}%`} />
          )}
          <DetailRow label="Predicted Failure" value={drive.predicted_failure ? <span style={{ color: 'var(--danger)' }}>Yes</span> : 'No'} />
          {drive.reallocated_sectors != null && (
            <DetailRow label="Reallocated Sectors" value={String(drive.reallocated_sectors)} />
          )}
          {drive.pending_sectors != null && (
            <DetailRow label="Pending Sectors" value={String(drive.pending_sectors)} />
          )}
          {drive.media_errors != null && drive.media_errors > 0 && (
            <DetailRow label="Media Errors" value={<span style={{ color: 'var(--danger)' }}>{String(drive.media_errors)}</span>} />
          )}
        </div>

        {/* Temperature history */}
        {drive.temperature_c != null && (
          <TempHistoryCard driveId={drive.id} />
        )}

        {/* Self-tests */}
        <div className="card">
          <div className="flex items-center justify-between mb-3">
            <h3 className="text-sm font-semibold" style={{ color: 'var(--text)' }}>Self-Tests</h3>
            <div className="flex gap-2">
              {runningTest && drive.supports_abort && (
                <button onClick={abortTest} className="btn-secondary text-xs px-2 py-1">
                  Abort
                </button>
              )}
              {!runningTest && drive.supports_self_test && (
                <button
                  onClick={startShortTest}
                  disabled={testRunning}
                  className="btn-primary text-xs px-2 py-1"
                >
                  {testRunning ? 'Starting…' : 'Short Test'}
                </button>
              )}
              {!drive.supports_self_test && (
                <span className="text-xs" style={{ color: 'var(--text-secondary)' }}>Not supported</span>
              )}
            </div>
          </div>
          {selfTests.length === 0 ? (
            <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>No tests run yet.</p>
          ) : (
            selfTests.map((run) => <SelfTestRow key={run.id} run={run} />)
          )}
        </div>

        {/* SMART attributes */}
        {drive.raw_attributes.length > 0 && (
          <div className="card">
            <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>SMART Attributes</h3>
            <div style={{ maxHeight: 240, overflowY: 'auto' }}>
              {drive.raw_attributes.map((attr) => (
                <div
                  key={attr.key}
                  className="flex items-center justify-between py-1.5 text-xs"
                  style={{ borderBottom: '1px solid var(--border)' }}
                >
                  <span style={{ color: attr.status !== 'ok' ? 'var(--warning)' : 'var(--text-secondary)' }}>
                    {attr.name || attr.key}
                  </span>
                  <span style={{ color: 'var(--text)' }}>{attr.raw_value}</span>
                </div>
              ))}
            </div>
          </div>
        )}

        {/* Usage stats */}
        <div className="card">
          <h3 className="text-sm font-semibold mb-3" style={{ color: 'var(--text)' }}>Usage</h3>
          {drive.power_on_hours != null && (
            <DetailRow label="Power-on Hours" value={`${drive.power_on_hours.toLocaleString()} h`} />
          )}
          {drive.power_cycle_count != null && (
            <DetailRow label="Power Cycles" value={drive.power_cycle_count.toLocaleString()} />
          )}
          {drive.unsafe_shutdowns != null && (
            <DetailRow label="Unsafe Shutdowns" value={drive.unsafe_shutdowns.toLocaleString()} />
          )}
        </div>
      </div>
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function DrivesPage() {
  const [drives, setDrives] = useState<DriveSummary[]>([]);
  const [smartctlAvailable, setSmartctlAvailable] = useState(true);
  const [selectedDrive, setSelectedDrive] = useState<DriveDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [rescanning, setRescanning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [sortBy, setSortBy] = useState<'name' | 'temp' | 'health'>('name');
  const [filterHealth, setFilterHealth] = useState<DriveHealthStatus | 'all'>('all');
  const [filterMedia, setFilterMedia] = useState<string>('all');

  async function loadDrives() {
    try {
      const r = await api.drives.list();
      setDrives(r.drives);
      setSmartctlAvailable(r.smartctl_available);
    } catch {
      setError('Failed to load drives. Check your connection.');
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { loadDrives(); }, []);

  async function handleRescan() {
    setRescanning(true);
    try {
      await api.drives.rescan();
      await loadDrives();
    } finally {
      setRescanning(false);
    }
  }

  async function handleSelectDrive(id: string) {
    try {
      const detail = await api.drives.get(id);
      setSelectedDrive(detail);
    } catch {
      setError('Failed to load drive details. Check your connection.');
    }
  }

  async function handleRefreshDetail() {
    if (!selectedDrive) return;
    try {
      await api.drives.refresh(selectedDrive.id);
      const detail = await api.drives.get(selectedDrive.id);
      setSelectedDrive(detail);
    } catch {
      // ignore
    }
  }

  const sortedFiltered = drives
    .filter((d) => filterHealth === 'all' || d.health_status === filterHealth)
    .filter((d) => filterMedia === 'all' || d.media_type === filterMedia)
    .sort((a, b) => {
      if (sortBy === 'temp') {
        return (b.temperature_c ?? -Infinity) - (a.temperature_c ?? -Infinity);
      }
      if (sortBy === 'health') {
        const order: Record<DriveHealthStatus, number> = { critical: 0, warning: 1, unknown: 2, healthy: 3 };
        return (order[a.health_status] ?? 2) - (order[b.health_status] ?? 2);
      }
      return a.name.localeCompare(b.name);
    });

  const mediaTypes = Array.from(new Set(drives.map((d) => d.media_type)));

  if (selectedDrive) {
    return (
      <div className="p-6">
        <DriveDetailPanel
          drive={selectedDrive}
          onBack={() => setSelectedDrive(null)}
          onRefresh={handleRefreshDetail}
        />
      </div>
    );
  }

  return (
    <div className="p-6 animate-fade-in">
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="section-title">Storage Drives</h1>
          <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>
            {drives.length} drive{drives.length !== 1 ? 's' : ''} detected
          </p>
        </div>
        <button
          onClick={handleRescan}
          disabled={rescanning}
          className="btn-secondary flex items-center gap-2"
        >
          <RefreshCw size={14} className={rescanning ? 'animate-spin' : ''} />
          {rescanning ? 'Scanning…' : 'Rescan'}
        </button>
      </div>

      {/* Error banner */}
      {error && (
        <div
          className="flex items-center gap-3 px-4 py-3 rounded-lg mb-6 text-sm"
          style={{ background: 'var(--danger)', border: '1px solid var(--danger)', color: '#fff' }}
        >
          <AlertTriangle size={16} />
          <span>{error}</span>
        </div>
      )}

      {/* Degraded mode banner */}
      {!smartctlAvailable && (
        <div
          className="flex items-center gap-3 px-4 py-3 rounded-lg mb-6 text-sm"
          style={{ background: 'var(--accent-muted)', border: '1px solid var(--accent)', color: 'var(--accent)' }}
        >
          <AlertTriangle size={16} />
          <span>
            <strong>smartmontools not found.</strong> Install smartmontools for SMART health data, temperature readings, and self-test support.
          </span>
        </div>
      )}

      {/* Filters + sort */}
      {drives.length > 0 && (
        <div className="flex flex-wrap gap-3 mb-5">
          <div className="flex items-center gap-2 text-sm">
            <span style={{ color: 'var(--text-secondary)' }}>Sort:</span>
            {(['name', 'temp', 'health'] as const).map((s) => (
              <button
                key={s}
                onClick={() => setSortBy(s)}
                className="px-2 py-1 rounded text-xs font-medium capitalize"
                style={sortBy === s
                  ? { background: 'var(--accent)', color: 'white' }
                  : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
              >
                {s}
              </button>
            ))}
          </div>
          <div className="flex items-center gap-2 text-sm">
            <span style={{ color: 'var(--text-secondary)' }}>Health:</span>
            {(['all', 'healthy', 'warning', 'critical'] as const).map((h) => (
              <button
                key={h}
                onClick={() => setFilterHealth(h)}
                className="px-2 py-1 rounded text-xs font-medium capitalize"
                style={filterHealth === h
                  ? { background: 'var(--accent)', color: 'white' }
                  : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
              >
                {h}
              </button>
            ))}
          </div>
          {mediaTypes.length > 1 && (
            <div className="flex items-center gap-2 text-sm">
              <span style={{ color: 'var(--text-secondary)' }}>Type:</span>
              {(['all', ...mediaTypes]).map((m) => (
                <button
                  key={m}
                  onClick={() => setFilterMedia(m)}
                  className="px-2 py-1 rounded text-xs font-medium uppercase"
                  style={filterMedia === m
                    ? { background: 'var(--accent)', color: 'white' }
                    : { background: 'var(--surface-200)', color: 'var(--text-secondary)' }}
                >
                  {m === 'all' ? 'All' : mediaIcon(m)}
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Drive list */}
      {loading ? (
        <div className="flex items-center justify-center py-16" style={{ color: 'var(--text-secondary)' }}>
          <Activity size={20} className="animate-spin mr-2" />
          Loading drives…
        </div>
      ) : sortedFiltered.length === 0 ? (
        <div className="card text-center py-12">
          <HardDrive size={40} className="mx-auto mb-3" style={{ color: 'var(--text-secondary)' }} />
          <p className="font-medium" style={{ color: 'var(--text)' }}>
            {drives.length === 0 ? 'No drives detected' : 'No drives match the current filter'}
          </p>
          <p className="text-sm mt-1" style={{ color: 'var(--text-secondary)' }}>
            {drives.length === 0 ? 'Click Rescan to search for drives.' : 'Try changing the filters above.'}
          </p>
        </div>
      ) : (
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
          {sortedFiltered.map((drive) => (
            <DriveCard key={drive.id} drive={drive} onClick={() => handleSelectDrive(drive.id)} />
          ))}
        </div>
      )}
    </div>
  );
}
