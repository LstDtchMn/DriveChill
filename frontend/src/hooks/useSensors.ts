'use client';

import { useMemo } from 'react';
import { useAppStore } from '@/stores/appStore';
import type { SensorReading } from '@/lib/types';

export function useSensors() {
  const readings = useAppStore((s) => s.readings);

  return useMemo(() => {
    const cpuTemps: SensorReading[] = [];
    const gpuTemps: SensorReading[] = [];
    const hddTemps: SensorReading[] = [];
    const caseTemps: SensorReading[] = [];
    const cpuLoads: SensorReading[] = [];
    const gpuLoads: SensorReading[] = [];
    const fanRpms: SensorReading[] = [];
    const fanPcts: SensorReading[] = [];

    for (const r of readings) {
      switch (r.sensor_type) {
        case 'cpu_temp': cpuTemps.push(r); break;
        case 'gpu_temp': gpuTemps.push(r); break;
        case 'hdd_temp': hddTemps.push(r); break;
        case 'case_temp': caseTemps.push(r); break;
        case 'cpu_load': cpuLoads.push(r); break;
        case 'gpu_load': gpuLoads.push(r); break;
        case 'fan_rpm': fanRpms.push(r); break;
        case 'fan_percent': fanPcts.push(r); break;
      }
    }

    // Primary values (first sensor of each type, or average)
    const cpuTemp = cpuTemps[0]?.value ?? 0;
    const gpuTemp = gpuTemps[0]?.value ?? 0;
    const hddTemp = hddTemps[0]?.value ?? 0;
    const caseTemp = caseTemps[0]?.value ?? 0;
    const cpuLoad = cpuLoads[0]?.value ?? 0;
    const gpuLoad = gpuLoads[0]?.value ?? 0;

    return {
      all: readings,
      cpuTemps, gpuTemps, hddTemps, caseTemps,
      cpuLoads, gpuLoads, fanRpms, fanPcts,
      cpuTemp, gpuTemp, hddTemp, caseTemp,
      cpuLoad, gpuLoad,
    };
  }, [readings]);
}
