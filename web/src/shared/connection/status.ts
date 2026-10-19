/**
 * Whether the server can be reached. The API client reports every request's outcome here, the
 * health poller checks while offline, and the live-update hub reports its own state.
 */
export interface ConnectionState {
  apiReachable: boolean;
  hub: 'off' | 'connecting' | 'connected' | 'reconnecting';
  serverName: string | null;
}

type Listener = (state: ConnectionState) => void;

let state: ConnectionState = { apiReachable: true, hub: 'off', serverName: null };
const listeners = new Set<Listener>();

export function connectionState(): ConnectionState {
  return state;
}

export function updateConnection(change: Partial<ConnectionState>): void {
  const next = { ...state, ...change };
  if (
    next.apiReachable === state.apiReachable &&
    next.hub === state.hub &&
    next.serverName === state.serverName
  )
    return;
  state = next;
  listeners.forEach((listener) => listener(state));
}

export function onConnectionChange(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function isOffline(s: ConnectionState): boolean {
  return !s.apiReachable || s.hub === 'reconnecting';
}
