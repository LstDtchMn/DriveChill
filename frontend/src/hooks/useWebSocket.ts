'use client';

import { useEffect, useRef, useCallback } from 'react';
import { useAppStore } from '@/stores/appStore';
import type { WSMessage } from '@/lib/types';
import { api, getWsUrl } from '@/lib/api';

const WS_URL = getWsUrl();

/**
 * Exponential backoff delays (ms) for WebSocket reconnection attempts.
 * After exhausting all entries the last value (15s) is reused indefinitely.
 */
const RECONNECT_DELAYS = [1000, 2000, 4000, 8000, 15000];

let _globalWsRef: WebSocket | null = null;

/** Imperatively close the singleton WebSocket (e.g. on logout). */
export function closeWebSocket() {
  if (_globalWsRef && (_globalWsRef.readyState === WebSocket.OPEN || _globalWsRef.readyState === WebSocket.CONNECTING)) {
    _globalWsRef.close();
    _globalWsRef = null;
  }
}

/**
 * Maintains a singleton WebSocket connection to the backend sensor stream.
 * Dispatches incoming sensor updates, alerts, fan test progress, and safe-mode
 * status into the Zustand app store.  Reconnects automatically with exponential
 * backoff (see {@link RECONNECT_DELAYS}).  Pass `enabled = false` to tear down
 * the connection (e.g. when auth expires).
 */
export function useWebSocket(enabled = true) {
  const wsRef = useRef<WebSocket | null>(null);
  const reconnectAttempt = useRef(0);
  const reconnectTimer = useRef<NodeJS.Timeout>();
  // Track enabled state in a ref so callbacks can read the current value
  // without needing to be recreated when `enabled` changes.
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;

  const setReadings       = useAppStore((s) => s.setReadings);
  const addHistoryPoint   = useAppStore((s) => s.addHistoryPoint);
  const setAppliedSpeeds  = useAppStore((s) => s.setAppliedSpeeds);
  const setControlSources = useAppStore((s) => s.setControlSources);
  const addAlertEvents    = useAppStore((s) => s.addAlertEvents);
  const setActiveAlerts   = useAppStore((s) => s.setActiveAlerts);
  const setFanTestProgress = useAppStore((s) => s.setFanTestProgress);
  const setSafeMode       = useAppStore((s) => s.setSafeMode);
  const setConnected      = useAppStore((s) => s.setConnected);
  const setBackendName    = useAppStore((s) => s.setBackendName);

  const scheduleReconnect = useCallback(() => {
    // Guard: don't reconnect if WS has been disabled (auth expired, etc.)
    if (!enabledRef.current) return;

    const delay = RECONNECT_DELAYS[Math.min(reconnectAttempt.current, RECONNECT_DELAYS.length - 1)];
    reconnectAttempt.current++;

    if (reconnectTimer.current) {
      clearTimeout(reconnectTimer.current);
    }

    reconnectTimer.current = setTimeout(() => {
      if (enabledRef.current) {
        connect();
      }
    }, delay);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const connect = useCallback(() => {
    if (!enabledRef.current) return;
    if (wsRef.current?.readyState === WebSocket.OPEN) return;

    try {
      const ws = new WebSocket(WS_URL);
      wsRef.current = ws;
      _globalWsRef = ws;

      ws.onopen = async () => {
        setConnected(true);
        reconnectAttempt.current = 0;
        try {
          const health = await api.health();
          setBackendName(health?.backend || 'Connected');
        } catch {
          setBackendName('Connected');
        }
      };

      ws.onmessage = (event) => {
        try {
          const msg: WSMessage = JSON.parse(event.data);

          if (msg.type === 'sensor_update' && msg.readings) {
            setReadings(msg.readings);
            addHistoryPoint({
              timestamp: msg.timestamp || new Date().toISOString(),
              readings: msg.readings,
            });

            if (msg.applied_speeds) {
              setAppliedSpeeds(msg.applied_speeds);
            }

            if (msg.control_sources) {
              setControlSources(msg.control_sources);
            }

            if (msg.alerts && msg.alerts.length > 0) {
              addAlertEvents(msg.alerts);
            }

            if (msg.active_alerts) {
              setActiveAlerts(msg.active_alerts);
            }

            if (msg.safe_mode) {
              setSafeMode(msg.safe_mode);
            }

            // Fan benchmark progress (only present when tests are running)
            setFanTestProgress(msg.fan_test ?? []);
          }
        } catch {
          // Ignore parse errors
        }
      };

      ws.onclose = () => {
        _globalWsRef = null;
        setConnected(false);
        setBackendName('Disconnected');
        // Only reconnect if still enabled — prevents reconnect loop
        // after intentional close (logout, auth expired, component unmount).
        if (enabledRef.current) {
          scheduleReconnect();
        }
      };

      ws.onerror = () => {
        if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) {
          ws.close();
        }
      };
    } catch {
      scheduleReconnect();
    }
  }, [setReadings, addHistoryPoint, setAppliedSpeeds, setControlSources, addAlertEvents, setActiveAlerts, setFanTestProgress, setSafeMode, setConnected, setBackendName, scheduleReconnect]);

  useEffect(() => {
    if (!enabled) {
      // Close existing connection when disabled (e.g. auth expired)
      enabledRef.current = false;
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      if (reconnectTimer.current) {
        clearTimeout(reconnectTimer.current);
      }
      return;
    }

    connect();

    return () => {
      // Prevent onclose from scheduling a reconnect after unmount.
      enabledRef.current = false;
      if (reconnectTimer.current) {
        clearTimeout(reconnectTimer.current);
      }
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
    };
  }, [connect, enabled]);
}
