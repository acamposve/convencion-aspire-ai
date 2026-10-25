import { describe, expect, it } from 'vitest';
import {
  SupabaseAuthClient,
  UnconfiguredAuthClient,
  createAuthClient,
} from './auth';

function env(values: Partial<ImportMetaEnv>): ImportMetaEnv {
  return values as ImportMetaEnv;
}

describe('createAuthClient', () => {
  it('fails closed when Supabase is not configured', async () => {
    const auth = createAuthClient(env({}));

    expect(auth).toBeInstanceOf(UnconfiguredAuthClient);
    expect(auth.isConfigured).toBe(false);
    await expect(auth.signIn('ana@example.test', 'clave')).rejects.toMatchObject({
      code: 'auth_not_configured',
    });
  });

  it('uses Supabase when both public values are present', () => {
    const auth = createAuthClient(
      env({
        VITE_SUPABASE_URL: 'https://example.supabase.co',
        VITE_SUPABASE_ANON_KEY: 'public-anon-key',
      }),
    );

    expect(auth).toBeInstanceOf(SupabaseAuthClient);
    expect(auth.isConfigured).toBe(true);
  });

  it('does not use Supabase with only one of the two values', () => {
    const auth = createAuthClient(
      env({ VITE_SUPABASE_URL: 'https://example.supabase.co' }),
    );

    expect(auth).toBeInstanceOf(UnconfiguredAuthClient);
  });
});
