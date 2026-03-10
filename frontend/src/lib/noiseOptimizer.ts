import type { NoiseProfile, NoiseDataPoint } from './types';

export interface NoiseRecommendation {
  fanId: string;
  fanName: string;
  currentPercent: number;
  recommendedPercent: number;
  noiseReductionDb: number;
  temperatureMarginC: number;
  message: string;
}

interface FanInput {
  fanId: string;
  fanName: string;
  percent: number;
}

interface TempTargetInput {
  sensorId: string;
  target: number;
  current: number;
}

/**
 * Interpolate dB at a given percent from noise profile data points.
 * Data points are keyed by RPM; we treat them as ordered by index (ascending speed)
 * and map the percent linearly across the data range.
 */
function interpolateDb(data: NoiseDataPoint[], percent: number): number | null {
  if (data.length === 0) return null;

  // Sort by rpm ascending
  const sorted = [...data].sort((a, b) => a.rpm - b.rpm);

  // Map percent (0–100) onto the rpm range
  const minRpm = sorted[0].rpm;
  const maxRpm = sorted[sorted.length - 1].rpm;
  const targetRpm = minRpm + (percent / 100) * (maxRpm - minRpm);

  if (sorted.length === 1) return sorted[0].db;

  // Find surrounding data points and interpolate
  for (let i = 0; i < sorted.length - 1; i++) {
    const lo = sorted[i];
    const hi = sorted[i + 1];
    if (targetRpm >= lo.rpm && targetRpm <= hi.rpm) {
      const t = (targetRpm - lo.rpm) / (hi.rpm - lo.rpm);
      return lo.db + t * (hi.db - lo.db);
    }
  }

  // Clamp to edges
  if (targetRpm < sorted[0].rpm) return sorted[0].db;
  return sorted[sorted.length - 1].db;
}

export function computeNoiseRecommendations(
  profiles: NoiseProfile[],
  currentFanSpeeds: FanInput[],
  temperatureTargets: TempTargetInput[],
): NoiseRecommendation[] {
  // Build a map from fanId -> profile
  const profileByFan = new Map<string, NoiseProfile>();
  for (const p of profiles) {
    // Keep the most recent profile if duplicates exist
    const existing = profileByFan.get(p.fan_id);
    if (!existing || new Date(p.created_at) > new Date(existing.created_at)) {
      profileByFan.set(p.fan_id, p);
    }
  }

  // Compute overall temperature safety margin:
  // margin = min(target - current) across all targets
  // A positive margin means temperatures are below targets.
  const globalMarginC = temperatureTargets.length > 0
    ? Math.min(...temperatureTargets.map((t) => t.target - t.current))
    : 0;

  // For each fan with a profile and current speed > 0, compute what stepping down would give
  interface Candidate {
    fanId: string;
    fanName: string;
    currentPercent: number;
    recommendedPercent: number;
    dbCurrent: number;
    dbRecommended: number;
    noiseReductionDb: number;
    marginC: number;
    ratio: number; // dB saved per degree of margin consumed
  }

  const candidates: Candidate[] = [];

  for (const fan of currentFanSpeeds) {
    if (fan.percent <= 0) continue;

    const profile = profileByFan.get(fan.fanId);
    if (!profile || profile.data.length === 0) continue;

    const dbNow = interpolateDb(profile.data, fan.percent);
    if (dbNow === null) continue;

    // Try stepping down in 10% increments
    const STEP = 10;
    const minPercent = Math.max(0, fan.percent - STEP);

    if (minPercent >= fan.percent) continue;

    const dbLower = interpolateDb(profile.data, minPercent);
    if (dbLower === null) continue;

    const noiseReductionDb = dbNow - dbLower;

    // Only recommend if there's actual noise reduction
    if (noiseReductionDb <= 0) continue;

    // Estimate temperature impact: reducing fan speed consumes margin.
    // We use globalMarginC as the remaining safety window.
    // Conservative heuristic: each 10% fan reduction "consumes" up to 5°C of margin.
    const MARGIN_CONSUMED_PER_STEP = 5;
    const remainingMarginC = globalMarginC - MARGIN_CONSUMED_PER_STEP;

    // Only recommend if we still have positive margin after reduction
    if (remainingMarginC <= 0) continue;

    // ratio: dB saved per degree of margin consumed (higher = better trade-off)
    const ratio = noiseReductionDb / MARGIN_CONSUMED_PER_STEP;

    candidates.push({
      fanId: fan.fanId,
      fanName: fan.fanName,
      currentPercent: fan.percent,
      recommendedPercent: minPercent,
      dbCurrent: dbNow,
      dbRecommended: dbLower,
      noiseReductionDb,
      marginC: remainingMarginC,
      ratio,
    });
  }

  // Sort by ratio descending (best noise/margin trade-off first)
  candidates.sort((a, b) => b.ratio - a.ratio);

  return candidates.map((c) => ({
    fanId: c.fanId,
    fanName: c.fanName,
    currentPercent: c.currentPercent,
    recommendedPercent: c.recommendedPercent,
    noiseReductionDb: Math.round(c.noiseReductionDb * 10) / 10,
    temperatureMarginC: Math.round(c.marginC * 10) / 10,
    message: `Reduce ${c.fanName} from ${c.currentPercent}% to ${c.recommendedPercent}% — saves ~${(Math.round(c.noiseReductionDb * 10) / 10).toFixed(1)} dB, target still met with ${(Math.round(c.marginC * 10) / 10).toFixed(1)}°C margin`,
  }));
}
