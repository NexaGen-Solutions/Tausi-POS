import { deviceToken } from '../device/device';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { t } from '../i18n/i18n';
import { Banner, useToast } from '../ui';
import { api } from '../api/client';
import { endPinSession, pinSession, setIdleLogoutMinutes } from '../session/session';
import { onPosEvent, startHub, stopHub } from './hub';
import { setServerMode } from './serverState';

interface PrintFailed {
  printJobId: string;
  printerName: string;
  kind: string;
  ticketId: string | null;
}

/** The order screen shows its own ticket's failures with Retry, so they aren't repeated here. */
const onItsOrderScreen = (job: PrintFailed) =>
  job.ticketId !== null && window.location.pathname === `/pos/ticket/${job.ticketId}`;

/**
 * Keeps the live-update connection open while mounted (inside signed-in screens), refreshes
 * cached data when events arrive, and shows a banner when a slip fails to print.
 */
export function LiveUpdates() {
  const queries = useQueryClient();
  const [failed, setFailed] = useState<PrintFailed[]>([]);
  const toast = useToast();
  const logoutTimer = useRef<number | undefined>(undefined);

  useEffect(() => {
    startHub();
    const unsubscribe = [
      onPosEvent('settings.changed', () => {
        void queries.invalidateQueries({ queryKey: ['brand'] });
        void queries.invalidateQueries({ queryKey: ['settings'] });
        void queries.invalidateQueries({ queryKey: ['device'] });
        // The idle logout may have changed: count down with what the server now uses.
        if (pinSession())
          api<{ idleLogoutMinutes: number | null }>('/api/auth/session')
            .then((status) => setIdleLogoutMinutes(status.idleLogoutMinutes))
            .catch(() => {});
      }),
      // Buttons appear or disappear at once; the server checks every request anyway.
      onPosEvent('permissions.changed', () => {
        void queries.invalidateQueries({ queryKey: ['me'] });
        void queries.invalidateQueries({ queryKey: ['permissions'] });
      }),
      onPosEvent('printer.status', () => {
        void queries.invalidateQueries({ queryKey: ['health'] });
        void queries.invalidateQueries({ queryKey: ['printers'] });
      }),
      onPosEvent(
        'terminal.status',
        () => void queries.invalidateQueries({ queryKey: ['terminals'] }),
      ),
      onPosEvent('day.opened', () => void queries.invalidateQueries({ queryKey: ['device'] })),
      onPosEvent('day.closed', (payload) => {
        void queries.invalidateQueries({ queryKey: ['device'] });
        void queries.invalidateQueries({ queryKey: ['table-map'] });
        // A 60-second warning, then everyone is logged out (the server ends the sessions too).
        const logoutAt = (payload as { logoutAt?: string } | null)?.logoutAt;
        if (!logoutAt || !pinSession()) return;
        toast(t('pos.day.closingWarning'), { tone: 'warning' });
        window.clearTimeout(logoutTimer.current);
        logoutTimer.current = window.setTimeout(
          () => endPinSession('logout'),
          Math.max(0, Date.parse(logoutAt) - Date.now()),
        );
      }),
      onPosEvent('menu.changed', () => void queries.invalidateQueries({ queryKey: ['pos-menu'] })),
      onPosEvent('stock.changed', () => {
        void queries.invalidateQueries({ queryKey: ['stock'] });
        void queries.invalidateQueries({ queryKey: ['deliveries'] });
      }),
      onPosEvent(
        'licence.changed',
        () => void queries.invalidateQueries({ queryKey: ['licence'] }),
      ),
      // A backup is being restored: everything stops until it is done, then reloads.
      onPosEvent('server.restoring', () => setServerMode('restoring')),
      onPosEvent('server.restored', () => location.reload()),
      // A paired terminal goes to its PIN screen when the server's setup is finished.
      onPosEvent('setup.completed', () => {
        if (deviceToken() && !location.pathname.startsWith('/setup')) location.assign('/pos');
      }),
      onPosEvent('money.changed', () => {
        void queries.invalidateQueries({ queryKey: ['cashbook'] });
        void queries.invalidateQueries({ queryKey: ['accounts'] });
        void queries.invalidateQueries({ queryKey: ['awaiting'] });
        void queries.invalidateQueries({ queryKey: ['stock', 'suppliers'] });
      }),
      onPosEvent('print.failed', (payload) => {
        const job = payload as PrintFailed;
        if (!onItsOrderScreen(job)) setFailed((list) => [...list, job]);
      }),
      // After a reconnect anything may have changed while we were away.
      onPosEvent('reconnected', () => void queries.invalidateQueries()),
    ];
    return () => {
      unsubscribe.forEach((off) => off());
      void stopHub();
    };
  }, [queries, toast]);

  return (
    <>
      {failed.map((job) => (
        <Banner
          key={job.printJobId}
          action={{
            label: t('common.close'),
            onClick: () => setFailed((list) => list.filter((j) => j.printJobId !== job.printJobId)),
          }}
        >
          {t('connection.printFailed', { printer: job.printerName })} ·{' '}
          {t('connection.printFailedDetail', { kind: job.kind.toLowerCase() })}
        </Banner>
      ))}
    </>
  );
}
