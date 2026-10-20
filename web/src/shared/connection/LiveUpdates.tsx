import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { t } from '../i18n/i18n';
import { Banner } from '../ui';
import { onPosEvent, startHub, stopHub } from './hub';

interface PrintFailed {
  printJobId: string;
  printerName: string;
  kind: string;
}

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
      onPosEvent('menu.changed', () => void queries.invalidateQueries({ queryKey: ['pos-menu'] })),
      onPosEvent('print.failed', (payload) =>
        setFailed((list) => [...list, payload as PrintFailed]),
      ),
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
