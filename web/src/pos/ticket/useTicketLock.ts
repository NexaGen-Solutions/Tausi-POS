import { useEffect, useState } from 'react';
import { api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { isTauri } from '../../shared/device/device';
import type { LockState, TableEvent } from '../tables/types';

/** Terminals renew their edit lock this often while a ticket is on screen (the server drops it after 90 s). */
export const lockRenewMs = 30_000;

/**
 * Holds a ticket's edit lock while it is on screen: takes it, renews it every 30 seconds, and
 * gives it back on leaving, when the tab closes or reloads (pagehide), or when the terminal app's
 * window closes. If another terminal has it, the ticket is read-only until they leave it.
 * `onReleasedElsewhere` runs when that happens, e.g. to refetch the ticket.
 */
export function useTicketLock(ticketId: string | null): LockState | null {
  const [lock, setLock] = useState<LockState | null>(null);

  useEffect(() => {
    if (!ticketId) return;
    let alive = true;
    let editable = false;
    const take = async () => {
      try {
        const state = await api<LockState>(`/api/pos/tickets/${ticketId}/lock`, { method: 'POST' });
        if (!alive) return;
        editable = state.editable;
        setLock(state);
      } catch {
        // Offline: the banner shows; we try again on the next renewal.
      }
    };
    // keepalive lets the request finish while the page unloads, with the device and session headers
    // (navigator.sendBeacon can't send those).
    const release = () => {
      if (!editable) return Promise.resolve();
      editable = false;
      return api(`/api/pos/tickets/${ticketId}/lock`, { method: 'DELETE', keepalive: true }).catch(
        () => undefined,
      );
    };

    void take();
    const renew = window.setInterval(() => void take(), lockRenewMs);
    const onPageHide = () => void release();
    window.addEventListener('pagehide', onPageHide);
    const off = onPosEvent('ticket.lock', (payload) => {
      const event = payload as TableEvent;
      if (event.ticketId === ticketId && !event.terminalId && !editable) void take();
    });

    // The terminal app: give the lock back before the window closes (at most a second's wait).
    let stopWatchingClose: (() => void) | undefined;
    if (isTauri()) {
      void import('@tauri-apps/api/window')
        .then(({ getCurrentWindow }) =>
          getCurrentWindow().onCloseRequested(() =>
            Promise.race([
              release(),
              new Promise((resolve) => window.setTimeout(resolve, 1000)),
            ]).then(() => undefined),
          ),
        )
        .then((unlisten) => {
          if (alive) stopWatchingClose = unlisten;
          else unlisten();
        })
        .catch(() => undefined);
    }

    return () => {
      alive = false;
      window.clearInterval(renew);
      window.removeEventListener('pagehide', onPageHide);
      off();
      stopWatchingClose?.();
      void release();
    };
  }, [ticketId]);

  return ticketId ? lock : null;
}
