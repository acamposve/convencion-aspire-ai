import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    restoreMocks: true,
    include: ['src/**/*.test.{ts,tsx}', 'scripts/**/*.test.ts'],
    // The suite renders the full application per file. Eight workers on this machine make the
    // heavier screens contend for the event loop, which shows up as a slow wait rather than a
    // failure; four keeps the run parallel without turning timing into a coin flip.
    maxWorkers: 4,
  },
});
