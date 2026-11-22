import { convertToParamMap } from '@angular/router';
import {
  formatYearMonthParam,
  parseBooleanFlagParam,
  parseEnumParam,
  parseIsoDateParam,
  parseOneBasedIndexSetParam,
  parsePositiveFloatParam,
  parsePositiveIntParam,
  parsePositiveIntSetParam,
  parseStringListParam,
  parseYearMonthParam,
  queryParamsMatch,
  serializePositiveIntsParam,
  serializeStringListParam,
  serializeZeroBasedIndicesParam,
} from './query-param-state';

describe('query-param-state', () => {
  it('parses positive integers and floats strictly', () => {
    expect(parsePositiveIntParam('3')).toBe(3);
    expect(parsePositiveIntParam('0')).toBeNull();
    expect(parsePositiveIntParam('-2')).toBeNull();
    expect(parsePositiveIntParam('3.5')).toBeNull();
    expect(parsePositiveIntParam('abc')).toBeNull();
    expect(parsePositiveIntParam(null)).toBeNull();

    expect(parsePositiveFloatParam('4.5')).toBe(4.5);
    expect(parsePositiveFloatParam('6')).toBe(6);
    expect(parsePositiveFloatParam('0')).toBeNull();
    expect(parsePositiveFloatParam('-1.5')).toBeNull();
    expect(parsePositiveFloatParam('nope')).toBeNull();
  });

  it('parses boolean flags and enums with fallback', () => {
    expect(parseBooleanFlagParam('1')).toBe(true);
    expect(parseBooleanFlagParam('true')).toBe(true);
    expect(parseBooleanFlagParam('TRUE')).toBe(true);
    expect(parseBooleanFlagParam('0')).toBe(false);
    expect(parseBooleanFlagParam(null)).toBe(false);

    expect(
      parseEnumParam('Metric', ['Original', 'UsCustomary', 'Metric'] as const, 'Original'),
    ).toBe('Metric');
    expect(
      parseEnumParam('Bogus', ['Original', 'UsCustomary', 'Metric'] as const, 'Original'),
    ).toBe('Original');
  });

  it('parses and serializes comma-separated lists and 1-based index sets', () => {
    expect(parseStringListParam(' a , b , a , ')).toEqual(['a', 'b']);
    expect(serializeStringListParam(['a', 'b', 'a'])).toBe('a,b');
    expect(serializeStringListParam([])).toBeNull();

    expect(Array.from(parseOneBasedIndexSetParam('1,3,99,0,-1,abc,3', 5))).toEqual([0, 2]);
    expect(serializeZeroBasedIndicesParam([2, 0, 2, -1])).toBe('1,3');
    expect(serializeZeroBasedIndicesParam([])).toBeNull();

    expect(Array.from(parsePositiveIntSetParam('2,1,2,0,bad'))).toEqual([2, 1]);
    expect(serializePositiveIntsParam([3, 1, 3, 0])).toBe('1,3');
    expect(serializePositiveIntsParam([])).toBeNull();
  });

  it('validates ISO dates and YYYY-MM year-months', () => {
    expect(parseIsoDateParam('2026-11-21')).toBe('2026-11-21');
    expect(parseIsoDateParam('2026-02-30')).toBeNull();
    expect(parseIsoDateParam('not-a-date')).toBeNull();

    expect(parseYearMonthParam('2026-11')).toEqual({ year: 2026, monthIndex0: 10 });
    expect(parseYearMonthParam('2026-13')).toBeNull();
    expect(formatYearMonthParam(2026, 0)).toBe('2026-01');
    expect(formatYearMonthParam(2026, 10)).toBe('2026-11');
  });

  it('checks whether current ParamMap matches expected values', () => {
    const map = convertToParamMap({ q: 'chili', page: '2' });
    expect(queryParamsMatch(map, { q: 'chili', page: 2, status: null })).toBe(true);
    expect(queryParamsMatch(map, { q: 'chili', page: 3 })).toBe(false);
    expect(queryParamsMatch(map, { q: null, page: 2 })).toBe(false);
  });
});
