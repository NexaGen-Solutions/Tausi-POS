/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The dev server proxies API, hub and file requests to Nexagen Server, so the app always uses
// same-origin URLs, exactly as in production where the server hosts the built files.
const server = process.env.NEXAGEN_SERVER_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': server,
      '/files': server,
      '/hubs': { target: server, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
