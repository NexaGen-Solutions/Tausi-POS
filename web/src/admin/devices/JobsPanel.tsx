import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatDayTime } from '../../shared/format/dates';
import { t } from '../../shared/i18n/i18n';
import { Button, Field, Modal, SelectField } from '../../shared/ui';
import type { PrinterRow, PrintJobRow } from './types';

/** A printer's failed jobs: retry (keeps its place), send to another printer, discard with a reason. */
export function JobsPanel({ printer, printers }: { printer: PrinterRow; printers: PrinterRow[] }) {
  const queries = useQueryClient();
  const jobs = useQuery({
    queryKey: ['printer-jobs', printer.id],
    queryFn: () => api<PrintJobRow[]>(`/api/admin/printers/${printer.id}/jobs?status=Failed`),
    staleTime: 0,
  });
  const others = printers.filter((p) => p.active && p.id !== printer.id);
  const [target, setTarget] = useState<Record<string, string>>({});
  const [discarding, setDiscarding] = useState<PrintJobRow | null>(null);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);

  const run = async (action: () => Promise<unknown>) => {
    setError(null);
    try {
      await action();
      await queries.invalidateQueries({ queryKey: ['printer-jobs', printer.id] });
      await queries.invalidateQueries({ queryKey: ['printers'] });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : t('common.somethingWrong'));
    }
  };

  const list = jobs.data ?? [];
  return (
    <div className="devices-panel__form">
      {list.length === 0 && jobs.isSuccess && (
        <p className="devices-panel__note">{t('admin.devices.noJobs')}</p>
      )}
      {list.length > 1 && (
        <Button
          onClick={() =>
            void run(() =>
              api(`/api/admin/printers/${printer.id}/jobs/retry-all`, { method: 'POST' }),
            )
          }
        >
          {t('admin.devices.retryAll')}
        </Button>
      )}
      <ul className="devices-jobs">
        {list.map((job) => (
          <li key={job.id}>
            <div>
              <strong>{job.kind}</strong> · {formatDayTime(job.createdAt)} ·{' '}
              {t('admin.devices.attempts', { count: job.attempts })}
              {job.ticketId && (
                <> · {t('admin.devices.ticket', { id: job.ticketId.slice(0, 8) })}</>
              )}
            </div>
            <p className="devices-jobs__error">{job.error}</p>
            <div className="devices-jobs__actions">
              <Button
                onClick={() =>
                  void run(() => api(`/api/admin/print-jobs/${job.id}/retry`, { method: 'POST' }))
                }
              >
                {t('admin.devices.retry')}
              </Button>
              {others.length > 0 && (
                <>
                  <SelectField
                    label={t('admin.devices.sendTo')}
                    value={target[job.id] ?? others[0].id}
                    onChange={(e) => setTarget((m) => ({ ...m, [job.id]: e.target.value }))}
                    options={others.map((p) => ({ value: p.id, label: p.name }))}
                  />
                  <Button
                    onClick={() =>
                      void run(() =>
                        api(`/api/admin/print-jobs/${job.id}/redirect`, {
                          method: 'POST',
                          body: { printerId: target[job.id] ?? others[0].id },
                        }),
                      )
                    }
                  >
                    {t('admin.devices.send')}
                  </Button>
                </>
              )}
              <Button variant="danger" onClick={() => setDiscarding(job)}>
                {t('admin.devices.discard')}
              </Button>
            </div>
          </li>
        ))}
      </ul>
      {error && (
        <p className="devices-panel__error" role="alert">
          {error}
        </p>
      )}
      {discarding && (
        <Modal title={t('admin.devices.discardTitle')} onClose={() => setDiscarding(null)}>
          <form
            className="devices-confirm"
            onSubmit={(e) => {
              e.preventDefault();
              const job = discarding;
              setDiscarding(null);
              void run(() =>
                api(`/api/admin/print-jobs/${job.id}/discard`, {
                  method: 'POST',
                  body: { reason: reason.trim() },
                }),
              ).then(() => setReason(''));
            }}
          >
            <Field
              label={t('admin.devices.discardReason')}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              autoFocus
            />
            <Button
              type="submit"
              variant="danger"
              size="lg"
              block
              disabled={reason.trim().length < 3}
            >
              {t('admin.devices.discard')}
            </Button>
            <Button block onClick={() => setDiscarding(null)}>
              {t('common.cancel')}
            </Button>
          </form>
        </Modal>
      )}
    </div>
  );
}
