/** Date helpers for the `yyyy-MM-dd` inputs the API expects (`DateOnly`). */

export function todayIso(): string {
  return new Date().toISOString().slice(0, 10);
}

/** A date input with a value; null stays empty rather than becoming "Invalid Date". */
export function toDateInput(value: string | null | undefined): string {
  return value === null || value === undefined ? '' : value.slice(0, 10);
}

export function addYearsIso(iso: string, years: number): string {
  const date = new Date(`${iso}T00:00:00Z`);

  if (Number.isNaN(date.getTime())) {
    return '';
  }

  date.setUTCFullYear(date.getUTCFullYear() + years);
  return date.toISOString().slice(0, 10);
}
