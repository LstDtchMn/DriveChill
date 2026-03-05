import { create } from 'zustand';

interface AuthState {
  /** Whether the backend requires authentication. */
  authRequired: boolean;
  /** Whether the current session is authenticated. */
  authenticated: boolean;
  /** Authenticated username (if any). */
  username: string | null;
  /** Role of the authenticated user: 'admin' | 'viewer' */
  role: 'admin' | 'viewer';
  /** True while the initial session check is in flight. */
  checking: boolean;

  setAuth: (authRequired: boolean, authenticated: boolean, username?: string | null, role?: string) => void;
  setChecking: (v: boolean) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  authRequired: false,
  authenticated: false,
  username: null,
  role: 'admin',
  checking: true,

  setAuth: (authRequired, authenticated, username = null, role = 'admin') =>
    set({ authRequired, authenticated, username, role: role === 'viewer' ? 'viewer' : 'admin', checking: false }),
  setChecking: (v) => set({ checking: v }),
  logout: () => set({ authenticated: false, username: null, role: 'viewer' }),
}));
