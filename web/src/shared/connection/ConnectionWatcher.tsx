import { useEffect } from 'react';
import { apiUrl } from '../api/client';
import { connectionState, onConnectionChange, updateConnection } from './status';

interface Health {
  server: string;
}

/**
 * Checks /api/health: every 3 seconds while the server is unreachable (so the offline banner
 * clears as soon as it is back), every 20 seconds otherwise.
 */
export function ConnectionWatcher() {
  useEffect(() => {
    let timer: number | undefined;
    let stopped = false;

    const check = async () => {
      try {
        const response = await fetch(apiUrl('/api/health'), { cache: 'no-store' });
        if (!response.ok) throw new Error(String(response.status));
        const health = (await response.json()) as Health;
        updateConnection({ apiReachable: true, serverName: health.server });
      } catch {
        updateConnection({ apiReachable: false });
      }
      schedule();
    };

    const schedule = () => {
      if (stopped) return;
      window.clearTimeout(timer);
      timer = window.setTimeout(check, connectionState().apiReachable ? 20_000 : 3_000);
    };

    // When a request fails, check again soon rather than waiting for the long interval.
    const unsubscribe = onConnectionChange(schedule);
    void check();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      unsubscribe();
    };
  }, []);

  return null;
}
