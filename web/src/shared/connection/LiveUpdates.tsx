import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { t } from '../i18n/i18n';
import { Banner } from '../ui';
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
      onPosEvent('day.closed', () => {
        void queries.invalidateQueries({ queryKey: ['device'] });
        void queries.invalidateQueries({ queryKey: ['table-map'] });
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
  }, [queries]);

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
