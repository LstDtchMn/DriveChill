'use client';

import { useEffect, useRef, useCallback } from 'react';
import { useAppStore } from '@/stores/appStore';
import { useSettingsStore } from '@/stores/settingsStore';

const ALERT_COOLDOWN_MS = 60_000; // Don't re-fire same alert within 60s

/**
 * Fires browser (desktop) notifications for critical events:
 * - Temperature alert rules triggering
 * - Safe-mode activation (sensor failure / temp panic)
 *
 * Requires the user to enable notifications in Settings and grant
 * browser permission via the Notification API.
 */
export function useNotifications() {
  const alertEvents = useAppStore((s) => s.alertEvents);
  const safeMode = useAppStore((s) => s.safeMode);
  const notificationsEnabled = useSettingsStore((s) => s.notificationsEnabled);

  // Track which alerts we've already notified to avoid spam
  const notifiedAlerts = useRef<Map<string, number>>(new Map());
  // Use a sentinel to suppress notification on first render / page refresh
  const lastSafeModeReason = useRef<string | null | undefined>(undefined);
  // Track last-seen event timestamp instead of array length (array is capped at 100)
  const lastSeenTimestamp = useRef<string | null>(null);

  const canNotify = useCallback(() => {
    if (!notificationsEnabled) return false;
    if (typeof window === 'undefined') return false;
    if (!('Notification' in window)) return false;
    return Notification.permission === 'granted';
  }, [notificationsEnabled]);

  const fireNotification = useCallback((title: string, body: string, tag?: string) => {
    if (!canNotify()) return;
    try {
      const n = new Notification(title, {
        body,
        icon: '/favicon.ico',
        tag: tag || undefined,
        silent: false,
      });
      n.onclick = () => {
        window.focus();
        n.close();
      };
    } catch {
      // Notification constructor can throw in some contexts
    }
  }, [canNotify]);

  // Fire notifications for new alert events.
  // Tracks by timestamp instead of array index because the store caps at 100 items.
  useEffect(() => {
    if (!canNotify()) return;
    if (alertEvents.length === 0) return;

    // On first render, record the latest timestamp without notifying
    if (lastSeenTimestamp.current === null) {
      lastSeenTimestamp.current = alertEvents[alertEvents.length - 1].timestamp;
      return;
    }

    // Find events newer than the last one we saw
    const newEvents = alertEvents.filter((e) => e.timestamp > lastSeenTimestamp.current!);
    if (newEvents.length === 0) return;

    lastSeenTimestamp.current = newEvents[newEvents.length - 1].timestamp;

    const now = Date.now();
    for (const event of newEvents) {
      const lastFired = notifiedAlerts.current.get(event.rule_id);
      if (lastFired && now - lastFired < ALERT_COOLDOWN_MS) continue;

      notifiedAlerts.current.set(event.rule_id, now);
      fireNotification(
        `DriveChill Alert: ${event.sensor_name}`,
        event.message || `${event.actual_value}° exceeded threshold ${event.threshold}°`,
        `alert-${event.rule_id}`,
      );
    }

    // Prune old entries to prevent memory growth
    if (notifiedAlerts.current.size > 100) {
      const cutoff = now - ALERT_COOLDOWN_MS * 2;
      for (const [key, ts] of notifiedAlerts.current) {
        if (ts < cutoff) notifiedAlerts.current.delete(key);
      }
    }
  }, [alertEvents, canNotify, fireNotification]);

  // Fire notification on safe-mode changes (panic events)
  useEffect(() => {
    if (!canNotify()) return;

    const reason = safeMode.reason;

    // First render: record current state without firing (prevents spurious
    // notification on page refresh when safe mode is already active).
    if (lastSafeModeReason.current === undefined) {
      lastSafeModeReason.current = reason;
      return;
    }

    if (reason === lastSafeModeReason.current) return;
    lastSafeModeReason.current = reason;

    if (reason === 'sensor_failure') {
      fireNotification(
        'DriveChill: Sensor Failure',
        'Sensor reads failed repeatedly — all fans forced to 100%.',
        'safe-mode',
      );
    } else if (reason === 'temp_panic') {
      fireNotification(
        'DriveChill: Temperature Panic',
        'Critical temperature threshold exceeded — all fans forced to 100%.',
        'safe-mode',
      );
    }
  }, [safeMode, canNotify, fireNotification]);
}

/**
 * Request browser notification permission. Returns the resulting permission state.
 */
export async function requestNotificationPermission(): Promise<NotificationPermission | 'unsupported'> {
  if (typeof window === 'undefined' || !('Notification' in window)) {
    return 'unsupported';
  }
  if (Notification.permission === 'granted') return 'granted';
  if (Notification.permission === 'denied') return 'denied';
  try {
    return await Notification.requestPermission();
  } catch {
    return 'denied';
  }
}
