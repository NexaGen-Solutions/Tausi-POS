/**
 * What the server says about itself, outside any one request: its version (X-Tausi-Server-Version
 * on every answer), and whether it is restoring a backup (503 "server_restoring"), can't reach its
 * database for now (503 "server_busy"), or needs this terminal to update before anyone logs in
 * (426 "terminal_update_required").
 */
export type ServerMode = 'normal' | 'restoring' | 'updating' | 'busy';

let mode: ServerMode = 'normal';
let version: string | null = null;
const listeners = new Set<(mode: ServerMode) => void>();

export function serverMode(): ServerMode {
  return mode;
}

export function setServerMode(next: ServerMode): void {
  if (next === mode) return;
  mode = next;
  listeners.forEach((listener) => listener(mode));
}

export function onServerMode(listener: (mode: ServerMode) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function serverVersion(): string | null {
  return version;
}

export function setServerVersion(next: string | null): void {
  if (next) version = next;
}

/** This app's version, sent as X-Tausi-Version: the terminal app's own inside it, else the web build's. */
let appVersion: string = typeof __APP_VERSION__ === 'string' ? __APP_VERSION__ : '0.0.0';

export function clientVersion(): string {
  return appVersion;
}

export function setClientVersion(next: string): void {
  appVersion = next;
}

/** Negative when a is older than b (MAJOR.MINOR.PATCH; anything after - or + is ignored). */
export function compareVersions(a: string, b: string): number {
  const parts = (v: string) =>
    v
      .replace(/^v/, '')
      .split(/[-+]/)[0]
      .split('.')
      .map((n) => Number.parseInt(n, 10) || 0);
  const [x, y] = [parts(a), parts(b)];
  for (let i = 0; i < 3; i++) {
    const d = (x[i] ?? 0) - (y[i] ?? 0);
    if (d !== 0) return d;
  }
  return 0;
}
