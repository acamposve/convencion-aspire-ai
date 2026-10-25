import { createClient, type SupabaseClient } from '@supabase/supabase-js';
import { AuthError } from './errors';

/**
 * The authentication boundary the application depends on.
 *
 * The real implementation is Supabase Auth. Tests and local development supply an in-memory
 * stand-in, so no test needs a Supabase project and no token is ever written to browser storage.
 */
export interface AuthClient {
  readonly isConfigured: boolean;
  signIn(email: string, password: string): Promise<void>;
  /** Development-only path: accept a token already minted for a real API account. */
  signInWithToken?(token: string): Promise<void>;
  signOut(): Promise<void>;
  getAccessToken(): Promise<string | null>;
}

/**
 * Supabase Auth adapter.
 *
 * `persistSession: false` is deliberate: the access and refresh tokens stay in memory, so there is
 * nothing in `localStorage` for an XSS to read. The cost is that a page reload signs the user out;
 * the security model prefers that to a stored token.
 */
export class SupabaseAuthClient implements AuthClient {
  readonly isConfigured = true;
  private readonly client: SupabaseClient;

  constructor(url: string, anonKey: string, client?: SupabaseClient) {
    this.client =
      client ??
      createClient(url, anonKey, {
        auth: {
          persistSession: false,
          autoRefreshToken: true,
          detectSessionInUrl: false,
        },
      });
  }

  async signIn(email: string, password: string): Promise<void> {
    const { error } = await this.client.auth.signInWithPassword({ email, password });

    if (error) {
      // Supabase provides a stable code (invalid_credentials, email_not_confirmed, …). The UI
      // displays the code, not the raw message.
      throw new AuthError(error.code ?? 'invalid_credentials', 'No se pudo iniciar sesión.');
    }
  }

  async signOut(): Promise<void> {
    await this.client.auth.signOut();
  }

  async getAccessToken(): Promise<string | null> {
    const { data, error } = await this.client.auth.getSession();

    return error ? null : (data.session?.access_token ?? null);
  }
}

/**
 * Used when no Supabase project is configured (the state of this repository until T18).
 *
 * It fails closed: email/password sign-in is refused with `auth_not_configured`. The development
 * token path exists so the SPA can be exercised against a real API with a token minted elsewhere;
 * that token still has to pass the API's signature, issuer, audience and account checks.
 */
export class UnconfiguredAuthClient implements AuthClient {
  readonly isConfigured = false;
  private token: string | null = null;

  async signIn(): Promise<void> {
    throw new AuthError(
      'auth_not_configured',
      'La autenticación no está configurada. Defina VITE_SUPABASE_URL y VITE_SUPABASE_ANON_KEY.',
    );
  }

  async signInWithToken(token: string): Promise<void> {
    this.token = token.trim() === '' ? null : token.trim();

    if (this.token === null) {
      throw new AuthError('validation_failed', 'El token no puede estar vacío.');
    }
  }

  async signOut(): Promise<void> {
    this.token = null;
  }

  async getAccessToken(): Promise<string | null> {
    return this.token;
  }
}

export function createAuthClient(env: ImportMetaEnv = import.meta.env): AuthClient {
  const url = env.VITE_SUPABASE_URL?.trim();
  const anonKey = env.VITE_SUPABASE_ANON_KEY?.trim();

  if (url && anonKey) {
    return new SupabaseAuthClient(url, anonKey);
  }

  return new UnconfiguredAuthClient();
}
