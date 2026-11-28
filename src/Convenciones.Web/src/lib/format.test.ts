import { describe, expect, it } from 'vitest';
import { formatValidity } from './format';

/**
 * The validity line is a calendar calculation (#138a). These tests pin the cases the aggregate screen
 * depends on: an exact year, a single month, a range that crosses a year boundary, an open end, and a
 * `YYYY-MM-DD` string that must not shift a day because of a timezone.
 */
describe('formatValidity', () => {
  it('counts an exact year as one year', () => {
    expect(formatValidity('2023-01-01', '2024-01-01')).toBe('2023-01-01 – 2024-01-01 · 1 año');
  });

  it('counts a single month', () => {
    expect(formatValidity('2023-01-01', '2023-02-01')).toBe('2023-01-01 – 2023-02-01 · 1 mes');
  });

  it('does not count a month before the day of month is reached', () => {
    expect(formatValidity('2023-01-31', '2023-02-28')).toBe('2023-01-31 – 2023-02-28 · menos de un mes');
  });

  it('crosses a year boundary in months', () => {
    expect(formatValidity('2023-11-01', '2024-01-01')).toBe('2023-11-01 – 2024-01-01 · 2 meses');
    expect(formatValidity('2023-11-01', '2025-01-01')).toBe('2023-11-01 – 2025-01-01 · 1 año y 2 meses');
  });

  it('breaks a long range into years and months', () => {
    expect(formatValidity('2023-01-01', '2025-12-01')).toBe('2023-01-01 – 2025-12-01 · 2 años y 11 meses');
  });

  it('reports an open-ended agreement without inventing a duration', () => {
    expect(formatValidity('2023-01-01', null)).toBe('2023-01-01 – sin fecha fin');
    expect(formatValidity('2023-01-01', '')).toBe('2023-01-01 – sin fecha fin');
  });

  it('reads the date from the string, so an ISO time cannot shift the day', () => {
    // `new Date('2023-01-01T00:00:00Z')` is 31 December in a negative offset; the components are read
    // from the string instead, so the day survives.
    expect(formatValidity('2023-01-01T00:00:00Z', '2024-01-01T00:00:00Z')).toBe(
      '2023-01-01T00:00:00Z – 2024-01-01T00:00:00Z · 1 año',
    );
  });

  it('degrades instead of inventing a duration', () => {
    expect(formatValidity('no-es-una-fecha', '2024-01-01')).toBe('—');
    expect(formatValidity('2023-01-01', '2022-01-01')).toBe('2023-01-01 – 2022-01-01 · fechas inconsistentes');
  });
});
