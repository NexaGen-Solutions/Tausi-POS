import { formatMoney } from '../../shared/format/money';
import { t } from '../../shared/i18n/i18n';
import { Button } from '../../shared/ui';
import { age, tableLabel } from './tableState';
import type { TableTile, TicketSummary } from './types';

/** The right panel of the table map: the selected table's tickets and what can be done with them. */
export function TablePanel({
  table,
  chosen,
  now,
  currency,
  canAdd,
  canMove,
  canMerge,
  disabled,
  onChoose,
  onOpen,
  onNew,
  onMerge,
  onMove,
}: {
  table: TableTile | null;
  chosen: TicketSummary | null;
  now: number;
  currency: string;
  canAdd: boolean;
  canMove: boolean;
  canMerge: boolean;
  disabled: boolean;
  onChoose: (ticket: TicketSummary) => void;
  onOpen: (ticket: TicketSummary) => void;
  onNew: () => void;
  onMerge: () => void;
  onMove: (ticket: TicketSummary) => void;
}) {
  if (!table)
    return (
      <aside className="map-panel" aria-label={t('pos.map.selected', { table: '' })}>
        <p className="map-panel__hint">{t('pos.map.selectHint')}</p>
        <p className="map-panel__note">{t('pos.map.liveNote')}</p>
      </aside>
    );

  const label = tableLabel(table.name);
  const tickets = table.tickets;
  return (
    <aside className="map-panel" aria-label={t('pos.map.selected', { table: label })}>
      <p className="map-panel__eyebrow">{t('pos.map.selected', { table: label })}</p>
      {tickets.length === 0 ? (
        <>
          <div className="map-panel__card">
            <p className="map-panel__heading">{t('pos.map.tableFree', { table: label })}</p>
          </div>
          {canAdd && (
            <Button variant="primary" size="lg" block disabled={disabled} onClick={onNew}>
              {t('pos.map.newTicket')}
            </Button>
          )}
        </>
      ) : (
        <>
          <div className="map-panel__card">
            <p className="map-panel__heading">
              {tickets.length === 1
                ? t('pos.map.openTicket')
                : t('pos.map.openTickets', { count: tickets.length })}
            </p>
            <ul className="map-panel__tickets">
              {tickets.map((ticket) => (
                <li key={ticket.id}>
                  <button
                    type="button"
                    className={`map-panel__ticket${chosen?.id === ticket.id ? ' map-panel__ticket--chosen' : ''}`}
                    aria-pressed={chosen?.id === ticket.id}
                    onClick={() => onChoose(ticket)}
                  >
                    {t('pos.map.ticketLine', {
                      number: ticket.number,
                      waiter: ticket.openedBy,
                      total: formatMoney(ticket.total, currency),
                      age: age(ticket.openedAt, now),
                    })}
                  </button>
                </li>
              ))}
            </ul>
          </div>
          <Button
            variant="primary"
            size="lg"
            block
            disabled={disabled || !chosen}
            onClick={() => chosen && onOpen(chosen)}
          >
            {t('pos.map.openTheTicket')}
          </Button>
          {canAdd && (
            <Button size="lg" block disabled={disabled} onClick={onNew}>
              {t('pos.map.newOnTable')}
            </Button>
          )}
          {canMerge && tickets.length >= 2 && (
            <Button size="lg" block disabled={disabled} onClick={onMerge}>
              {t('pos.map.merge')}
            </Button>
          )}
          {canMove && (
            <Button
              size="lg"
              block
              disabled={disabled || !chosen}
              onClick={() => chosen && onMove(chosen)}
            >
              {t('pos.map.move')}
            </Button>
          )}
        </>
      )}
      <p className="map-panel__note">{t('pos.map.liveNote')}</p>
    </aside>
  );
}
