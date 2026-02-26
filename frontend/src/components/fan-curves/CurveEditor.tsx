'use client';

import { useState, useRef, useCallback, useEffect } from 'react';
import { useSettingsStore } from '@/stores/settingsStore';
import { displayTemp, tempUnitSymbol } from '@/lib/tempUnit';
import type { FanCurvePoint } from '@/lib/types';

interface CurveEditorProps {
  points: FanCurvePoint[];
  onChange: (points: FanCurvePoint[]) => void;
  width?: number;
  height?: number;
  /** Current temperature (°C) for the "you are here" operating point. */
  currentTemp?: number;
  /** Current fan speed (%) for the "you are here" operating point. */
  currentSpeed?: number;
}

const PADDING = 40;
const GRID_LINES_X = 10;
const GRID_LINES_Y = 10;

export function CurveEditor({
  points, onChange, width = 500, height = 300,
  currentTemp, currentSpeed,
}: CurveEditorProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [dragging, setDragging] = useState<number | null>(null);
  const [hoveredPoint, setHoveredPoint] = useState<number | null>(null);
  const [selectedPoint, setSelectedPoint] = useState<number | null>(null);
  const tempUnit = useSettingsStore((s) => s.tempUnit);

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
    setSelectedPoint(index);
  }, []);

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (dragging === null) return;

    const { x, y } = getSVGPoint(e);
    const temp = Math.round(xToTemp(x));
    const speed = Math.round(yToSpeed(y));

    const newPoints = [...points];
    newPoints[dragging] = { temp, speed };
    const sorted = newPoints.sort((a, b) => a.temp - b.temp);
    // Track the dragged point's new index after sort
    const newIdx = sorted.findIndex((p) => p.temp === temp && p.speed === speed);
    setDragging(newIdx >= 0 ? newIdx : dragging);
    setSelectedPoint(newIdx >= 0 ? newIdx : dragging);
    onChange(sorted);
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
      const newIdx = newPoints.findIndex((p) => p.temp === temp && p.speed === speed);
      setSelectedPoint(newIdx >= 0 ? newIdx : null);
      onChange(newPoints);
    }
  }, [points, onChange, getSVGPoint, xToTemp, yToSpeed]);

  const handleRightClick = useCallback((index: number, e: React.MouseEvent) => {
    e.preventDefault();
    if (points.length > 2) {
      const newPoints = points.filter((_, i) => i !== index);
      onChange(newPoints);
      setSelectedPoint(null);
    }
  }, [points, onChange]);

  // Click on SVG background deselects
  const handleSvgClick = useCallback((e: React.MouseEvent) => {
    if (e.target === svgRef.current) {
      setSelectedPoint(null);
    }
  }, []);

  // Keyboard shortcuts: arrow keys nudge, Delete removes.
  // Scoped to the SVG element (via onKeyDown) so it doesn't hijack text inputs.
  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (selectedPoint === null) return;
    const sorted = [...points].sort((a, b) => a.temp - b.temp);
    if (selectedPoint >= sorted.length) return;

    const step = e.shiftKey ? 5 : 1;
    const p = sorted[selectedPoint];

    switch (e.key) {
      case 'ArrowRight': {
        e.preventDefault();
        const newTemp = Math.min(110, p.temp + step);
        const newPoints = sorted.map((pt, i) => i === selectedPoint ? { ...pt, temp: newTemp } : pt);
        const re = [...newPoints].sort((a, b) => a.temp - b.temp);
        const ni = re.findIndex((pt) => pt.temp === newTemp && pt.speed === p.speed);
        setSelectedPoint(ni >= 0 ? ni : selectedPoint);
        onChange(re);
        break;
      }
      case 'ArrowLeft': {
        e.preventDefault();
        const newTemp = Math.max(0, p.temp - step);
        const newPoints = sorted.map((pt, i) => i === selectedPoint ? { ...pt, temp: newTemp } : pt);
        const re = [...newPoints].sort((a, b) => a.temp - b.temp);
        const ni = re.findIndex((pt) => pt.temp === newTemp && pt.speed === p.speed);
        setSelectedPoint(ni >= 0 ? ni : selectedPoint);
        onChange(re);
        break;
      }
      case 'ArrowUp': {
        e.preventDefault();
        const newSpeed = Math.min(100, p.speed + step);
        const newPoints = sorted.map((pt, i) => i === selectedPoint ? { ...pt, speed: newSpeed } : pt);
        onChange(newPoints);
        break;
      }
      case 'ArrowDown': {
        e.preventDefault();
        const newSpeed = Math.max(0, p.speed - step);
        const newPoints = sorted.map((pt, i) => i === selectedPoint ? { ...pt, speed: newSpeed } : pt);
        onChange(newPoints);
        break;
      }
      case 'Delete':
      case 'Backspace': {
        e.preventDefault();
        if (points.length > 2) {
          const newPoints = sorted.filter((_, i) => i !== selectedPoint);
          onChange(newPoints);
          setSelectedPoint(null);
        }
        break;
      }
    }
  }, [selectedPoint, points, onChange]);

  // Build the line path
  const sortedPoints = [...points].sort((a, b) => a.temp - b.temp);
  const pathD = sortedPoints
    .map((p, i) => `${i === 0 ? 'M' : 'L'} ${tempToX(p.temp)} ${speedToY(p.speed)}`)
    .join(' ');

  // Fill area under curve
  const fillD = sortedPoints.length > 0
    ? `${pathD} L ${tempToX(sortedPoints[sortedPoints.length - 1].temp)} ${speedToY(0)} L ${tempToX(sortedPoints[0].temp)} ${speedToY(0)} Z`
    : '';

  // Operating point ("you are here")
  const hasOpPoint = currentTemp != null && currentSpeed != null;
  const opX = hasOpPoint ? tempToX(currentTemp!) : 0;
  const opY = hasOpPoint ? speedToY(currentSpeed!) : 0;

  return (
    <div className="card p-4 animate-card-enter">
      <div className="flex items-center justify-between mb-3">
        <h3 className="section-title">Curve Editor</h3>
        <p className="text-xs" style={{ color: 'var(--text-secondary)' }}>
          Double-click to add · Right-click to remove · Arrow keys to nudge
        </p>
      </div>

      <svg
        ref={svgRef}
        width={width}
        height={height}
        className="select-none outline-none"
        style={{ cursor: dragging !== null ? 'grabbing' : 'crosshair' }}
        onDoubleClick={handleDoubleClick}
        onClick={handleSvgClick}
        onKeyDown={handleKeyDown}
        tabIndex={0}
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
              {displayTemp(temp, tempUnit)}°
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
          Temperature ({tempUnitSymbol(tempUnit)})
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

        {/* Operating point crosshairs */}
        {hasOpPoint && (
          <>
            <line x1={opX} y1={PADDING} x2={opX} y2={PADDING + chartH}
              stroke="var(--warning)" strokeWidth={1} strokeDasharray="4 3" opacity={0.5} />
            <line x1={PADDING} y1={opY} x2={PADDING + chartW} y2={opY}
              stroke="var(--warning)" strokeWidth={1} strokeDasharray="4 3" opacity={0.5} />
          </>
        )}

        {/* Operating point dot */}
        {hasOpPoint && (
          <>
            <circle cx={opX} cy={opY} r={8} fill="var(--warning)" opacity={0.2}>
              <animate attributeName="r" values="8;12;8" dur="2s" repeatCount="indefinite" />
              <animate attributeName="opacity" values="0.2;0.05;0.2" dur="2s" repeatCount="indefinite" />
            </circle>
            <circle cx={opX} cy={opY} r={4.5}
              fill="var(--warning)" stroke="var(--card-bg)" strokeWidth={2} />
            <text x={opX} y={opY - 12} fill="var(--warning)" fontSize={10}
              textAnchor="middle" fontWeight={600} fontFamily="JetBrains Mono, monospace">
              {displayTemp(currentTemp!, tempUnit)}° / {Math.round(currentSpeed!)}%
            </text>
          </>
        )}

        {/* Points */}
        {sortedPoints.map((p, i) => {
          const cx = tempToX(p.temp);
          const cy = speedToY(p.speed);
          const isHovered = hoveredPoint === i;
          const isDragged = dragging === i;
          const isSelected = selectedPoint === i;

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
              {/* Selection ring */}
              {isSelected && !isDragged && (
                <circle cx={cx} cy={cy} r={10} fill="none"
                  stroke="var(--accent)" strokeWidth={1.5} strokeDasharray="3 2" opacity={0.6} />
              )}
              {/* Glow */}
              {(isHovered || isDragged) && (
                <circle cx={cx} cy={cy} r={10} fill="var(--accent)" opacity={0.15} />
              )}
              {/* Point */}
              <circle
                cx={cx} cy={cy}
                r={isDragged ? 7 : isHovered || isSelected ? 6 : 5}
                fill="var(--card-bg)"
                stroke="var(--accent)"
                strokeWidth={2.5}
                className="transition-all duration-150"
              />
              {/* Label */}
              {(isHovered || isDragged || isSelected) && (
                <text x={cx} y={cy - 14} fill="var(--text)" fontSize={11}
                  textAnchor="middle" fontWeight={600} fontFamily="JetBrains Mono, monospace">
                  {displayTemp(p.temp, tempUnit)}° → {p.speed}%
                </text>
              )}
            </g>
          );
        })}
      </svg>
    </div>
  );
}
