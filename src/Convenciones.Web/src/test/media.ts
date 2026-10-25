import { vi } from 'vitest';

/**
 * Installs a controllable `matchMedia`.
 *
 * `matches` applies to every query, which is enough for the two uses here: MUI's breakpoint
 * queries and the reduced-motion query. A test sets `true` to simulate a 360 px viewport.
 */
export function installMatchMedia(matches: boolean): void {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(() => false),
  })) as unknown as typeof window.matchMedia;
}
