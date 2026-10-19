/** The PIN-login session on this terminal. Kept for the browser tab only. */
export interface PinSession {
  token: string;
  expiresAt: string;
  user: SessionUser;
}

export interface SessionUser {
  id: string;
  name: string;
  role: string;
  terminalId: string | null;
  terminalName: string | null;
}

const storageKey = 'nexagen.session';
type Listener = (session: PinSession | null) => void;
const listeners = new Set<Listener>();

function read(): PinSession | null {
  try {
    const raw = sessionStorage.getItem(storageKey);
    if (!raw) return null;
    const session = JSON.parse(raw) as PinSession;
    return new Date(session.expiresAt) > new Date() ? session : null;
  } catch {
    return null;
  }
}

let current: PinSession | null = read();

export function pinSession(): PinSession | null {
  return current;
}

export function setPinSession(session: PinSession | null): void {
  current = session;
  if (session) sessionStorage.setItem(storageKey, JSON.stringify(session));
  else sessionStorage.removeItem(storageKey);
  listeners.forEach((listener) => listener(session));
}

export function onPinSessionChange(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
