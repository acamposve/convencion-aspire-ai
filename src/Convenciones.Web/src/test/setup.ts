import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach, beforeEach } from 'vitest';
import { installMatchMedia } from './media';

class ResizeObserverStub {
  observe(): void {
    // The layout observer is not used by the assertions; a stub keeps MUI from crashing under jsdom.
  }

  unobserve(): void {
    // No-op.
  }

  disconnect(): void {
    // No-op.
  }
}

globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
window.scrollTo = () => {
  // jsdom does not implement scrolling; the application only needs the call not to throw.
};

beforeEach(() => {
  installMatchMedia(false);
  window.localStorage.clear();
  window.sessionStorage.clear();
});

afterEach(() => {
  cleanup();
});
