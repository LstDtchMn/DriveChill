import { create } from 'zustand';

interface AuthState {
  /** Whether the backend requires authentication. */
  authRequired: boolean;
  /** Whether the current session is authenticated. */
  authenticated: boolean;
  /** Authenticated username (if any). */
  username: string | null;
  /** True while the initial session check is in flight. */
  checking: boolean;

  setAuth: (authRequired: boolean, authenticated: boolean, username?: string | null) => void;
  setChecking: (v: boolean) => void;
  logout: () => void;
}

export const useAuthStore = create<AuthState>((set) => ({
  authRequired: false,
  authenticated: false,
  username: null,
  checking: true,

  setAuth: (authRequired, authenticated, username = null) =>
    set({ authRequired, authenticated, username, checking: false }),
  setChecking: (v) => set({ checking: v }),
  logout: () => set({ authenticated: false, username: null }),
}));
