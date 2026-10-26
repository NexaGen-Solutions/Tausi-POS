import { setClientVersion } from '../connection/serverState';

/**
 * Which terminal this is. In the Tausi Terminal app (Tauri) the server address and device
 * token live in the app's store; in a plain browser during development they live in localStorage.
 */
export interface DeviceConfig {
  serverUrl?: string | null;
  deviceToken?: string | null;
  /** The address the installer was given (TausiPOS-Setup.exe /S SERVER=...), to pre-fill the first screen. */
  suggestedServerUrl?: string | null;
  /** Kiosk mode: full screen, no frame, can't be closed. On unless switched off on this PC. */
  kiosk?: boolean;
}

const storageKey = 'tausi.device';
/** The key used before the rename to Tausi POS; read once so a paired browser stays paired. */
export const oldStorageKey = 'nexagen.device';
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
    try {
      const { getVersion } = await import('@tauri-apps/api/app');
      setClientVersion(await getVersion());
    } catch {
      // Keep the web build's version.
    }
  } else {
    try {
      const old = localStorage.getItem(oldStorageKey);
      if (old !== null && localStorage.getItem(storageKey) === null)
        localStorage.setItem(storageKey, old);
      localStorage.removeItem(oldStorageKey);
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

export function suggestedServerUrl(): string | null {
  return current.suggestedServerUrl ?? null;
}

export function kioskMode(): boolean {
  return isTauri() && current.kiosk !== false;
}

/** In the terminal app: is there a newer terminal version on the server (never newer than the server)? Starts downloading it. */
export async function checkForTerminalUpdate(serverVersion: string): Promise<string | null> {
  return invoke<string | null>('update_check', { serverVersion });
}

/** In the terminal app: installs the downloaded update and restarts. */
export async function installTerminalUpdate(): Promise<void> {
  await invoke('update_install');
}

/** In the terminal app: goes to another server address, keeping the pairing (moving to a new server PC). */
export async function moveServer(serverUrl: string): Promise<void> {
  await invoke('move_server', { serverUrl });
}

/** In the terminal app: leaves (after an admin-site login), optionally switching kiosk mode off on this PC. */
export async function exitKiosk(turnOff: boolean): Promise<void> {
  if (turnOff) await invoke('kiosk_set', { enabled: false });
  await invoke('kiosk_exit');
}

/** In the terminal app: leave the bundled setup screen for the server's POS screens. */
export async function openServer(): Promise<void> {
  await invoke('open_server');
}

/** In the terminal app: print raw ESC/POS bytes on a printer plugged into this PC. */
export async function printRaw(printer: string, dataBase64: string): Promise<void> {
  await invoke('print_raw', { printer, dataBase64 });
}

/** In the terminal app: the printers installed on this PC. */
export async function listPrinters(): Promise<string[]> {
  return invoke<string[]>('list_printers');
}

/** In the terminal app: is there a printer by this name on this PC? Rejects with the reason. */
export async function checkPrinter(printer: string): Promise<void> {
  await invoke('check_printer', { printer });
}

/** In the terminal app: change the server address (drops pairing; the app restarts). */
export async function setServerUrl(serverUrl: string): Promise<void> {
  await invoke('set_server_url', { serverUrl });
}

/** For tests. */
export function setDeviceForTests(config: DeviceConfig): void {
  current = config;
}
