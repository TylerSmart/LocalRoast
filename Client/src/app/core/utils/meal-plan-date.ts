export const DAY_NAMES_SHORT = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
export const DAY_NAMES_LONG = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

export function parseIsoDateLocal(iso: string): Date {
  const [year, month, day] = iso.split('-').map(Number);
  return new Date(year, month - 1, day);
}

export function formatIsoDateLocal(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

export function coerceToDateLocal(value: Date | string | null | undefined): Date {
  if (value instanceof Date && !isNaN(value.getTime())) {
    return value;
  }
  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}/.test(value)) {
    return parseIsoDateLocal(value.substring(0, 10));
  }
  return new Date();
}

export function coerceToIsoDateLocal(value: Date | string | null | undefined): string {
  if (!value) {
    return getTodayIsoLocal();
  }
  if (value instanceof Date && !isNaN(value.getTime())) {
    return formatIsoDateLocal(value);
  }
  if (typeof value === 'string') {
    if (/^\d{4}-\d{2}-\d{2}/.test(value)) {
      return value.substring(0, 10);
    }
    const parsed = new Date(value);
    if (!isNaN(parsed.getTime())) {
      return formatIsoDateLocal(parsed);
    }
  }
  return getTodayIsoLocal();
}

export function getTodayIsoLocal(): string {
  return formatIsoDateLocal(new Date());
}

export function shouldUseTouchDatepicker(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }
  const hasCoarsePointer =
    typeof window.matchMedia === 'function' &&
    window.matchMedia('(pointer: coarse), (hover: none)').matches;
  const hasTouchPoints = typeof navigator !== 'undefined' && (navigator.maxTouchPoints ?? 0) > 0;
  const isSmallViewport = window.innerWidth <= 900;
  return hasCoarsePointer || hasTouchPoints || isSmallViewport;
}

export function normalizeToMondayIso(isoDate: string): string {
  const date = parseIsoDateLocal(isoDate);
  const day = date.getDay(); // 0 = Sun, 1 = Mon ... 6 = Sat
  const diff = (day - 1 + 7) % 7;
  date.setDate(date.getDate() - diff);
  return formatIsoDateLocal(date);
}

export function addDaysIso(isoDate: string, days: number): string {
  const date = parseIsoDateLocal(isoDate);
  date.setDate(date.getDate() + days);
  return formatIsoDateLocal(date);
}

export function formatWeekRangeLabel(mondayIso: string): string {
  const mon = parseIsoDateLocal(mondayIso);
  const sun = parseIsoDateLocal(addDaysIso(mondayIso, 6));

  const monMonth = mon.toLocaleDateString(undefined, { month: 'short' });
  const sunMonth = sun.toLocaleDateString(undefined, { month: 'short' });

  if (mon.getFullYear() !== sun.getFullYear()) {
    return `${monMonth} ${mon.getDate()}, ${mon.getFullYear()} – ${sunMonth} ${sun.getDate()}, ${sun.getFullYear()}`;
  }

  if (monMonth === sunMonth) {
    return `${monMonth} ${mon.getDate()} – ${sun.getDate()}, ${mon.getFullYear()}`;
  }

  return `${monMonth} ${mon.getDate()} – ${sunMonth} ${sun.getDate()}, ${mon.getFullYear()}`;
}

export function formatMonthYearLabel(year: number, monthIndex0: number): string {
  const date = new Date(year, monthIndex0, 1);
  return date.toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
}

export function formatDayHeader(isoDate: string): {
  dayShort: string;
  dayLong: string;
  dateNum: number;
  isToday: boolean;
  formattedDate: string;
} {
  const date = parseIsoDateLocal(isoDate);
  const jsDay = date.getDay();
  const offset = (jsDay - 1 + 7) % 7; // 0 = Mon, 6 = Sun
  const today = getTodayIsoLocal();

  return {
    dayShort: DAY_NAMES_SHORT[offset],
    dayLong: DAY_NAMES_LONG[offset],
    dateNum: date.getDate(),
    isToday: isoDate === today,
    formattedDate: date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }),
  };
}

export interface MonthGridDay {
  iso: string;
  inCurrentMonth: boolean;
  isToday: boolean;
  dateNum: number;
}

export function getMonthGrid(year: number, monthIndex0: number): MonthGridDay[] {
  const firstOfMonth = new Date(year, monthIndex0, 1);
  const lastOfMonth = new Date(year, monthIndex0 + 1, 0);

  const startIso = normalizeToMondayIso(formatIsoDateLocal(firstOfMonth));
  const endIso = addDaysIso(normalizeToMondayIso(formatIsoDateLocal(lastOfMonth)), 6);

  const todayIso = getTodayIsoLocal();
  const days: MonthGridDay[] = [];

  let currIso = startIso;
  while (currIso <= endIso) {
    const d = parseIsoDateLocal(currIso);
    days.push({
      iso: currIso,
      inCurrentMonth: d.getMonth() === monthIndex0,
      isToday: currIso === todayIso,
      dateNum: d.getDate(),
    });
    currIso = addDaysIso(currIso, 1);
  }

  return days;
}
