'use client';

import { useState, type FormEvent } from 'react';
import { authApi } from '@/lib/api';
import { useAuthStore } from '@/stores/authStore';
import { APIError } from '@/lib/api';

export function LoginPage() {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const setAuth = useAuthStore((s) => s.setAuth);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setError(null);
    setLoading(true);

    try {
      const result = await authApi.login(username, password);
      // Re-fetch session to get role
      const session = await authApi.checkSession().catch(() => null);
      setAuth(true, true, result.username, session?.role ?? 'viewer');
    } catch (err) {
      if (err instanceof APIError) {
        if (err.status === 429) {
          setError('Too many attempts. Please wait before trying again.');
        } else if (err.status === 401) {
          setError('Invalid username or password.');
        } else {
          setError('Login failed. Please try again.');
        }
      } else {
        setError('Unable to connect to server.');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div
      className="min-h-screen flex items-center justify-center p-4"
      style={{ background: 'var(--bg)' }}
    >
      <div className="card p-8 w-full max-w-sm">
        <div className="text-center mb-6">
          <h1 className="text-2xl font-bold" style={{ color: 'var(--text)' }}>
            DriveChill
          </h1>
          <p className="text-sm mt-1" style={{ color: 'var(--text-secondary)' }}>
            Sign in to continue
          </p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label
              htmlFor="username"
              className="block text-xs font-medium mb-1"
              style={{ color: 'var(--text-secondary)' }}
            >
              Username
            </label>
            <input
              id="username"
              type="text"
              autoComplete="username"
              required
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              className="w-full px-3 py-2 rounded-lg border text-sm outline-none transition-colors"
              style={{
                background: 'var(--bg-secondary)',
                borderColor: 'var(--border)',
                color: 'var(--text)',
              }}
              onFocus={(e) => (e.target.style.borderColor = 'var(--accent)')}
              onBlur={(e) => (e.target.style.borderColor = 'var(--border)')}
            />
          </div>

          <div>
            <label
              htmlFor="password"
              className="block text-xs font-medium mb-1"
              style={{ color: 'var(--text-secondary)' }}
            >
              Password
            </label>
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full px-3 py-2 rounded-lg border text-sm outline-none transition-colors"
              style={{
                background: 'var(--bg-secondary)',
                borderColor: 'var(--border)',
                color: 'var(--text)',
              }}
              onFocus={(e) => (e.target.style.borderColor = 'var(--accent)')}
              onBlur={(e) => (e.target.style.borderColor = 'var(--border)')}
            />
          </div>

          {error && (
            <p className="text-xs font-medium" style={{ color: 'var(--danger)' }}>
              {error}
            </p>
          )}

          <button
            type="submit"
            disabled={loading}
            className="btn-primary w-full disabled:opacity-50"
          >
            {loading ? 'Signing in...' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  );
}
