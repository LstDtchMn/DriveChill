'use client';

import { useEffect, useState } from 'react';
import { Volume, VolumeX, Zap, Gauge, Wind } from 'lucide-react';
import { api } from '@/lib/api';
import type { Profile } from '@/lib/types';

interface PresetSelectorProps {
  onProfileChange?: () => void;
}

const PRESET_ICONS: Record<string, typeof Zap> = {
  silent: VolumeX,
  balanced: Gauge,
  performance: Zap,
  full_speed: Wind,
  custom: Volume,
};

const PRESET_DESCRIPTIONS: Record<string, string> = {
  silent: 'Fans stay quiet until temps get high',
  balanced: 'Even balance of noise and cooling',
  performance: 'Aggressive cooling for heavy workloads',
  full_speed: 'All fans at maximum speed',
  custom: 'User-defined fan curves',
};

export function PresetSelector({ onProfileChange }: PresetSelectorProps) {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [loading, setLoading] = useState(true);

  const fetchProfiles = async () => {
    try {
      const { profiles: p } = await api.getProfiles();
      setProfiles(p);
    } catch {
      // API not available
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchProfiles();
  }, []);

  const handleActivate = async (id: string) => {
    try {
      await api.activateProfile(id);
      await fetchProfiles();
      onProfileChange?.();
    } catch {
      // Handle error
    }
  };

  if (loading) {
    return (
      <div className="card p-6 flex items-center justify-center">
        <p className="text-sm" style={{ color: 'var(--text-secondary)' }}>Loading profiles...</p>
      </div>
    );
  }

  return (
    <div>
      <h3 className="section-title mb-3 px-1">Preset Profiles</h3>
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
        {profiles.map((profile) => {
          const Icon = PRESET_ICONS[profile.preset] || Volume;
          const description = PRESET_DESCRIPTIONS[profile.preset] || '';

          return (
            <button
              key={profile.id}
              onClick={() => handleActivate(profile.id)}
              className={`card p-4 text-left transition-all duration-200 animate-card-enter ${
                profile.is_active ? 'ring-2' : 'hover:scale-[1.02]'
              }`}
              style={profile.is_active ? {
                borderColor: 'var(--accent)',
                boxShadow: '0 0 0 2px var(--accent-muted)',
              } : {}}
              >
              <div className="flex items-center gap-2 mb-2">
                <div
                  className="w-8 h-8 rounded-lg flex items-center justify-center"
                  style={{
                    background: profile.is_active ? 'var(--accent)' : 'var(--surface-200)',
                  }}
                >
                  <Icon size={16} style={{ color: profile.is_active ? 'white' : 'var(--text-secondary)' }} />
                </div>
                {profile.is_active && (
                  <span className="badge badge-success">Active</span>
                )}
              </div>
              <p className="text-sm font-semibold mb-1" style={{ color: 'var(--text)' }}>
                {profile.name}
              </p>
              <p className="text-xs leading-relaxed" style={{ color: 'var(--text-secondary)' }}>
                {description}
              </p>
            </button>
          );
        })}
      </div>
    </div>
  );
}
