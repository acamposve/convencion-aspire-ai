import { useMemo } from 'react';
import { useSession } from './SessionProvider';

/**
 * What the caller may do, as the API reported it on `/me`.
 *
 * Capabilities are read from the database on every request, so this set is the same one the API will
 * enforce. The UI uses it to *not show* controls the caller cannot use (T14 AC 1); the API remains the
 * guard — a hidden button is a courtesy, not a permission.
 */
export function useCapabilities() {
  const { me } = useSession();

  return useMemo(() => {
    const granted = new Set(me?.capabilities ?? []);

    return {
      has: (capability: string): boolean => granted.has(capability),
      hasAll: (capabilities: string[]): boolean => capabilities.every((c) => granted.has(c)),
      granted: [...granted].sort(),
    };
  }, [me]);
}
