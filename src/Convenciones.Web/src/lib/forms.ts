/** Validation shared by the forms. Each function returns a message or null, never a boolean. */

export function requiredText(value: string, message = 'Este campo es obligatorio.'): string | null {
  return value.trim() === '' ? message : null;
}

export function positiveInteger(value: string): string | null {
  if (!/^\d+$/.test(value.trim())) {
    return 'Escriba un número entero.';
  }

  return Number(value) >= 1 ? null : 'El número debe ser mayor o igual a 1.';
}

export function isoDate(value: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) {
    return 'Indique una fecha (aaaa-mm-dd).';
  }

  return Number.isNaN(new Date(`${value}T00:00:00Z`).getTime()) ? 'La fecha no es válida.' : null;
}

/** `validTo` is optional; when present it cannot precede `validFrom`. */
export function dateOrder(validFrom: string, validTo: string): string | null {
  if (validTo === '') {
    return null;
  }

  return validTo < validFrom ? 'La fecha final no puede ser anterior a la inicial.' : null;
}

export function firstError(errors: Array<string | null>): string | null {
  return errors.find((error) => error !== null) ?? null;
}
