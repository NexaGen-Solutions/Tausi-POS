/**
 * Which terminal this is. In the Nexagen Terminal app (Tauri) the server address and device
 * token live in the app's store; in a plain browser during development they live in localStorage.
 */
export interface DeviceConfig {
  serverUrl?: string | null;
  deviceToken?: string | null;
}

const storageKey = 'nexagen.device';
let current: DeviceConfig = {};

export function isTauri(): boolean {
  return typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window;
}

/** True when this page is the setup screen bundled in the terminal app, not served by the server. */
export function isBundledTerminalPage(): boolean {
  return isTauri() && (location.protocol === 'tauri:' || location.hostname === 'tauri.localhost');
}

async function invoke<T>(command: string, args?: Record<string, unknown>): Promise<T> {
  const { invoke } = await import('@tauri-apps/api/core');
  return invoke<T>(command, args);
}

export async function loadDevice(): Promise<DeviceConfig> {
  if (isTauri()) {
    current = await invoke<DeviceConfig>('get_device_config');
  } else {
    try {
      current = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as DeviceConfig;
    } catch {
      current = {};
    }
  }
  return current;
}

export async function saveDevice(config: DeviceConfig): Promise<void> {
  current = config;
  if (isTauri()) {
    await invoke('save_device_config', { config });
  } else {
    localStorage.setItem(storageKey, JSON.stringify(config));
  }
}

export function deviceToken(): string | null {
  return current.deviceToken ?? null;
}

export function serverUrl(): string | null {
  return current.serverUrl ?? null;
}

/** In the terminal app: leave the bundled setup screen for the server's POS screens. */
export async function openServer(): Promise<void> {
  await invoke('open_server');
}

/** In the terminal app: print raw ESC/POS bytes on a printer plugged into this PC. */
export async function printRaw(printer: string, dataBase64: string): Promise<void> {
  await invoke('print_raw', { printer, dataBase64 });
}

/** For tests. */
export function setDeviceForTests(config: DeviceConfig): void {
  current = config;
}
