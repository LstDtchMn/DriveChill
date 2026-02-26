'use client';

import { useEffect, useRef, useCallback } from 'react';
import { useAppStore } from '@/stores/appStore';
import type { WSMessage } from '@/lib/types';

const WS_URL = process.env.NEXT_PUBLIC_WS_URL || 'ws://localhost:8085/api/ws';
const RECONNECT_DELAYS = [1000, 2000, 4000, 8000, 15000];

export function useWebSocket() {
  const wsRef = useRef<WebSocket | null>(null);
  const reconnectAttempt = useRef(0);
  const reconnectTimer = useRef<NodeJS.Timeout>();

  const {
    setReadings,
    addHistoryPoint,
    setAppliedSpeeds,
    addAlertEvents,
    setActiveAlerts,
    setFanTestProgress,
    setSafeMode,
    setConnected,
  } = useAppStore();

  const connect = useCallback(() => {
    if (wsRef.current?.readyState === WebSocket.OPEN) return;

    try {
      const ws = new WebSocket(WS_URL);
      wsRef.current = ws;

      ws.onopen = () => {
        setConnected(true);
        reconnectAttempt.current = 0;
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
        setConnected(false);
        scheduleReconnect();
      };

      ws.onerror = () => {
        ws.close();
      };
    } catch {
      scheduleReconnect();
    }
  }, [setReadings, addHistoryPoint, setAppliedSpeeds, addAlertEvents, setActiveAlerts, setFanTestProgress, setSafeMode, setConnected]);

  const scheduleReconnect = useCallback(() => {
    const delay = RECONNECT_DELAYS[Math.min(reconnectAttempt.current, RECONNECT_DELAYS.length - 1)];
    reconnectAttempt.current++;

    if (reconnectTimer.current) {
      clearTimeout(reconnectTimer.current);
    }

    reconnectTimer.current = setTimeout(() => {
      connect();
    }, delay);
  }, [connect]);

  useEffect(() => {
    connect();

    return () => {
      if (reconnectTimer.current) {
        clearTimeout(reconnectTimer.current);
      }
      if (wsRef.current) {
        wsRef.current.close();
      }
    };
  }, [connect]);
}
