import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { api } from '../../shared/api/client';
import { onPosEvent } from '../../shared/connection/hub';
import { formatAmount } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import type { ReceivedPayment } from './types';
import './mpesa.css';

const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/**
 * Board 24: M-Pesa payments received and not yet on a bill (last 24 hours), live. A QR payment for
 * this bill is highlighted; the same amount is only a suggestion. Add to this bill uses up to what
 * the bill still owes.
 */
export function ReceivedList({
  ticketId,
  disabled,
  onAdd,
}: {
  ticketId: string;
  disabled: boolean;
  onAdd: (payment: ReceivedPayment) => void;
}) {
  const queries = useQueryClient();
  const key = ['mpesa-received', ticketId];
  const list = useQuery({
    queryKey: key,
    queryFn: () => api<ReceivedPayment[]>(`/api/pos/mpesa/tickets/${ticketId}/received`),
    staleTime: 0,
  });
  useEffect(
    () =>
      onPosEvent(
        'mpesa.received',
        () => void queries.invalidateQueries({ queryKey: ['mpesa-received', ticketId] }),
      ),
    [queries, ticketId],
  );
  const rows = list.data ?? [];

  return (
    <section className="mpesa-received" aria-label={t('pos.mpesa.receivedTitle')}>
      <h3 className="mpesa-received__title">
        {t('pos.mpesa.receivedTitle')}{' '}
        <span className="mpesa-help">{t('pos.mpesa.receivedLive')}</span>
      </h3>
      {rows.length === 0 ? (
        <p className="mpesa-help">{t('pos.mpesa.noneReceived')}</p>
      ) : (
        <div className="mpesa-received__scroll">
          <table className="mpesa-table">
            <thead>
              <tr>
                <th scope="col">{t('pos.mpesa.time')}</th>
                <th scope="col">{t('pos.mpesa.from')}</th>
                <th scope="col">{t('pos.mpesa.code')}</th>
                <th scope="col" className="mpesa-num">
                  {t('pos.mpesa.amount')}
                </th>
                <th scope="col">{t('pos.mpesa.matches')}</th>
                <th scope="col">
                  <span className="visually-hidden">{t('pos.mpesa.add')}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr
                  key={r.id}
                  className={r.match === 'QrOnThisBill' ? 'mpesa-row--match' : undefined}
                >
                  <td className="mpesa-mono">{time(r.at)}</td>
                  <td>{[r.payerName, r.phone].filter(Boolean).join(' · ')}</td>
                  <td className="mpesa-mono">{r.code}</td>
                  <td className="mpesa-num mpesa-mono">
                    {formatAmount(r.available)}
                    {r.available !== r.amount && (
                      <span className="mpesa-help">
                        {' '}
                        {t('pos.mpesa.ofAmount', { amount: formatAmount(r.amount) })}
                      </span>
                    )}
                  </td>
                  <td>
                    {r.match === 'QrOnThisBill' ? (
                      <span className="mpesa-badge mpesa-badge--qr">
                        {t('pos.mpesa.qrOnThisBill')}
                      </span>
                    ) : r.match === 'SameAmount' ? (
                      <span className="mpesa-badge">{t('pos.mpesa.sameAmount')}</span>
                    ) : (
                      <span className="mpesa-help">{t('pos.mpesa.paidDirectly')}</span>
                    )}
                  </td>
                  <td>
                    <Button
                      variant={r.match === 'QrOnThisBill' ? 'primary' : 'secondary'}
                      disabled={disabled}
                      onClick={() => onAdd(r)}
                    >
                      {t('pos.mpesa.add')}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="mpesa-help">{t('pos.mpesa.receivedNote')}</p>
    </section>
  );
}
