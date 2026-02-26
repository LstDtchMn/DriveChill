'use client';

import { useEffect, useState, useRef } from 'react';
import { Volume, VolumeX, Zap, Gauge, Wind, Download, Upload, Gamepad2, Palette, Moon } from 'lucide-react';
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
  gaming: Gamepad2,
  rendering: Palette,
  sleep: Moon,
  custom: Volume,
};

const PRESET_DESCRIPTIONS: Record<string, string> = {
  silent: 'Fans stay quiet until temps get high',
  balanced: 'Even balance of noise and cooling',
  performance: 'Aggressive cooling for heavy workloads',
  full_speed: 'All fans at maximum speed',
  gaming: 'Aggressive GPU cooling, quiet CPU at idle',
  rendering: 'Sustained balanced cooling for long workloads',
  sleep: 'Absolute minimum RPM for silent idle',
  custom: 'User-defined fan curves',
};

export function PresetSelector({ onProfileChange }: PresetSelectorProps) {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [loading, setLoading] = useState(true);
  const fileInputRef = useRef<HTMLInputElement>(null);

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
      alert('Failed to activate profile.');
    }
  };

  const handleExport = async (e: React.MouseEvent, profileId: string) => {
    e.stopPropagation();
    try {
      const data = await api.exportProfile(profileId);
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      const name = data.profile?.name ?? 'profile';
      a.download = `drivechill-profile-${name.toLowerCase().replace(/\s+/g, '-')}.json`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      alert('Failed to export profile.');
    }
  };

  const handleImport = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      const text = await file.text();
      const data = JSON.parse(text);
      // Support both raw profile object and wrapped export format
      const profile = data.profile ?? data;
      await api.importProfile(profile);
      await fetchProfiles();
      onProfileChange?.();
    } catch {
      alert('Failed to import profile. Check that the file is valid JSON.');
    }
    // Reset file input so re-selecting the same file works
    if (fileInputRef.current) fileInputRef.current.value = '';
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
      <div className="flex items-center justify-between mb-3 px-1">
        <h3 className="section-title">Preset Profiles</h3>
        <div className="flex items-center gap-2">
          <input
            ref={fileInputRef}
            type="file"
            accept=".json"
            className="hidden"
            onChange={handleImport}
          />
          <button
            onClick={() => fileInputRef.current?.click()}
            className="btn-secondary flex items-center gap-1.5 text-xs"
          >
            <Upload size={13} />
            Import
          </button>
        </div>
      </div>
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
        {profiles.map((profile) => {
          const Icon = PRESET_ICONS[profile.preset] || Volume;
          const description = PRESET_DESCRIPTIONS[profile.preset] || '';

          return (
            <div
              key={profile.id}
              className={`card p-4 text-left transition-all duration-200 animate-card-enter relative group ${
                profile.is_active ? 'ring-2' : 'hover:scale-[1.02]'
              }`}
              style={profile.is_active ? {
                borderColor: 'var(--accent)',
                boxShadow: '0 0 0 2px var(--accent-muted)',
              } : {}}
            >
              <button
                onClick={() => handleActivate(profile.id)}
                className="w-full text-left"
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
              <button
                onClick={(e) => handleExport(e, profile.id)}
                className="absolute top-2 right-2 p-1.5 rounded opacity-0 group-hover:opacity-100 transition-opacity"
                style={{ background: 'var(--surface-200)' }}
                title="Export profile"
              >
                <Download size={12} style={{ color: 'var(--text-secondary)' }} />
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}
