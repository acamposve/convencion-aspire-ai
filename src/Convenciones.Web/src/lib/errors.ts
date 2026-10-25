/**
 * Errors that cross a boundary are modelled, not stringified.
 *
 * The UI shows a stable code and a safe detail. A raw exception message never reaches the screen,
 * and the API's problem `code` is preserved so a support conversation can start from it.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly detail: string;
  readonly correlationId: string | undefined;

  constructor(
    status: number,
    code: string,
    detail: string,
    correlationId?: string,
  ) {
    super(`${status} ${code}`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.detail = detail;
    this.correlationId = correlationId;
  }
}

export class AuthError extends Error {
  readonly code: string;

  /**
   * The detail is intentionally not surfaced. Identity providers describe failures with messages
   * written for developers, and echoing one is exactly the "raw message" the UI contract forbids.
   */
  constructor(code: string, _providerDetail?: string) {
    super(code);
    this.name = 'AuthError';
    this.code = code;
  }
}

export interface UiError {
  code: string;
  detail: string;
  status?: number;
  correlationId?: string;
}

const AUTH_DETAILS: Record<string, string> = {
  auth_not_configured:
    'La autenticación no está configurada. Defina VITE_SUPABASE_URL y VITE_SUPABASE_ANON_KEY.',
  invalid_credentials: 'El correo o la contraseña no son correctos.',
  validation_failed: 'Los datos de la sesión no son válidos.',
  invalid_token: 'El token no pudo utilizarse. Verifique que sea un token vigente.',
};

export function describeError(error: unknown): UiError {
  if (error instanceof ApiError) {
    return {
      code: error.code,
      detail: error.detail,
      ...(error.status > 0 ? { status: error.status } : {}),
      ...(error.correlationId ? { correlationId: error.correlationId } : {}),
    };
  }

  if (error instanceof AuthError) {
    return {
      code: error.code,
      detail: AUTH_DETAILS[error.code] ?? 'No se pudo iniciar sesión.',
    };
  }

  // Unknown failures are reported as a code, never by echoing an exception message.
  return {
    code: 'unexpected_error',
    detail: 'Ocurrió un error inesperado. Intente de nuevo.',
  };
}
