'use client';
import { useState, useCallback, useRef } from 'react';

interface DragHandleProps {
  onPointerDown: (e: React.PointerEvent) => void;
  onKeyDown: (e: React.KeyboardEvent) => void;
  style: React.CSSProperties;
  'aria-label': string;
  'aria-grabbed'?: boolean;
  role: string;
  tabIndex: number;
}

interface ItemProps {
  style: React.CSSProperties;
  'data-drag-index': number;
}

export function useDragReorder<T>(
  items: T[],
  onReorder: (newItems: T[]) => void,
): {
  dragHandleProps: (index: number) => DragHandleProps;
  itemProps: (index: number) => ItemProps;
  dragIndex: number | null;
  dropIndex: number | null;
} {
  const [dragIndex, setDragIndex] = useState<number | null>(null);
  const [dropIndex, setDropIndex] = useState<number | null>(null);
  const containerRef = useRef<HTMLElement | null>(null);
  const startYRef = useRef(0);
  const itemRectsRef = useRef<DOMRect[]>([]);

  const commitReorder = useCallback((from: number, to: number) => {
    if (from === to) { setDragIndex(null); setDropIndex(null); return; }
    const next = [...items];
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    onReorder(next);
    setDragIndex(null);
    setDropIndex(null);
  }, [items, onReorder]);

  const handlePointerDown = useCallback((index: number, e: React.PointerEvent) => {
    const target = e.currentTarget as HTMLElement;
    const container = target.closest('[data-drag-container]') as HTMLElement;
    if (!container) return;
    containerRef.current = container;
    startYRef.current = e.clientY;

    // Snapshot item positions
    const children = container.querySelectorAll<HTMLElement>('[data-drag-index]');
    itemRectsRef.current = Array.from(children).map(el => el.getBoundingClientRect());

    setDragIndex(index);
    setDropIndex(index);
    target.setPointerCapture(e.pointerId);

    const onMove = (ev: PointerEvent) => {
      const y = ev.clientY;
      let closest = index;
      let minDist = Infinity;
      itemRectsRef.current.forEach((rect, i) => {
        const mid = rect.top + rect.height / 2;
        const dist = Math.abs(y - mid);
        if (dist < minDist) { minDist = dist; closest = i; }
      });
      setDropIndex(closest);
    };

    const onUp = () => {
      setDropIndex(prev => {
        commitReorder(index, prev ?? index);
        return null;
      });
      target.removeEventListener('pointermove', onMove);
      target.removeEventListener('pointerup', onUp);
    };

    target.addEventListener('pointermove', onMove);
    target.addEventListener('pointerup', onUp);
  }, [commitReorder]);

  const handleKeyDown = useCallback((index: number, e: React.KeyboardEvent) => {
    if (dragIndex === null && e.key === ' ') {
      e.preventDefault();
      setDragIndex(index);
      setDropIndex(index);
    } else if (dragIndex !== null) {
      e.preventDefault();
      if (e.key === 'ArrowUp' && dropIndex !== null && dropIndex > 0) {
        setDropIndex(dropIndex - 1);
      } else if (e.key === 'ArrowDown' && dropIndex !== null && dropIndex < items.length - 1) {
        setDropIndex(dropIndex + 1);
      } else if (e.key === 'Enter') {
        commitReorder(dragIndex, dropIndex ?? dragIndex);
      } else if (e.key === 'Escape') {
        setDragIndex(null);
        setDropIndex(null);
      }
    }
  }, [dragIndex, dropIndex, items.length, commitReorder]);

  const dragHandleProps = useCallback((index: number): DragHandleProps => ({
    onPointerDown: (e: React.PointerEvent) => handlePointerDown(index, e),
    onKeyDown: (e: React.KeyboardEvent) => handleKeyDown(index, e),
    style: { cursor: dragIndex === index ? 'grabbing' : 'grab', touchAction: 'none' },
    'aria-label': `Drag to reorder widget ${index + 1} of ${items.length}`,
    'aria-grabbed': dragIndex === index ? true : undefined,
    role: 'button',
    tabIndex: 0,
  }), [handlePointerDown, handleKeyDown, dragIndex, items.length]);

  const itemProps = useCallback((index: number): ItemProps => ({
    style: {
      transition: dragIndex !== null ? 'transform 0.15s ease' : undefined,
      transform: dragIndex !== null && dropIndex !== null && index !== dragIndex
        ? index >= Math.min(dragIndex, dropIndex) && index <= Math.max(dragIndex, dropIndex)
          ? `translateY(${dragIndex < dropIndex ? '-40px' : '40px'})`
          : undefined
        : undefined,
      opacity: dragIndex === index ? 0.6 : 1,
      boxShadow: dragIndex === index ? '0 4px 12px rgba(108,99,255,0.3)' : undefined,
      border: dragIndex === index ? '1px solid var(--accent)' : undefined,
      borderRadius: dragIndex === index ? '6px' : undefined,
    },
    'data-drag-index': index,
  }), [dragIndex, dropIndex]);

  return { dragHandleProps, itemProps, dragIndex, dropIndex };
}
