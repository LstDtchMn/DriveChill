'use client';

import { useState, useEffect, useCallback } from 'react';
import { api } from '@/lib/api';
import type { MachineInfo, MachineRemoteState, RemoteProfile, RemoteFan, SensorReading } from '@/lib/types';
import { useCanWrite } from '@/hooks/useCanWrite';

interface Props {
  machine: MachineInfo;
  onClose: () => void;
}

const statusBadgeClass: Record<string, string> = {
  online: 'badge-success',
  degraded: 'badge-warning',
  offline: 'badge-danger',
  auth_error: 'badge-danger',
  version_mismatch: 'badge-warning',
  unknown: 'badge-warning',
};

const warmSensorTypes = new Set(['cpu_temp', 'gpu_temp']);

export function MachineDrillIn({ machine, onClose }: Props) {
  const canWrite = useCanWrite();
  const [state, setState] = useState<MachineRemoteState | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [releaseMsg, setReleaseMsg] = useState<string | null>(null);

  const fetchState = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await api.getMachineState(machine.id);
      setState(data.state);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load machine state');
    } finally {
      setLoading(false);
    }
  }, [machine.id]);

  useEffect(() => {
    fetchState();
  }, [fetchState]);

  const handleActivate = useCallback(async (profile: RemoteProfile) => {
    if (profile.is_active || busy) return;
    setBusy(true);
    try {
      await api.activateRemoteProfile(machine.id, profile.id);
      await fetchState();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to activate profile');
    } finally {
      setBusy(false);
    }
  }, [machine.id, busy, fetchState]);

  const handleRelease = useCallback(async () => {
    if (busy) return;
    setBusy(true);
    setReleaseMsg(null);
    try {
      await api.releaseRemoteFans(machine.id);
      setReleaseMsg('Fan control released.');
      await fetchState();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to release fan control');
    } finally {
      setBusy(false);
    }
  }, [machine.id, busy, fetchState]);

  const badgeClass = statusBadgeClass[machine.status] || 'badge-warning';

  return (
    <div className="animate-fade-in" style={{ maxWidth: '900px' }}>
      {/* Header */}
      <div className="flex items-center gap-3 mb-4 flex-wrap">
        <button
          className="btn-secondary"
          onClick={onClose}
          style={{ fontSize: '0.85rem', padding: '4px 12px' }}
        >
          &larr; Back
        </button>
        <span className="text-sm font-semibold" style={{ color: 'var(--text)', flexShrink: 0 }}>
          {machine.name}
        </span>
        <span className={`badge ${badgeClass}`}>{machine.status}</span>
        <span
          className="text-xs truncate"
          style={{ color: 'var(--text-secondary)', maxWidth: '240px' }}
          title={machine.base_url}
        >
          {machine.base_url}
        </span>
      </div>

      {/* Persistent last_error from machine record */}
      {machine.last_error && (
        <div
          className="card p-3 mb-4 text-xs"
          style={{ color: 'var(--danger)', borderColor: 'var(--danger)' }}
        >
          {machine.last_error}
        </div>
      )}

      {/* State fetch error */}
      {error && (
        <div className="card p-3 mb-4" style={{ borderColor: 'var(--danger)' }}>
          <p className="text-xs mb-2" style={{ color: 'var(--danger)' }}>{error}</p>
          <button className="btn-secondary" style={{ fontSize: '0.8rem' }} onClick={fetchState}>
            Retry
          </button>
        </div>
      )}

      {/* Spinner */}
      {loading && !error && (
        <div className="card p-6 text-center" style={{ color: 'var(--text-secondary)' }}>
          <div
            style={{
              width: 28,
              height: 28,
              border: '3px solid var(--border)',
              borderTopColor: 'var(--accent)',
              borderRadius: '50%',
              animation: 'spin 0.8s linear infinite',
              margin: '0 auto 8px',
            }}
          />
          <p className="text-xs">Loading machine state…</p>
        </div>
      )}

      {state && (
        <>
          {/* Sensors */}
          {state.sensors.length > 0 && (
            <div className="mb-5">
              <h3 className="section-title mb-3 px-1">Sensors</h3>
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))',
                  gap: '12px',
                }}
              >
                {state.sensors.map((sensor: SensorReading) => {
                  const warm = warmSensorTypes.has(sensor.sensor_type);
                  return (
                    <div key={sensor.id} className="card p-3 animate-card-enter">
                      <p
                        className="text-xs mb-1 truncate"
                        style={{ color: 'var(--text-secondary)' }}
                        title={sensor.name}
                      >
                        {sensor.name}
                      </p>
                      <p
                        className="text-sm font-semibold"
                        style={{ color: warm ? 'var(--warning)' : 'var(--text)' }}
                      >
                        {sensor.value != null ? sensor.value : '--'}
                        <span className="text-xs font-normal ml-1" style={{ color: 'var(--text-secondary)' }}>
                          {sensor.unit}
                        </span>
                      </p>
                    </div>
                  );
                })}
              </div>
            </div>
          )}

          {/* Profiles */}
          {state.profiles.length > 0 && (
            <div className="mb-5">
              <h3 className="section-title mb-3 px-1">Profiles</h3>
              <div className="card" style={{ overflow: 'hidden' }}>
                {state.profiles.map((profile: RemoteProfile, idx: number) => (
                  <div
                    key={profile.id}
                    className="flex items-center gap-3 px-4 py-3"
                    style={{
                      borderBottom: idx < state.profiles.length - 1 ? '1px solid var(--border)' : 'none',
                    }}
                  >
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <p className="text-sm font-medium truncate" style={{ color: 'var(--text)' }}>
                        {profile.name}
                      </p>
                      <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
                        {profile.preset}
                      </p>
                    </div>
                    {profile.is_active && (
                      <span className="badge badge-success" style={{ flexShrink: 0 }}>Active</span>
                    )}
                    <button
                      className={profile.is_active ? 'btn-secondary' : 'btn-primary'}
                      disabled={profile.is_active || busy || !canWrite}
                      onClick={() => handleActivate(profile)}
                      style={{ fontSize: '0.8rem', flexShrink: 0, opacity: (profile.is_active || !canWrite) ? 0.5 : 1 }}
                    >
                      {profile.is_active ? 'Active' : 'Activate'}
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Fans */}
          {state.fans.length > 0 && (
            <div className="mb-2">
              <h3 className="section-title mb-3 px-1">Fans</h3>
              <div className="card mb-3" style={{ overflow: 'hidden' }}>
                {state.fans.map((fan: RemoteFan, idx: number) => (
                  <div
                    key={fan.id}
                    className="flex items-center gap-4 px-4 py-3"
                    style={{
                      borderBottom: idx < state.fans.length - 1 ? '1px solid var(--border)' : 'none',
                    }}
                  >
                    <p className="text-sm" style={{ color: 'var(--text)', flex: 1, minWidth: 0 }}>
                      {fan.name}
                    </p>
                    <span className="text-xs" style={{ color: 'var(--text-secondary)', minWidth: '60px', textAlign: 'right' }}>
                      {fan.speed_percent != null ? `${fan.speed_percent}%` : '--'}
                    </span>
                    <span className="text-xs font-mono" style={{ color: 'var(--text-secondary)', minWidth: '70px', textAlign: 'right' }}>
                      {fan.rpm != null ? `${fan.rpm} RPM` : '--'}
                    </span>
                  </div>
                ))}
              </div>
              <div className="flex items-center gap-3">
                <button
                  className="btn-secondary"
                  disabled={busy || !canWrite}
                  onClick={handleRelease}
                  style={{ fontSize: '0.85rem', opacity: canWrite ? 1 : 0.5 }}
                >
                  Release Fan Control
                </button>
                {releaseMsg && (
                  <span className="text-xs" style={{ color: 'var(--success)' }}>{releaseMsg}</span>
                )}
              </div>
            </div>
          )}
        </>
      )}

      <style>{`
        @keyframes spin { to { transform: rotate(360deg); } }
      `}</style>
    </div>
  );
}
