import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr';
import { apiUrl } from '../api/client';
import { checkPrinter, deviceToken, isTauri, listPrinters, printRaw } from '../device/device';
import { pinSession } from '../session/session';
import { updateConnection } from './status';

/** Live-update events from the server (AGENTS.md). Payloads are small; refetch details via the API. */
export const posEvents = [
  'ticket.changed',
  'ticket.lock',
  'tables.changed',
  'approval.requested',
  'approval.resolved',
  'print.failed',
  'day.closed',
  'printer.status',
  'menu.changed',
  'settings.changed',
  'day.opened',
  'user.deactivated',
  'user.pin_reset',
  'terminal.status',
  'terminal.revoked',
] as const;

export type PosEventName = (typeof posEvents)[number] | 'reconnected';
type Handler = (payload: unknown) => void;

const handlers = new Map<string, Set<Handler>>();
let connection: HubConnection | null = null;
let restartTimer: number | undefined;
let wanted = false;

function emit(name: PosEventName, payload: unknown) {
  handlers.get(name)?.forEach((handler) => handler(payload));
}

export function onPosEvent(name: PosEventName, handler: Handler): () => void {
  if (!handlers.has(name)) handlers.set(name, new Set());
  handlers.get(name)!.add(handler);
  return () => handlers.get(name)?.delete(handler);
}

function build(): HubConnection {
  const device = deviceToken();
  const url = apiUrl('/hubs/pos') + (device ? `?device_token=${encodeURIComponent(device)}` : '');
  const hub = new HubConnectionBuilder()
    .withUrl(url, { accessTokenFactory: () => pinSession()?.token ?? '' })
    // Keep trying for ever: 0 s, 1 s, 2 s, 4 s ... then every 10 s.
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => Math.min(10_000, 500 * 2 ** ctx.previousRetryCount),
    })
    .configureLogging(LogLevel.Warning)
    .build();

  for (const name of posEvents) hub.on(name, (payload: unknown) => emit(name, payload));

  // A "via terminal" printer is plugged into this PC: print here and tell the server how it went.
  hub.on('print.job', async (job: { printer: string; dataBase64: string }) => {
    if (!isTauri()) return { ok: false, error: 'This is not a Tausi Terminal app.' };
    try {
      await printRaw(job.printer, job.dataBase64);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  });

  // The admin asks "Check connection" for a printer plugged into this PC.
  hub.on('printer.check', async (job: { printer: string }) => {
    if (!isTauri()) return { ok: false, error: 'This is not a Tausi Terminal app.' };
    try {
      await checkPrinter(job.printer);
      return { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  });

  hub.onreconnecting(() => updateConnection({ hub: 'reconnecting' }));
  hub.onreconnected(() => {
    updateConnection({ hub: 'connected' });
    emit('reconnected', null);
    void reportPrinters(hub);
  });
  hub.onclose(() => {
    if (wanted) {
      updateConnection({ hub: 'reconnecting' });
      scheduleRestart();
    } else {
      updateConnection({ hub: 'off' });
    }
  });
  return hub;
}

/** Tells the server which printers this PC has, so an admin can pick one for "via terminal". */
async function reportPrinters(hub: HubConnection) {
  if (!isTauri() || !deviceToken()) return;
  try {
    await hub.invoke('ReportPrinters', await listPrinters());
  } catch {
    // Not important enough to bother anyone: the admin can type the name instead.
  }
}

function scheduleRestart() {
  window.clearTimeout(restartTimer);
  restartTimer = window.setTimeout(() => void start(), 5000);
}

async function start() {
  if (!wanted) return;
  connection ??= build();
  if (connection.state !== HubConnectionState.Disconnected) return;
  try {
    updateConnection({ hub: 'connecting' });
    await connection.start();
    updateConnection({ hub: 'connected' });
    emit('reconnected', null);
    void reportPrinters(connection);
  } catch {
    updateConnection({ hub: 'reconnecting' });
    scheduleRestart();
  }
}

/** Connects to /hubs/pos (after login) and keeps reconnecting until stopped. */
export function startHub(): void {
  wanted = true;
  void start();
}

export async function stopHub(): Promise<void> {
  wanted = false;
  window.clearTimeout(restartTimer);
  const current = connection;
  connection = null;
  await current?.stop();
  updateConnection({ hub: 'off' });
}
