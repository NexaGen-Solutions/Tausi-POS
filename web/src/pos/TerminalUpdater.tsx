import { useEffect, useRef } from 'react';
import { checkForTerminalUpdate, installTerminalUpdate, isTauri } from '../shared/device/device';
import { serverVersion } from '../shared/connection/serverState';
import { onPinSessionChange, pinSession } from '../shared/session/session';

export const CHECK_EVERY = 6 * 60 * 60 * 1000;
export const IDLE_BEFORE_INSTALL = 5 * 60 * 1000;

/**
 * Terminal app updates come from the server (not the internet), never newer than the server. It
 * checks on start and every 6 hours and downloads in the background, and installs only while the
 * PIN screen shows and nobody has been logged in for 5 minutes (or at the next start). Never mid-order.
 */
export function TerminalUpdater({ now = () => Date.now() }: { now?: () => number }) {
  const ready = useRef<string | null>(null);
  const lastSession = useRef(now());

  useEffect(() => {
    if (!isTauri()) return;
    const off = onPinSessionChange(() => {
      lastSession.current = now();
    });
    const check = async () => {
      const server = serverVersion();
      if (!server) return;
      try {
        ready.current = (await checkForTerminalUpdate(server)) ?? ready.current;
      } catch {
        // No update source (an older server) or no connection: try again later.
      }
    };
    const maybeInstall = () => {
      if (!ready.current || pinSession()) return;
      if (now() - lastSession.current < IDLE_BEFORE_INSTALL) return;
      ready.current = null;
      void installTerminalUpdate().catch(() => {});
    };
    const first = window.setTimeout(() => void check(), 30_000);
    const checks = window.setInterval(() => void check(), CHECK_EVERY);
    const installs = window.setInterval(maybeInstall, 60_000);
    return () => {
      off();
      window.clearTimeout(first);
      window.clearInterval(checks);
      window.clearInterval(installs);
    };
  }, [now]);

  return null;
}
