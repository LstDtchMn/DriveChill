'use client';

import { useAppStore } from '@/stores/appStore';
import { Sun, Moon } from 'lucide-react';

export function ThemeToggle() {
  const { isDark, toggleTheme } = useAppStore();

  return (
    <button
      onClick={toggleTheme}
      className="p-2 rounded-lg transition-colors duration-200 hover:bg-surface-200"
      aria-label="Toggle theme"
    >
      {isDark ? (
        <Sun size={18} className="text-surface-600" />
      ) : (
        <Moon size={18} className="text-surface-600" />
      )}
    </button>
  );
}
