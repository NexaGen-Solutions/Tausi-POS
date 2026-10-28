/// <reference types="vitest/config" />
import { readFileSync } from 'node:fs';
import react from '@vitejs/plugin-react';
import { defineConfig, type Plugin } from 'vite';

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

/** The repo's THIRD-PARTY-NOTICES, served as /third-party-notices.txt (the About page links to it). */
function thirdPartyNotices(): Plugin {
  const file = new URL('../THIRD-PARTY-NOTICES', import.meta.url);
  return {
    name: 'tausi-third-party-notices',
    configureServer(dev) {
      dev.middlewares.use('/third-party-notices.txt', (_request, response) => {
        response.setHeader('Content-Type', 'text/plain; charset=utf-8');
        response.end(readFileSync(file));
      });
    },
    generateBundle() {
      this.emitFile({
        type: 'asset',
        fileName: 'third-party-notices.txt',
        source: readFileSync(file),
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), thirdPartyNotices()],
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
    // Stylesheets are skipped in tests, except as text (tokens.test.ts reads them with ?raw).
    css: { include: [/\?raw$/] },
  },
});
