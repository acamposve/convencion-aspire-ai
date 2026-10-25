import { describe, expect, it } from 'vitest';
import { FORBIDDEN_MARKERS, scanText } from './check-bundle.mjs';

describe('bundle scan', () => {
  it('flags a service-role key and a database URL', () => {
    expect(scanText('const role = "service_role";')).toContain('service_role');
    expect(scanText('const url = "postgresql://user:secret@host/db";')).toContain(
      'postgresql://',
    );
    expect(scanText(`const key = "sb_secret_${'a'.repeat(40)}";`)).toContain('sb_secret_ key');
  });

  it('does not flag the library prefix that is not a key', () => {
    // @supabase/supabase-js contains `startsWith("sb_secret_")` in its own validation helper.
    expect(scanText('const isSecret = (key) => key.startsWith("sb_secret_");')).toEqual([]);
  });

  it('flags the base64 form of a service-role claim', () => {
    expect(scanText('const payload = "c2VydmljZV9yb2xl";')).toContain('c2VydmljZV9yb2xl');
  });

  it('passes a bundle containing only public configuration', () => {
    const bundle = [
      'const api = "https://api.example.test";',
      'const project = "https://abcdef.supabase.co";',
      'const anon = "public-anon-key";',
    ].join('\n');

    expect(scanText(bundle)).toEqual([]);
  });

  it('has no duplicate markers', () => {
    expect(new Set(FORBIDDEN_MARKERS).size).toBe(FORBIDDEN_MARKERS.length);
  });
});
