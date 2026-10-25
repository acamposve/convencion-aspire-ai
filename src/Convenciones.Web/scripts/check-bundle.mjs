import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Markers that must never reach a browser bundle.
 *
 * `service_role` is the Supabase key that bypasses RLS. The base64 forms cover the same value inside
 * a JWT payload, where the plain string would not appear literally. Database URLs and the modern
 * `sb_secret_` prefix are covered for the same reason.
 */
export const FORBIDDEN_MARKERS = [
  'service_role',
  'SUPABASE_SERVICE_ROLE_KEY',
  'SUPABASE_SERVICE',
  'postgres://',
  'postgresql://',
  'c2VydmljZV9yb2xl', // base64("service_role")
  'InJvbGUiOiJzZXJ2aWNlX3JvbGUi', // base64 fragment of {"role":"service_role"}
];

/**
 * A real modern secret key has a long random suffix. The Supabase client itself contains the
 * literal prefix `sb_secret_` in a validation helper, so matching the prefix alone would fail every
 * build for a library string that is not a secret.
 */
const SECRET_KEY_PATTERN = /sb_secret_[A-Za-z0-9_-]{20,}/;

/** Returns every forbidden marker present in a string. */
export function scanText(text) {
  const found = FORBIDDEN_MARKERS.filter((marker) => text.includes(marker));

  if (SECRET_KEY_PATTERN.test(text)) {
    found.push('sb_secret_ key');
  }

  return found;
}

/** Scans the built JavaScript, CSS and HTML. Returns `{ file, marker }` for every violation. */
export function scanBundle(distDirectory) {
  const files = [];

  const walk = (directory) => {
    for (const entry of readdirSync(directory)) {
      const full = join(directory, entry);

      if (statSync(full).isDirectory()) {
        walk(full);
      } else if (/\.(js|mjs|css|html)$/.test(entry)) {
        files.push(full);
      }
    }
  };

  walk(distDirectory);

  const violations = [];

  for (const file of files) {
    for (const marker of scanText(readFileSync(file, 'utf8'))) {
      violations.push({ file, marker });
    }
  }

  return violations;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const dist = join(process.cwd(), 'dist');
  const violations = scanBundle(dist);

  if (violations.length > 0) {
    console.error('Bundle scan failed. A forbidden marker reached the browser bundle:');

    for (const violation of violations) {
      console.error(`  ${violation.marker} in ${violation.file}`);
    }

    process.exit(1);
  }

  console.log('Bundle scan passed: no service key, database URL or secret marker found.');
}
