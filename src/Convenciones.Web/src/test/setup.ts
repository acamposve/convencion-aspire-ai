import '@testing-library/jest-dom/vitest';
import { cleanup, configure } from '@testing-library/react';
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

// The suite runs several files in parallel workers; a 1s query timeout is too tight for the heavier
// screens once the machine is busy, and a flaky wait is not a real defect.
configure({ asyncUtilTimeout: 5000 });

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
