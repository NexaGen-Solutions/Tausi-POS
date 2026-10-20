import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { api } from '../../shared/api/client';
import type { Brand } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { onPosEvent } from '../../shared/connection/hub';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, TopBar } from '../../shared/ui';
import { tableLabel } from '../tables/tableState';
import type { LockState, TableEvent, TableMap, TicketDetail } from '../tables/types';
import { tableMapKey } from '../tables/useTableMap';
import { TestMenu } from '../TestMenu';
import { usePinSession } from '../usePinSession';
import '../tables/tables.css';

/** Terminals renew their edit lock this often while a ticket is on screen (the server drops it after 90 s). */
export const lockRenewMs = 30_000;

/**
 * The order screen's place (Task 6 builds it): the header, the lines read-only and Back to tables.
 * It already takes the edit lock, renews it, and gives it back on leaving; a ticket open on another
 * terminal is shown read-only until that terminal leaves it.
 */
export function TicketPage() {
  const { id = 'new' } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const queries = useQueryClient();
  const session = usePinSession();
  const isNew = id === 'new';
  const tableId = params.get('table');
  const [lock, setLock] = useState<LockState | null>(null);

  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const ticket = useQuery({
    queryKey: ['ticket', id],
    queryFn: () => api<TicketDetail>(`/api/pos/tickets/${id}`),
    enabled: !isNew,
    staleTime: 0,
  });
  const currency = brand.data?.currency ?? 'KES';

  // Take the lock, renew it while here, give it back on leaving. If someone else has it, try
  // again when they leave (ticket.lock with no holder).
  useEffect(() => {
    if (isNew) return;
    let alive = true;
    let editable = false;
    const take = async () => {
      try {
        const state = await api<LockState>(`/api/pos/tickets/${id}/lock`, { method: 'POST' });
        if (!alive) return;
        editable = state.editable;
        setLock(state);
      } catch {
        // Offline: the banner shows; we try again on the next renewal.
      }
    };
    void take();
    const renew = window.setInterval(() => void take(), lockRenewMs);
    const off = [
      onPosEvent('ticket.lock', (payload) => {
        const event = payload as TableEvent;
        if (event.ticketId === id && !event.terminalId && !editable) void take();
      }),
      onPosEvent('ticket.changed', (payload) => {
        if ((payload as TableEvent).ticketId === id)
          void queries.invalidateQueries({ queryKey: ['ticket', id] });
      }),
    ];
    return () => {
      alive = false;
      window.clearInterval(renew);
      off.forEach((unsubscribe) => unsubscribe());
      if (editable)
        void api(`/api/pos/tickets/${id}/lock`, { method: 'DELETE' }).catch(() => undefined);
    };
  }, [id, isNew, queries]);

  const map = queries.getQueryData<TableMap>(tableMapKey);
  const tableName =
    ticket.data?.tableName ??
    (tableId ? map?.tables.find((table) => table.id === tableId)?.name : undefined);
  const title =
    params.get('takeaway') || (ticket.data && !ticket.data.tableId)
      ? t('pos.ticket.takeaway')
      : tableName
        ? tableLabel(tableName)
        : '';
  const heading = isNew
    ? t('pos.ticket.newTicket')
    : ticket.data
      ? t('pos.ticket.number', { number: ticket.data.number })
      : '';
  const lines = ticket.data?.lines ?? [];

  return (
    <div className="pos-screen">
      <TopBar
        start={
          <Button variant="ghost-dark" onClick={() => navigate('/pos/tables')}>
            {t('pos.backToTables')}
          </Button>
        }
        title={title}
        subtitle={[heading, session?.user.name].filter(Boolean).join(' · ')}
      />
      <OfflineBanner />
      {lock && !lock.editable && lock.message && <Banner tone="info">{lock.message}</Banner>}
      {ticket.data && ticket.data.status !== 'Open' && <Banner>{t('pos.ticket.notOpen')}</Banner>}
      <main className="ticket-placeholder">
        <section className="ticket-placeholder__lines" aria-label={t('pos.ticket.linesTitle')}>
          {lines.length === 0 ? (
            <p className="map-empty">{t('pos.ticket.noLines')}</p>
          ) : (
            <table className="ticket-lines">
              <thead>
                <tr>
                  <th scope="col">{t('pos.ticket.colQty')}</th>
                  <th scope="col">{t('pos.ticket.colItem')}</th>
                  <th scope="col">{t('pos.ticket.colBy')}</th>
                  <th scope="col" className="ticket-lines__amount">
                    {t('pos.ticket.colAmount')}
                  </th>
                </tr>
              </thead>
              <tbody>
                {lines.map((line) => (
                  <tr key={line.id} className={line.voided ? 'ticket-lines__voided' : undefined}>
                    <td className="ticket-lines__qty">{line.quantity}</td>
                    <td>
                      {line.name}
                      {line.options ? ` (${line.options})` : ''}
                      {line.voided && ` · ${t('pos.ticket.voided')}`}
                      {!line.printed && !line.voided && ` · ${t('pos.ticket.new')}`}
                    </td>
                    <td>
                      {line.addedBy} · {line.addedOn}
                    </td>
                    <td className="ticket-lines__amount">{formatMoney(line.gross, currency)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <th scope="row" colSpan={3}>
                    {t('pos.ticket.total')}
                  </th>
                  <td className="ticket-lines__amount">
                    {formatMoney(ticket.data?.total ?? 0, currency)}
                  </td>
                </tr>
              </tfoot>
            </table>
          )}
          <p className="map-empty">{t('pos.ticket.placeholder')}</p>
        </section>
        {/* TEMPORARY: the read-only menu check moves here until the order screen (Task 6) replaces it. */}
        <TestMenu />
      </main>
    </div>
  );
}
