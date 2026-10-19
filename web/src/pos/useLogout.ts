import { useCallback } from 'react';
import { api } from '../shared/api/client';
import { endPinSession } from '../shared/session/session';

/** Ends the session on the server (audited) and returns to the PIN pad, even if offline. */
export function useLogout() {
  return useCallback(async () => {
    try {
      await api('/api/auth/logout', { method: 'POST' });
    } catch {
      // Offline or already ended: the server session times out on its own.
    } finally {
      endPinSession('logout');
    }
  }, []);
}
