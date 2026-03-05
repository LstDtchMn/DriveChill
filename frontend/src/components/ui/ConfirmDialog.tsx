'use client';

import { createContext, useCallback, useContext, useRef, useState } from 'react';

interface ConfirmOptions {
  title?: string;
  message: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
}

interface ConfirmContextValue {
  confirm: (options: ConfirmOptions | string) => Promise<boolean>;
}

const ConfirmContext = createContext<ConfirmContextValue | null>(null);

interface PendingConfirm {
  options: ConfirmOptions;
  resolve: (value: boolean) => void;
}

export function ConfirmDialogProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = useState<PendingConfirm | null>(null);
  const resolveRef = useRef<((value: boolean) => void) | null>(null);

  const confirm = useCallback((options: ConfirmOptions | string): Promise<boolean> => {
    const opts: ConfirmOptions = typeof options === 'string' ? { message: options } : options;
    return new Promise((resolve) => {
      resolveRef.current = resolve;
      setPending({ options: opts, resolve });
    });
  }, []);

  const handleResponse = (value: boolean) => {
    resolveRef.current?.(value);
    setPending(null);
  };

  return (
    <ConfirmContext.Provider value={{ confirm }}>
      {children}
      {pending && (
        <div
          style={{
            position: 'fixed', inset: 0, zIndex: 9999,
            background: 'rgba(0,0,0,0.5)',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            padding: '1rem',
          }}
          onClick={() => handleResponse(false)}
        >
          <div
            className="card"
            style={{ maxWidth: 420, width: '100%', padding: '1.5rem' }}
            onClick={(e) => e.stopPropagation()}
          >
            {pending.options.title && (
              <h3
                className="section-title"
                style={{
                  marginBottom: '0.75rem',
                  color: pending.options.danger ? 'var(--danger)' : 'var(--text)',
                }}
              >
                {pending.options.title}
              </h3>
            )}
            <p style={{ color: 'var(--text-secondary)', marginBottom: '1.5rem', lineHeight: 1.5 }}>
              {pending.options.message}
            </p>
            <div style={{ display: 'flex', gap: '0.75rem', justifyContent: 'flex-end' }}>
              <button
                className="btn-secondary"
                onClick={() => handleResponse(false)}
              >
                {pending.options.cancelLabel ?? 'Cancel'}
              </button>
              <button
                className={pending.options.danger ? 'btn-primary' : 'btn-primary'}
                style={pending.options.danger ? { background: 'var(--danger)', color: '#fff' } : undefined}
                onClick={() => handleResponse(true)}
              >
                {pending.options.confirmLabel ?? 'Confirm'}
              </button>
            </div>
          </div>
        </div>
      )}
    </ConfirmContext.Provider>
  );
}

export function useConfirm(): (options: ConfirmOptions | string) => Promise<boolean> {
  const ctx = useContext(ConfirmContext);
  if (!ctx) throw new Error('useConfirm must be used within ConfirmDialogProvider');
  return ctx.confirm;
}
