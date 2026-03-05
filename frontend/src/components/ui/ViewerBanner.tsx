'use client';

import { useCanWrite } from '@/hooks/useCanWrite';
import { useAuthStore } from '@/stores/authStore';

/**
 * Thin info bar shown to viewer-role users on write-heavy pages.
 * Renders nothing when auth is not required or the user can write.
 */
export function ViewerBanner() {
  const canWrite = useCanWrite();
  const authRequired = useAuthStore((s) => s.authRequired);

  if (!authRequired || canWrite) return null;

  return (
    <div
      className="px-4 py-2 text-xs font-medium flex items-center gap-2 rounded-lg mb-4"
      style={{
        background: 'var(--accent-muted)',
        border: '1px solid var(--accent)',
        color: 'var(--accent)',
      }}
    >
      <span>Read-only access — contact your admin to make changes.</span>
    </div>
  );
}
