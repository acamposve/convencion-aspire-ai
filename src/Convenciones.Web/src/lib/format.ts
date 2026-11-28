const MIB = 1024 * 1024;

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return '—';
  }

  if (bytes < 1024) {
    return `${bytes} B`;
  }

  if (bytes < MIB) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }

  return `${(bytes / MIB).toFixed(1)} MiB`;
}

export function formatDateTime(iso: string): string {
  const date = new Date(iso);

  if (Number.isNaN(date.getTime())) {
    return '—';
  }

  return new Intl.DateTimeFormat('es', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

export function shortId(value: string): string {
  return value.length > 8 ? value.slice(0, 8) : value;
}

/**
 * A calendar date with no timezone at all.
 *
 * `new Date('2023-01-01')` parses as UTC midnight, so in a negative offset it renders as 31 December
 * 2022 and a validity range computes one month less than it should. The API sends `YYYY-MM-DD`, so the
 * components are read from the string and never converted.
 */
interface CalendarDate {
  year: number;
  month: number;
  day: number;
}

function parseCalendarDate(value: string): CalendarDate | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value.trim());

  if (!match) {
    return null;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);

  if (month < 1 || month > 12 || day < 1 || day > 31) {
    return null;
  }

  return { year, month, day };
}

/**
 * Whole months between two calendar dates, counting a month only once the day of month is reached.
 *
 * 2023-01-01 → 2024-01-01 is 12 months (an exact year). 2023-01-01 → 2023-02-01 is 1 month.
 * 2023-01-31 → 2023-02-28 is 0 months: the day of month was never reached.
 * A negative result (validTo before validFrom) is not a duration.
 */
function wholeMonthsBetween(from: CalendarDate, to: CalendarDate): number {
  let months = (to.year - from.year) * 12 + (to.month - from.month);

  if (to.day < from.day) {
    months -= 1;
  }

  return months;
}

function describeMonths(months: number): string {
  if (months < 0) {
    return 'fechas inconsistentes';
  }

  if (months === 0) {
    return 'menos de un mes';
  }

  if (months < 12) {
    return months === 1 ? '1 mes' : `${months} meses`;
  }

  const years = Math.floor(months / 12);
  const remainder = months % 12;

  if (remainder === 0) {
    return years === 1 ? '1 año' : `${years} años`;
  }

  const yearLabel = years === 1 ? '1 año' : `${years} años`;
  return `${yearLabel} y ${remainder === 1 ? '1 mes' : `${remainder} meses`}`;
}

/**
 * The validity of an agreement as one line: `2023-01-01 – 2025-12-31 · 2 años y 11 meses`, or
 * `2023-01-01 – sin fecha fin` when there is no end date (#138a).
 *
 * The duration is a calendar calculation, not a division of milliseconds: a range that crosses a leap
 * year or a month boundary must not drift. An unparseable date degrades to a dash rather than inventing
 * a duration.
 */
export function formatValidity(from: string, to: string | null): string {
  const start = parseCalendarDate(from);

  if (!start) {
    return '—';
  }

  if (to === null || to.trim() === '') {
    return `${from.trim()} – sin fecha fin`;
  }

  const end = parseCalendarDate(to);

  if (!end) {
    return `${from.trim()} – sin fecha fin`;
  }

  return `${from.trim()} – ${to.trim()} · ${describeMonths(wholeMonthsBetween(start, end))}`;
}
