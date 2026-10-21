import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { ApiError, api } from '../../shared/api/client';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button, useToast } from '../../shared/ui';

/** GET /api/pos/tickets/settled: a ticket settled in the current work period. */
export interface SettledTicket {
  id: string;
  receiptNumber: number;
  number: number;
  where: string;
  total: number;
  paymentTypes: string[];
  cashier: string;
  settledAt: string;
}

const typeLabel: Record<string, string> = { Cash: 'Cash', Mpesa: 'M-Pesa', Card: 'Card' };
const time = (iso: string) =>
  new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

/** The cashier's Settled tab: today's receipts, newest first, each with Reprint receipt (COPY). */
export function SettledList({ currency, disabled }: { currency: string; disabled: boolean }) {
  const toast = useToast();
  const [busy, setBusy] = useState<string | null>(null);
  const settled = useQuery({
    queryKey: ['settled'],
    queryFn: () => api<SettledTicket[]>('/api/pos/tickets/settled'),
    staleTime: 0,
  });

  const reprint = async (ticket: SettledTicket) => {
    setBusy(ticket.id);
    try {
      // Needs ticket.reprint: the approval pad asks for a supervisor's PIN when required.
      await api(`/api/pos/tickets/${ticket.id}/receipt/reprint`, { method: 'POST' });
      toast(t('pos.settled.reprinted', { number: ticket.receiptNumber }));
    } catch (error) {
      toast(error instanceof ApiError ? error.message : t('common.somethingWrong'), {
        tone: 'warning',
      });
    } finally {
      setBusy(null);
    }
  };

  const list = settled.data ?? [];
  return (
    <section className="map-takeaway" aria-label={t('pos.settled.title')}>
      {list.length === 0 ? (
        <p className="map-empty">{t('pos.settled.none')}</p>
      ) : (
        <ul className="map-takeaway__list">
          {list.map((ticket) => (
            <li key={ticket.id} className="map-settled__row">
              <span className="map-takeaway__number">
                {t('pos.settled.receipt', { number: ticket.receiptNumber })}
              </span>
              <span>
                #{ticket.number} · {ticket.where}
              </span>
              <span className="map-takeaway__total">{formatMoney(ticket.total, currency)}</span>
              <span>{ticket.paymentTypes.map((p) => typeLabel[p] ?? p).join(' + ')}</span>
              <span>{ticket.cashier}</span>
              <span className="map-takeaway__age">{time(ticket.settledAt)}</span>
              <Button
                disabled={disabled || busy === ticket.id}
                onClick={() => void reprint(ticket)}
              >
                {t('pos.settled.reprint')}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
