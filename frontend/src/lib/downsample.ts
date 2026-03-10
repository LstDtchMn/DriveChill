/**
 * Largest-Triangle-Three-Buckets (LTTB) downsampling algorithm.
 * Reduces a dataset to at most `threshold` points while preserving visual shape.
 *
 * Reference: Sveinn Steinarsson, "Downsampling Time Series for Visual Representation" (2013)
 */
export interface DataPoint {
  x: number; // epoch ms or any numeric x-axis value
  y: number;
}

export function lttbDownsample(data: DataPoint[], threshold: number): DataPoint[] {
  const n = data.length;
  if (threshold >= n || threshold === 0) return data;
  if (threshold < 3) return [data[0], data[n - 1]];

  const sampled: DataPoint[] = [];
  // Always include the first point
  sampled.push(data[0]);

  const bucketSize = (n - 2) / (threshold - 2);
  let a = 0; // index of the previously selected point

  for (let i = 0; i < threshold - 2; i++) {
    // Calculate the range for the current bucket
    const rangeStart = Math.floor((i + 1) * bucketSize) + 1;
    const rangeEnd   = Math.min(Math.floor((i + 2) * bucketSize) + 1, n);

    // Calculate the average x and y of the *next* bucket (look-ahead)
    const nextRangeStart = rangeEnd;
    const nextRangeEnd   = Math.min(Math.floor((i + 3) * bucketSize) + 1, n);

    let avgX = 0;
    let avgY = 0;
    const nextLen = nextRangeEnd - nextRangeStart;
    for (let j = nextRangeStart; j < nextRangeEnd; j++) {
      avgX += data[j].x;
      avgY += data[j].y;
    }
    if (nextLen > 0) {
      avgX /= nextLen;
      avgY /= nextLen;
    }

    // Find the point in the current bucket that forms the largest triangle
    const pointA = data[a];
    let maxArea = -1;
    let nextA = rangeStart;

    for (let j = rangeStart; j < rangeEnd; j++) {
      // Area of triangle formed by pointA, data[j], and the look-ahead avg
      const area = Math.abs(
        (pointA.x - avgX) * (data[j].y - pointA.y) -
        (pointA.x - data[j].x) * (avgY - pointA.y)
      ) / 2;

      if (area > maxArea) {
        maxArea = area;
        nextA = j;
      }
    }

    sampled.push(data[nextA]);
    a = nextA;
  }

  // Always include the last point
  sampled.push(data[n - 1]);
  return sampled;
}
