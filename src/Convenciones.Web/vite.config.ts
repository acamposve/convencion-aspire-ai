import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The API base URL is configured at build time through VITE_API_BASE_URL. No secret is read here:
// only VITE_-prefixed values reach the browser bundle, and the bundle is scanned after `build`.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
  },
  preview: {
    port: 4173,
    strictPort: true,
  },
});
