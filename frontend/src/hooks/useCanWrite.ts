import { useAuthStore } from '@/stores/authStore';

/**
 * Returns true when the current session is allowed to perform write operations.
 * When auth is not required (localhost binding), always returns true.
 * When auth is required, only admin-role sessions can write.
 */
export function useCanWrite(): boolean {
  const { role, authRequired } = useAuthStore();
  if (!authRequired) return true;
  return role === 'admin';
}
