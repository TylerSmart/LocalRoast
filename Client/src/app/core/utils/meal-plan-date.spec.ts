import { describe, expect, it } from 'vitest';
import {
  addDaysIso,
  formatDayHeader,
  formatMonthYearLabel,
  formatWeekRangeLabel,
  getMonthGrid,
  normalizeToSundayIso,
  parseIsoDateLocal,
} from './meal-plan-date';

describe('meal-plan-date utils', () => {
  it('normalizes dates to Sunday accurately', () => {
    // 2026-11-01 is Sunday
    expect(normalizeToSundayIso('2026-11-01')).toBe('2026-11-01');
    // 2026-11-04 is Wednesday
    expect(normalizeToSundayIso('2026-11-04')).toBe('2026-11-01');
    // 2026-11-07 is Saturday
    expect(normalizeToSundayIso('2026-11-07')).toBe('2026-11-01');
    // 2026-03-04 is Wednesday -> previous Sunday was 2026-03-01
    expect(normalizeToSundayIso('2026-03-04')).toBe('2026-03-01');
  });

  it('adds days to ISO date', () => {
    expect(addDaysIso('2026-11-01', 3)).toBe('2026-11-04');
    expect(addDaysIso('2026-11-30', 1)).toBe('2026-12-01');
    expect(addDaysIso('2026-11-01', -1)).toBe('2026-10-31');
  });

  it('formats week range label within same month and across month/year', () => {
    const sameMonth = formatWeekRangeLabel('2026-11-01');
    expect(sameMonth).toContain('Nov 1');
    expect(sameMonth).toContain('7');
    expect(sameMonth).toContain('2026');

    const crossMonth = formatWeekRangeLabel('2026-11-29');
    expect(crossMonth).toContain('Nov 29');
    expect(crossMonth).toContain('Dec 5');
    expect(crossMonth).toContain('2026');
  });

  it('formats month year label', () => {
    expect(formatMonthYearLabel(2026, 10)).toBe('November 2026');
  });

  it('formats day header with correct weekday and day number', () => {
    const sunday = formatDayHeader('2026-11-01');
    expect(sunday.dayShort).toBe('Sun');
    expect(sunday.dayLong).toBe('Sunday');
    expect(sunday.dateNum).toBe(1);

    const monday = formatDayHeader('2026-11-02');
    expect(monday.dayShort).toBe('Mon');
    expect(monday.dayLong).toBe('Monday');
    expect(monday.dateNum).toBe(2);

    const saturday = formatDayHeader('2026-11-07');
    expect(saturday.dayShort).toBe('Sat');
    expect(saturday.dayLong).toBe('Saturday');
    expect(saturday.dateNum).toBe(7);
  });

  it('generates Sunday-aligned month grid', () => {
    const grid = getMonthGrid(2026, 10); // November 2026 (Nov 1 is Sunday)
    expect(grid.length % 7).toBe(0); // Full weeks
    expect(grid[0].iso).toBe('2026-11-01'); // Starting Sunday
    expect(grid[grid.length - 1].iso).toBe('2026-12-05'); // Ending Saturday

    const firstNov = grid.find((d) => d.iso === '2026-11-01');
    expect(firstNov?.inCurrentMonth).toBe(true);

    const decDay = grid.find((d) => d.iso === '2026-12-01');
    expect(decDay?.inCurrentMonth).toBe(false);
  });

  it('parses ISO date in local time without UTC offset skew', () => {
    const date = parseIsoDateLocal('2026-11-01');
    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(10);
    expect(date.getDate()).toBe(1);
  });
});
