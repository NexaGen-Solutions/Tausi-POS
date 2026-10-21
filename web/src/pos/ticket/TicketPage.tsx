import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { api } from '../../shared/api/client';
import type { Brand } from '../../shared/api/types';
import { OfflineBanner } from '../../shared/connection/OfflineBanner';
import { onPosEvent } from '../../shared/connection/hub';
import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Banner, Button, TopBar } from '../../shared/ui';
import { tableLabel } from '../tables/tableState';
import type { TableEvent, TableMap, TicketDetail } from '../tables/types';
import { tableMapKey } from '../tables/useTableMap';
import { usePinSession } from '../usePinSession';
import { useTicketLock } from './useTicketLock';
import '../tables/tables.css';

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

  const brand = useQuery({ queryKey: ['brand'], queryFn: () => api<Brand>('/api/brand') });
  const ticket = useQuery({
    queryKey: ['ticket', id],
    queryFn: () => api<TicketDetail>(`/api/pos/tickets/${id}`),
    enabled: !isNew,
    staleTime: 0,
  });
  const currency = brand.data?.currency ?? 'KES';

  const lock = useTicketLock(isNew ? null : id);
  useEffect(
    () =>
      onPosEvent('ticket.changed', (payload) => {
        if ((payload as TableEvent).ticketId === id)
          void queries.invalidateQueries({ queryKey: ['ticket', id] });
      }),
    [id, queries],
  );

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
      </main>
    </div>
  );
}
