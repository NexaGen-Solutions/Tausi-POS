import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { t } from '../i18n/i18n';
import { Banner, useToast } from '../ui';
import { endPinSession, pinSession } from '../session/session';
import { onPosEvent, startHub, stopHub } from './hub';

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
