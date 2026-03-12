'use client';

import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';

type ToastType = 'success' | 'error' | 'info';

interface Toast {
  id: number;
  message: string;
  type: ToastType;
}

interface ToastContextValue {
  toast: (message: string, type?: ToastType) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

let _id = 0;

const TOAST_COLORS: Record<ToastType, string> = {
  success: 'var(--success)',
  error:   'var(--danger)',
  info:    'var(--accent)',
};

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const timersRef = useRef<Set<ReturnType<typeof setTimeout>>>(new Set());

  // Clean up all pending timers on unmount
  useEffect(() => () => { timersRef.current.forEach(clearTimeout); }, []);

  const toast = useCallback((message: string, type: ToastType = 'info') => {
    const id = ++_id;
    setToasts((prev) => [...prev, { id, message, type }]);
    const timer = setTimeout(() => {
      timersRef.current.delete(timer);
      setToasts((prev) => prev.filter((t) => t.id !== id));
    }, 4000);
    timersRef.current.add(timer);
  }, []);

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div
        style={{
          position: 'fixed', bottom: '1.5rem', right: '1.5rem',
          zIndex: 10000, display: 'flex', flexDirection: 'column', gap: '0.5rem',
          pointerEvents: 'none',
        }}
      >
        {toasts.map((t) => (
          <div
            key={t.id}
            style={{
              background: 'var(--card-bg)',
              border: `1px solid ${TOAST_COLORS[t.type]}`,
              borderLeft: `4px solid ${TOAST_COLORS[t.type]}`,
              color: 'var(--text)',
              padding: '0.75rem 1rem',
              borderRadius: '0.375rem',
              boxShadow: '0 4px 12px rgba(0,0,0,0.3)',
              maxWidth: 360,
              fontSize: '0.875rem',
              lineHeight: 1.4,
              pointerEvents: 'auto',
              animation: 'fadeIn 0.2s ease',
            }}
          >
            {t.message}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): (message: string, type?: ToastType) => void {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used within ToastProvider');
  return ctx.toast;
}
