/**
 * Temperature unit conversion utilities.
 *
 * All backend data is stored and transmitted in Celsius.
 * These helpers convert for display when the user selects Fahrenheit.
 */

export type TempUnit = 'C' | 'F';

/** Convert Celsius to Fahrenheit. */
export function cToF(c: number): number {
  return c * 9 / 5 + 32;
}

/** Convert Fahrenheit to Celsius. */
export function fToC(f: number): number {
  return (f - 32) * 5 / 9;
}

/** Display a temperature value in the active unit, rounded to nearest integer. */
export function displayTemp(celsius: number, unit: TempUnit): number {
  return unit === 'F' ? Math.round(cToF(celsius)) : Math.round(celsius);
}

/** Display a temperature value with unit suffix. */
export function formatTemp(celsius: number, unit: TempUnit): string {
  return `${displayTemp(celsius, unit)}°${unit}`;
}

/** Unit symbol string. */
export function tempUnitSymbol(unit: TempUnit): string {
  return `°${unit}`;
}
