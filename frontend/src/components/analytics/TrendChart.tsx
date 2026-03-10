'use client';

import { useState, useRef, useCallback, useEffect, useMemo } from 'react';
import { lttbDownsample, type DataPoint } from '@/lib/downsample';
import type { AnalyticsBucket } from '@/lib/types';

export interface TrendChartProps {
  buckets: AnalyticsBucket[];
  sensorId: string;
  sensorName: string;
  unit: string;
  fmt: (v: number, unit: string) => string;
}

const PAD = { top: 16, right: 16, bottom: 32, left: 52 };
const CHART_HEIGHT = 300;
const MAX_POINTS = 500;

function formatXLabel(epoch: number, rangeMs: number): string {
  const d = new Date(epoch);
  if (rangeMs <= 2 * 60 * 60 * 1000) {
    // ≤ 2h → HH:MM:SS
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
  if (rangeMs <= 3 * 24 * 60 * 60 * 1000) {
    // ≤ 3d → HH:MM
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }
  // > 3d → MM/DD
  return d.toLocaleDateString([], { month: '2-digit', day: '2-digit' });
}

interface TooltipState {
  x: number;
  y: number;
  value: number;
  min: number;
  max: number;
  epoch: number;
}

export function TrendChart({ buckets, sensorId: _sensorId, unit, fmt }: TrendChartProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const [svgWidth, setSvgWidth] = useState(600);

  // Zoom state: null = full range, else [startEpoch, endEpoch]
  const [zoomRange, setZoomRange] = useState<[number, number] | null>(null);
  // Drag state
  const [dragStart, setDragStart] = useState<number | null>(null); // SVG x px
  const [dragEnd, setDragEnd] = useState<number | null>(null);     // SVG x px
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);

  // Observe container width
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width;
      if (w && w > 0) setSvgWidth(w);
    });
    ro.observe(el);
    setSvgWidth(el.clientWidth || 600);
    return () => ro.disconnect();
  }, []);

  const innerW = svgWidth - PAD.left - PAD.right;
  const innerH = CHART_HEIGHT - PAD.top - PAD.bottom;

  // Sort + derive full raw series
  const sorted = useMemo(() => {
    return [...buckets].sort(
      (a, b) => new Date(a.timestamp_utc).getTime() - new Date(b.timestamp_utc).getTime()
    );
  }, [buckets]);

  const fullEpochs = useMemo(() => sorted.map((b) => new Date(b.timestamp_utc).getTime()), [sorted]);

  // Apply zoom filter
  const visibleBuckets = useMemo(() => {
    if (!zoomRange) return sorted;
    const [zs, ze] = zoomRange;
    return sorted.filter((b, i) => {
      const e = fullEpochs[i];
      return e >= zs && e <= ze;
    });
  }, [sorted, zoomRange, fullEpochs]);

  // LTTB downsample for rendering
  const downsampled = useMemo<{ raw: AnalyticsBucket[]; pts: DataPoint[] }>(() => {
    const rawPts: DataPoint[] = visibleBuckets.map((b) => ({
      x: new Date(b.timestamp_utc).getTime(),
      y: b.avg_value,
    }));
    const ds = lttbDownsample(rawPts, MAX_POINTS);
    // Re-map downsampled indices back to buckets for min/max
    const dsSet = new Set(ds.map((p) => p.x));
    const dsRaw = visibleBuckets.filter((b) =>
      dsSet.has(new Date(b.timestamp_utc).getTime())
    );
    return { raw: dsRaw, pts: ds };
  }, [visibleBuckets]);

  const { pts, raw: dsRaw } = downsampled;

  // Compute scales
  const xMin = pts.length ? pts[0].x : 0;
  const xMax = pts.length ? pts[pts.length - 1].x : 1;
  const xRange = xMax - xMin || 1;

  const allY = pts.map((p) => p.y).concat(
    dsRaw.flatMap((b) => [b.min_value, b.max_value])
  );
  const yMin = allY.length ? Math.min(...allY) : 0;
  const yMax = allY.length ? Math.max(...allY) : 1;
  const yPad = (yMax - yMin) * 0.08 || 1;
  const yLow = yMin - yPad;
  const yHigh = yMax + yPad;
  const yRange = yHigh - yLow || 1;

  const toSvgX = useCallback((epoch: number) => PAD.left + ((epoch - xMin) / xRange) * innerW, [xMin, xRange, innerW]);
  const toSvgY = useCallback((val: number) => PAD.top + innerH - ((val - yLow) / yRange) * innerH, [yLow, yRange, innerH]);
  const toEpoch = useCallback((svgX: number) => xMin + ((svgX - PAD.left) / innerW) * xRange, [xMin, xRange, innerW]);

  // Build SVG paths
  const linePath = useMemo(() => {
    if (pts.length < 2) return '';
    return pts.map((p, i) => `${i === 0 ? 'M' : 'L'}${toSvgX(p.x).toFixed(2)},${toSvgY(p.y).toFixed(2)}`).join(' ');
  }, [pts, toSvgX, toSvgY]);

  const bandPath = useMemo(() => {
    if (dsRaw.length < 2) return '';
    const top = dsRaw.map((b, i) => {
      const x = toSvgX(new Date(b.timestamp_utc).getTime());
      const y = toSvgY(b.max_value);
      return `${i === 0 ? 'M' : 'L'}${x.toFixed(2)},${y.toFixed(2)}`;
    });
    const bottom = [...dsRaw].reverse().map((b) => {
      const x = toSvgX(new Date(b.timestamp_utc).getTime());
      const y = toSvgY(b.min_value);
      return `L${x.toFixed(2)},${y.toFixed(2)}`;
    });
    return `${top.join(' ')} ${bottom.join(' ')} Z`;
  }, [dsRaw, toSvgX, toSvgY]);

  // Y-axis ticks
  const yTicks = useMemo(() => {
    const count = 5;
    return Array.from({ length: count }, (_, i) => {
      const val = yLow + (i / (count - 1)) * yRange;
      return { val, y: toSvgY(val) };
    });
  }, [yLow, yRange, toSvgY]);

  // X-axis ticks
  const xTicks = useMemo(() => {
    if (pts.length === 0) return [];
    const count = Math.min(6, pts.length);
    const step = Math.floor(pts.length / (count - 1 || 1));
    const indices: number[] = [];
    for (let i = 0; i < pts.length; i += step) indices.push(i);
    if (indices[indices.length - 1] !== pts.length - 1) indices.push(pts.length - 1);
    return indices.slice(0, count).map((idx) => ({
      epoch: pts[idx].x,
      svgX: toSvgX(pts[idx].x),
    }));
  }, [pts, toSvgX]);

  // Mouse / touch helpers
  const getSvgX = useCallback((clientX: number): number => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect) return 0;
    return clientX - rect.left;
  }, []);

  const clampSvgX = (x: number) => Math.max(PAD.left, Math.min(PAD.left + innerW, x));

  const findNearestPoint = useCallback(
    (svgX: number): { bucket: AnalyticsBucket; pt: DataPoint; idx: number } | null => {
      if (pts.length === 0) return null;
      let best = 0;
      let bestDist = Infinity;
      for (let i = 0; i < pts.length; i++) {
        const dist = Math.abs(toSvgX(pts[i].x) - svgX);
        if (dist < bestDist) { bestDist = dist; best = i; }
      }
      return { bucket: dsRaw[best], pt: pts[best], idx: best };
    },
    [pts, dsRaw, toSvgX]
  );

  const showTooltip = useCallback(
    (clientX: number, clientY: number) => {
      const rect = svgRef.current?.getBoundingClientRect();
      if (!rect) return;
      const svgX = getSvgX(clientX);
      const nearest = findNearestPoint(svgX);
      if (!nearest) return;
      setTooltip({
        x: toSvgX(nearest.pt.x),
        y: toSvgY(nearest.pt.y),
        value: nearest.pt.y,
        min: nearest.bucket.min_value,
        max: nearest.bucket.max_value,
        epoch: nearest.pt.x,
      });
    },
    [getSvgX, findNearestPoint, toSvgX, toSvgY]
  );

  // Mouse handlers
  const onMouseMove = (e: React.MouseEvent<SVGSVGElement>) => {
    if (dragStart !== null) {
      setDragEnd(clampSvgX(getSvgX(e.clientX)));
    } else {
      showTooltip(e.clientX, e.clientY);
    }
  };

  const onMouseDown = (e: React.MouseEvent<SVGSVGElement>) => {
    if (pts.length === 0) return;
    const sx = clampSvgX(getSvgX(e.clientX));
    setDragStart(sx);
    setDragEnd(sx);
    setTooltip(null);
  };

  const onMouseUp = (e: React.MouseEvent<SVGSVGElement>) => {
    if (dragStart === null || dragEnd === null) return;
    const x1 = Math.min(dragStart, dragEnd);
    const x2 = Math.max(dragStart, dragEnd);
    if (x2 - x1 > 5) {
      const e1 = toEpoch(x1);
      const e2 = toEpoch(x2);
      setZoomRange([e1, e2]);
    }
    setDragStart(null);
    setDragEnd(null);
  };

  const onMouseLeave = () => {
    if (dragStart !== null) {
      // If dragging and mouse leaves, cancel drag
      setDragStart(null);
      setDragEnd(null);
    }
    setTooltip(null);
  };

  // Touch handlers
  const onTouchStart = (e: React.TouchEvent<SVGSVGElement>) => {
    const touch = e.touches[0];
    const sx = clampSvgX(getSvgX(touch.clientX));
    setDragStart(sx);
    setDragEnd(sx);
    setTooltip(null);
  };

  const onTouchMove = (e: React.TouchEvent<SVGSVGElement>) => {
    const touch = e.touches[0];
    if (dragStart !== null) {
      setDragEnd(clampSvgX(getSvgX(touch.clientX)));
    } else {
      showTooltip(touch.clientX, touch.clientY);
    }
  };

  const onTouchEnd = () => {
    if (dragStart !== null && dragEnd !== null) {
      const x1 = Math.min(dragStart, dragEnd);
      const x2 = Math.max(dragStart, dragEnd);
      if (x2 - x1 > 5) {
        setZoomRange([toEpoch(x1), toEpoch(x2)]);
      }
    }
    setDragStart(null);
    setDragEnd(null);
  };

  const resetZoom = () => {
    setZoomRange(null);
    setTooltip(null);
  };

  const dragRectLeft   = dragStart !== null && dragEnd !== null ? Math.min(dragStart, dragEnd) : 0;
  const dragRectWidth  = dragStart !== null && dragEnd !== null ? Math.abs(dragEnd - dragStart) : 0;
  const isDragging     = dragStart !== null && dragRectWidth > 2;
  const totalPoints    = sorted.length;
  const rangeMs        = xMax - xMin;

  if (pts.length < 2) {
    return (
      <div style={{ height: CHART_HEIGHT, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
        <span style={{ color: 'var(--text-secondary)', fontSize: 13 }}>Not enough data</span>
      </div>
    );
  }

  return (
    <div ref={containerRef} style={{ position: 'relative', width: '100%' }}>
      {/* Controls row */}
      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 4, gap: 8, alignItems: 'center' }}>
        {totalPoints > MAX_POINTS && (
          <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
            {MAX_POINTS} / {totalPoints} pts (LTTB)
          </span>
        )}
        {zoomRange && (
          <button
            onClick={resetZoom}
            className="btn-secondary text-xs px-2 py-0.5"
            style={{ fontSize: 11, minHeight: 24 }}
          >
            Reset zoom
          </button>
        )}
      </div>

      <svg
        ref={svgRef}
        width={svgWidth}
        height={CHART_HEIGHT}
        style={{ display: 'block', cursor: dragStart !== null ? 'col-resize' : 'crosshair', userSelect: 'none' }}
        onMouseDown={onMouseDown}
        onMouseMove={onMouseMove}
        onMouseUp={onMouseUp}
        onMouseLeave={onMouseLeave}
        onTouchStart={onTouchStart}
        onTouchMove={onTouchMove}
        onTouchEnd={onTouchEnd}
      >
        {/* Background */}
        <rect
          x={PAD.left} y={PAD.top}
          width={innerW} height={innerH}
          fill="transparent"
        />

        {/* Y grid lines */}
        {yTicks.map((t, i) => (
          <line
            key={i}
            x1={PAD.left} y1={t.y}
            x2={PAD.left + innerW} y2={t.y}
            stroke="var(--border)" strokeWidth="1" strokeDasharray="3,3"
          />
        ))}

        {/* Min/max band */}
        {bandPath && (
          <path d={bandPath} fill="var(--accent-muted)" fillOpacity="0.2" stroke="none" />
        )}

        {/* Line */}
        <path d={linePath} fill="none" stroke="var(--accent)" strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" />

        {/* Y-axis labels */}
        {yTicks.map((t, i) => (
          <text
            key={i}
            x={PAD.left - 6}
            y={t.y + 4}
            fontSize="10"
            fill="var(--text-secondary)"
            textAnchor="end"
          >
            {t.val.toFixed(1)}
          </text>
        ))}

        {/* X-axis labels */}
        {xTicks.map((t, i) => (
          <text
            key={i}
            x={t.svgX}
            y={PAD.top + innerH + 18}
            fontSize="10"
            fill="var(--text-secondary)"
            textAnchor="middle"
          >
            {formatXLabel(t.epoch, rangeMs)}
          </text>
        ))}

        {/* Axes */}
        <line x1={PAD.left} y1={PAD.top} x2={PAD.left} y2={PAD.top + innerH} stroke="var(--border)" strokeWidth="1" />
        <line x1={PAD.left} y1={PAD.top + innerH} x2={PAD.left + innerW} y2={PAD.top + innerH} stroke="var(--border)" strokeWidth="1" />

        {/* Drag selection rectangle */}
        {isDragging && (
          <rect
            x={dragRectLeft}
            y={PAD.top}
            width={dragRectWidth}
            height={innerH}
            fill="var(--accent)"
            fillOpacity="0.12"
            stroke="var(--accent)"
            strokeWidth="1"
            strokeDasharray="4,2"
          />
        )}

        {/* Tooltip crosshair */}
        {tooltip && !isDragging && (
          <>
            <line
              x1={tooltip.x} y1={PAD.top}
              x2={tooltip.x} y2={PAD.top + innerH}
              stroke="var(--accent)" strokeWidth="1" strokeDasharray="4,2" opacity="0.7"
            />
            <circle cx={tooltip.x} cy={tooltip.y} r={4} fill="var(--accent)" stroke="var(--card-bg)" strokeWidth="2" />
          </>
        )}
      </svg>

      {/* Floating tooltip */}
      {tooltip && !isDragging && (() => {
        const tipW = 160;
        const margin = 8;
        const tipX = tooltip.x + PAD.left + tipW + margin > svgWidth
          ? tooltip.x - tipW - margin
          : tooltip.x + margin;
        const tipY = Math.max(PAD.top, tooltip.y - 60);
        return (
          <div
            style={{
              position: 'absolute',
              left: tipX,
              top: tipY,
              background: 'var(--card-bg)',
              border: '1px solid var(--border)',
              borderRadius: 6,
              padding: '6px 10px',
              pointerEvents: 'none',
              fontSize: 11,
              lineHeight: 1.6,
              zIndex: 10,
              boxShadow: '0 2px 8px rgba(0,0,0,0.15)',
              minWidth: tipW,
            }}
          >
            <div style={{ color: 'var(--text-secondary)' }}>{new Date(tooltip.epoch).toLocaleString()}</div>
            <div style={{ color: 'var(--accent)', fontWeight: 700, fontSize: 13 }}>{fmt(tooltip.value, unit)}</div>
            <div style={{ color: 'var(--text-secondary)' }}>
              Min: {fmt(tooltip.min, unit)} / Max: {fmt(tooltip.max, unit)}
            </div>
          </div>
        );
      })()}

      {/* Hint */}
      <div style={{ textAlign: 'center', marginTop: 2 }}>
        <span style={{ fontSize: 10, color: 'var(--text-secondary)' }}>
          Drag to zoom &nbsp;·&nbsp; Hover to inspect
        </span>
      </div>
    </div>
  );
}
