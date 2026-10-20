/// <reference types="vitest/config" />
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

import pkg from './package.json' with { type: 'json' };

// The dev server proxies API, hub and file requests to Tausi Server, so the app always uses
// same-origin URLs, exactly as in production where the server hosts the built files.
// NEXAGEN_SERVER_URL is the name from before the rename to Tausi POS, still accepted.
if (!process.env.TAUSI_SERVER_URL && process.env.NEXAGEN_SERVER_URL)
  console.warn(
    'NEXAGEN_SERVER_URL is an old name from before the rename to Tausi POS. Use TAUSI_SERVER_URL.',
  );
const server =
  process.env.TAUSI_SERVER_URL ?? process.env.NEXAGEN_SERVER_URL ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  define: { __APP_VERSION__: JSON.stringify(pkg.version) },
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
