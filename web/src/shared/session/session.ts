/** The PIN-login session on this terminal. Kept for the browser tab only. */
export interface PinSession {
  token: string;
  expiresAt: string;
  idleLogoutMinutes: number;
  mustChangePin: boolean;
  user: SessionUser;
}

export interface SessionUser {
  id: string;
  name: string;
  role: string;
  terminalId: string | null;
  terminalName: string | null;
}

export type SessionEndReason = 'logout' | 'idle' | 'expired';

export interface LogoutNotice {
  reason: SessionEndReason;
  idleLogoutMinutes: number;
}

const storageKey = 'tausi.session';
/** The key used before the rename to Tausi POS; a session open during the upgrade is kept. */
const oldStorageKey = 'nexagen.session';
type Listener = (session: PinSession | null) => void;
const listeners = new Set<Listener>();

function read(): PinSession | null {
  try {
    const old = sessionStorage.getItem(oldStorageKey);
    if (old !== null && sessionStorage.getItem(storageKey) === null)
      sessionStorage.setItem(storageKey, old);
    sessionStorage.removeItem(oldStorageKey);
    const raw = sessionStorage.getItem(storageKey);
    if (!raw) return null;
    const session = JSON.parse(raw) as PinSession;
    return new Date(session.expiresAt) > new Date() ? session : null;
  } catch {
    return null;
  }
}

let current: PinSession | null = read();
let notice: LogoutNotice | null = null;

/**
 * When the server last saw this session (the time the last authenticated request was sent).
 * The server's idle timer restarts on each request, so counting from here logs out on screen
 * no later than the server does.
 */
let lastServerTouch = Date.now();

export function pinSession(): PinSession | null {
  return current;
}

export function setPinSession(session: PinSession | null): void {
  current = session;
  if (session) {
    sessionStorage.setItem(storageKey, JSON.stringify(session));
    lastServerTouch = Date.now();
  } else {
    sessionStorage.removeItem(storageKey);
  }
  listeners.forEach((listener) => listener(session));
}

/** Ends the session on this screen. The login screen shows a note for an idle logout. */
export function endPinSession(reason: SessionEndReason): void {
  if (!current) return;
  notice = { reason, idleLogoutMinutes: current.idleLogoutMinutes };
  setPinSession(null);
}

/** The note about the last logout, given once. */
export function takeLogoutNotice(): LogoutNotice | null {
  const taken = notice;
  notice = null;
  return taken;
}

export function onPinSessionChange(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function touchSession(at = Date.now()): void {
  lastServerTouch = at;
}

export function lastSessionTouch(): number {
  return lastServerTouch;
}

/** The moment this session goes idle on the server, in ms since the epoch. */
export function idleDeadline(): number | null {
  return current ? lastServerTouch + current.idleLogoutMinutes * 60_000 : null;
}
