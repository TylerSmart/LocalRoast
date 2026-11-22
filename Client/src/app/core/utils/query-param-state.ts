import { ParamMap } from '@angular/router';

const ISO_DATE_PATTERN = /^\d{4}-\d{2}-\d{2}$/;
const YEAR_MONTH_PATTERN = /^(\d{4})-(\d{2})$/;

export function parsePositiveIntParam(value: string | null | undefined): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (!/^\d+$/.test(trimmed)) return null;
  const parsed = Number.parseInt(trimmed, 10);
  return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

export function parsePositiveFloatParam(value: string | null | undefined): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (!/^\d+(?:\.\d+)?$/.test(trimmed)) return null;
  const parsed = Number.parseFloat(trimmed);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null;
}

export function parseBooleanFlagParam(value: string | null | undefined): boolean {
  if (!value) return false;
  const normalized = value.trim().toLowerCase();
  return normalized === '1' || normalized === 'true';
}

export function parseEnumParam<T extends string>(
  value: string | null | undefined,
  allowed: readonly T[],
  fallback: T,
): T {
  if (!value) return fallback;
  return (allowed as readonly string[]).includes(value) ? (value as T) : fallback;
}

export function parseStringListParam(value: string | null | undefined): string[] {
  if (!value) return [];
  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of value.split(',')) {
    const trimmed = raw.trim();
    if (trimmed && !seen.has(trimmed)) {
      seen.add(trimmed);
      result.push(trimmed);
    }
  }
  return result;
}

export function serializeStringListParam(values: readonly string[]): string | null {
  const cleaned = values.map((v) => v.trim()).filter((v) => v.length > 0);
  if (cleaned.length === 0) return null;
  return Array.from(new Set(cleaned)).join(',');
}

/**
 * Parses a comma-separated list of 1-based positive indices (e.g. "1,3,5") into a Set of 0-based
 * indices bounded by `maxCount`.
 */
export function parseOneBasedIndexSetParam(
  value: string | null | undefined,
  maxCount: number,
): Set<number> {
  const result = new Set<number>();
  if (!value || maxCount <= 0) return result;
  for (const token of value.split(',')) {
    const parsed = parsePositiveIntParam(token);
    if (parsed !== null && parsed <= maxCount) {
      result.add(parsed - 1);
    }
  }
  return result;
}

/**
 * Serializes 0-based indices into a sorted comma-separated list of 1-based numbers (e.g. "1,3,5"),
 * or returns `null` when empty.
 */
export function serializeZeroBasedIndicesParam(indices: Iterable<number>): string | null {
  const sorted = Array.from(
    new Set(Array.from(indices).filter((idx) => Number.isInteger(idx) && idx >= 0)),
  ).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  return sorted.map((idx) => idx + 1).join(',');
}

/**
 * Parses a comma-separated list of 1-based step numbers (e.g. "1,2,4") into a Set of step numbers.
 */
export function parsePositiveIntSetParam(value: string | null | undefined): Set<number> {
  const result = new Set<number>();
  if (!value) return result;
  for (const token of value.split(',')) {
    const parsed = parsePositiveIntParam(token);
    if (parsed !== null) {
      result.add(parsed);
    }
  }
  return result;
}

export function serializePositiveIntsParam(numbers: Iterable<number>): string | null {
  const sorted = Array.from(
    new Set(Array.from(numbers).filter((n) => Number.isInteger(n) && n > 0)),
  ).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  return sorted.join(',');
}

export function parseIsoDateParam(value: string | null | undefined): string | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (!ISO_DATE_PATTERN.test(trimmed)) return null;
  const [y, m, d] = trimmed.split('-').map((n) => Number.parseInt(n, 10));
  const check = new Date(y, m - 1, d);
  if (check.getFullYear() !== y || check.getMonth() !== m - 1 || check.getDate() !== d) {
    return null;
  }
  return trimmed;
}

export function parseYearMonthParam(
  value: string | null | undefined,
): { year: number; monthIndex0: number } | null {
  if (!value) return null;
  const match = YEAR_MONTH_PATTERN.exec(value.trim());
  if (!match) return null;
  const year = Number.parseInt(match[1], 10);
  const month = Number.parseInt(match[2], 10);
  if (year < 1970 || year > 2200 || month < 1 || month > 12) {
    return null;
  }
  return { year, monthIndex0: month - 1 };
}

export function formatYearMonthParam(year: number, monthIndex0: number): string {
  return `${year}-${String(monthIndex0 + 1).padStart(2, '0')}`;
}

/**
 * Checks whether the current route `ParamMap` already matches all key/value entries in `nextParams`
 * (where `null`/`undefined` means the param should be absent).
 */
export function queryParamsMatch(
  current: ParamMap,
  nextParams: Record<string, string | number | null | undefined>,
): boolean {
  for (const [key, rawVal] of Object.entries(nextParams)) {
    const expected = rawVal === null || rawVal === undefined ? null : String(rawVal);
    if (current.get(key) !== expected) {
      return false;
    }
  }
  return true;
}
