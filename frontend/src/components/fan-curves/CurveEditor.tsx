'use client';

import { useState, useRef, useCallback, useEffect } from 'react';
import type { FanCurvePoint } from '@/lib/types';

interface CurveEditorProps {
  points: FanCurvePoint[];
  onChange: (points: FanCurvePoint[]) => void;
  width?: number;
  height?: number;
}

const PADDING = 40;
const GRID_LINES_X = 10;
const GRID_LINES_Y = 10;

export function CurveEditor({ points, onChange, width = 500, height = 300 }: CurveEditorProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [dragging, setDragging] = useState<number | null>(null);
  const [hoveredPoint, setHoveredPoint] = useState<number | null>(null);

  const chartW = width - PADDING * 2;
  const chartH = height - PADDING * 2;

  const tempToX = (temp: number) => PADDING + (temp / 110) * chartW;
  const speedToY = (speed: number) => PADDING + chartH - (speed / 100) * chartH;
  const xToTemp = (x: number) => Math.max(0, Math.min(110, ((x - PADDING) / chartW) * 110));
  const yToSpeed = (y: number) => Math.max(0, Math.min(100, ((PADDING + chartH - y) / chartH) * 100));

  const getSVGPoint = useCallback((e: React.MouseEvent | MouseEvent) => {
    if (!svgRef.current) return { x: 0, y: 0 };
    const rect = svgRef.current.getBoundingClientRect();
    return {
      x: e.clientX - rect.left,
      y: e.clientY - rect.top,
    };
  }, []);

  const handleMouseDown = useCallback((index: number, e: React.MouseEvent) => {
    e.preventDefault();
    setDragging(index);
  }, []);

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (dragging === null) return;

    const { x, y } = getSVGPoint(e);
    const temp = Math.round(xToTemp(x));
    const speed = Math.round(yToSpeed(y));

    const newPoints = [...points];
    newPoints[dragging] = { temp, speed };
    onChange(newPoints.sort((a, b) => a.temp - b.temp));
  }, [dragging, points, onChange, getSVGPoint, xToTemp, yToSpeed]);

  const handleMouseUp = useCallback(() => {
    setDragging(null);
  }, []);

  useEffect(() => {
    if (dragging !== null) {
      window.addEventListener('mousemove', handleMouseMove);
      window.addEventListener('mouseup', handleMouseUp);
      return () => {
        window.removeEventListener('mousemove', handleMouseMove);
        window.removeEventListener('mouseup', handleMouseUp);
      };
    }
  }, [dragging, handleMouseMove, handleMouseUp]);

  const handleDoubleClick = useCallback((e: React.MouseEvent) => {
    const { x, y } = getSVGPoint(e);
    const temp = Math.round(xToTemp(x));
    const speed = Math.round(yToSpeed(y));

    if (temp >= 0 && temp <= 110 && speed >= 0 && speed <= 100) {
      const newPoints = [...points, { temp, speed }].sort((a, b) => a.temp - b.temp);
      onChange(newPoints);
    }
  }, [points, onChange, getSVGPoint, xToTemp, yToSpeed]);

  const handleRightClick = useCallback((index: number, e: React.MouseEvent) => {
    e.preventDefault();
    if (points.length > 2) {
      const newPoints = points.filter((_, i) => i !== index);
      onChange(newPoints);
    }
  }, [points, onChange]);

  // Build the line path
  const sortedPoints = [...points].sort((a, b) => a.temp - b.temp);
  const pathD = sortedPoints
    .map((p, i) => `${i === 0 ? 'M' : 'L'} ${tempToX(p.temp)} ${speedToY(p.speed)}`)
    .join(' ');

  // Fill area under curve
  const fillD = sortedPoints.length > 0
    ? `${pathD} L ${tempToX(sortedPoints[sortedPoints.length - 1].temp)} ${speedToY(0)} L ${tempToX(sortedPoints[0].temp)} ${speedToY(0)} Z`
    : '';

  return (
    <div className="card p-4 animate-card-enter">
      <div className="flex items-center justify-between mb-3">
        <h3 className="section-title">Curve Editor</h3>
        <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
          Double-click to add point, right-click to remove
        </p>
      </div>

      <svg
        ref={svgRef}
        width={width}
        height={height}
        className="select-none"
        style={{ cursor: dragging !== null ? 'grabbing' : 'crosshair' }}
        onDoubleClick={handleDoubleClick}
      >
        {/* Grid lines */}
        {Array.from({ length: GRID_LINES_X + 1 }, (_, i) => {
          const x = PADDING + (i / GRID_LINES_X) * chartW;
          return (
            <line key={`gx-${i}`} x1={x} y1={PADDING} x2={x} y2={PADDING + chartH}
              stroke="var(--surface-200)" strokeWidth={1} />
          );
        })}
        {Array.from({ length: GRID_LINES_Y + 1 }, (_, i) => {
          const y = PADDING + (i / GRID_LINES_Y) * chartH;
          return (
            <line key={`gy-${i}`} x1={PADDING} y1={y} x2={PADDING + chartW} y2={y}
              stroke="var(--surface-200)" strokeWidth={1} />
          );
        })}

        {/* Axis labels */}
        {Array.from({ length: 6 }, (_, i) => {
          const temp = i * 22;
          return (
            <text key={`xl-${i}`} x={tempToX(temp)} y={height - 8}
              fill="var(--text-secondary)" fontSize={10} textAnchor="middle">
              {temp}°
            </text>
          );
        })}
        {Array.from({ length: 6 }, (_, i) => {
          const speed = i * 20;
          return (
            <text key={`yl-${i}`} x={PADDING - 8} y={speedToY(speed) + 4}
              fill="var(--text-secondary)" fontSize={10} textAnchor="end">
              {speed}%
            </text>
          );
        })}

        {/* Axis titles */}
        <text x={width / 2} y={height - 0} fill="var(--text-secondary)" fontSize={11} textAnchor="middle">
          Temperature (°C)
        </text>
        <text x={12} y={height / 2} fill="var(--text-secondary)" fontSize={11}
          textAnchor="middle" transform={`rotate(-90, 12, ${height / 2})`}>
          Fan Speed (%)
        </text>

        {/* Fill area */}
        {fillD && (
          <path d={fillD} fill="var(--accent)" opacity={0.08} />
        )}

        {/* Line */}
        {pathD && (
          <path d={pathD} fill="none" stroke="var(--accent)" strokeWidth={2.5}
            strokeLinejoin="round" strokeLinecap="round" />
        )}

        {/* Points */}
        {sortedPoints.map((p, i) => {
          const cx = tempToX(p.temp);
          const cy = speedToY(p.speed);
          const isHovered = hoveredPoint === i;
          const isDragged = dragging === i;

          return (
            <g key={i}>
              {/* Hit area (larger) */}
              <circle
                cx={cx} cy={cy} r={12}
                fill="transparent"
                style={{ cursor: 'grab' }}
                onMouseDown={(e) => handleMouseDown(i, e)}
                onMouseEnter={() => setHoveredPoint(i)}
                onMouseLeave={() => setHoveredPoint(null)}
                onContextMenu={(e) => handleRightClick(i, e)}
              />
              {/* Glow */}
              {(isHovered || isDragged) && (
                <circle cx={cx} cy={cy} r={10} fill="var(--accent)" opacity={0.15} />
              )}
              {/* Point */}
              <circle
                cx={cx} cy={cy}
                r={isDragged ? 7 : isHovered ? 6 : 5}
                fill="var(--card-bg)"
                stroke="var(--accent)"
                strokeWidth={2.5}
                className="transition-all duration-150"
              />
              {/* Label */}
              {(isHovered || isDragged) && (
                <text x={cx} y={cy - 14} fill="var(--text)" fontSize={11}
                  textAnchor="middle" fontWeight={600} fontFamily="JetBrains Mono, monospace">
                  {p.temp}° → {p.speed}%
                </text>
              )}
            </g>
          );
        })}
      </svg>
    </div>
  );
}
