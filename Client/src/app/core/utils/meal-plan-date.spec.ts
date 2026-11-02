import { describe, expect, it } from 'vitest';
import {
  addDaysIso,
  formatDayHeader,
  formatMonthYearLabel,
  formatWeekRangeLabel,
  getMonthGrid,
  normalizeToMondayIso,
  parseIsoDateLocal,
} from './meal-plan-date';

describe('meal-plan-date utils', () => {
  it('normalizes dates to Monday accurately', () => {
    // 2026-11-02 is Monday
    expect(normalizeToMondayIso('2026-11-02')).toBe('2026-11-02');
    // 2026-11-04 is Wednesday
    expect(normalizeToMondayIso('2026-11-04')).toBe('2026-11-02');
    // 2026-11-08 is Sunday
    expect(normalizeToMondayIso('2026-11-08')).toBe('2026-11-02');
    // 2026-03-01 is Sunday
    expect(normalizeToMondayIso('2026-03-01')).toBe('2026-02-23');
  });

  it('adds days to ISO date', () => {
    expect(addDaysIso('2026-11-02', 3)).toBe('2026-11-05');
    expect(addDaysIso('2026-11-30', 1)).toBe('2026-12-01');
    expect(addDaysIso('2026-11-02', -1)).toBe('2026-11-01');
  });

  it('formats week range label within same month and across month/year', () => {
    const sameMonth = formatWeekRangeLabel('2026-11-02');
    expect(sameMonth).toContain('Nov 2');
    expect(sameMonth).toContain('8');
    expect(sameMonth).toContain('2026');

    const crossMonth = formatWeekRangeLabel('2026-11-30');
    expect(crossMonth).toContain('Nov 30');
    expect(crossMonth).toContain('Dec 6');
    expect(crossMonth).toContain('2026');
  });

  it('formats month year label', () => {
    expect(formatMonthYearLabel(2026, 10)).toBe('November 2026');
  });

  it('formats day header with correct weekday and day number', () => {
    const monday = formatDayHeader('2026-11-02');
    expect(monday.dayShort).toBe('Mon');
    expect(monday.dayLong).toBe('Monday');
    expect(monday.dateNum).toBe(2);

    const sunday = formatDayHeader('2026-11-08');
    expect(sunday.dayShort).toBe('Sun');
    expect(sunday.dayLong).toBe('Sunday');
    expect(sunday.dateNum).toBe(8);
  });

  it('generates Monday-aligned month grid', () => {
    const grid = getMonthGrid(2026, 10); // November 2026
    expect(grid.length % 7).toBe(0); // Full weeks
    expect(grid[0].iso).toBe('2026-10-26'); // Preceding Monday
    expect(grid[grid.length - 1].iso).toBe('2026-12-06'); // Ending Sunday

    const firstNov = grid.find((d) => d.iso === '2026-11-01');
    expect(firstNov?.inCurrentMonth).toBe(true);

    const octDay = grid.find((d) => d.iso === '2026-10-31');
    expect(octDay?.inCurrentMonth).toBe(false);
  });

  it('parses ISO date in local time without UTC offset skew', () => {
    const date = parseIsoDateLocal('2026-11-02');
    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(10);
    expect(date.getDate()).toBe(2);
  });
});
